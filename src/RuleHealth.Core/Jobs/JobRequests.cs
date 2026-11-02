using RuleHealth.Core.Auth;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Jobs;

/// <summary>Body of POST /api/tenants/{alias}/jobs.</summary>
/// <param name="Kind">collect | extract | refresh (collect then extract) | validate</param>
/// <param name="Days">collect: incident window; validate: validation window (1–30). Null = tenant defaults.</param>
/// <param name="FreshSignIn">Newly issued tokens ("Sign in again"), e.g. right after activating a PIM role.</param>
public sealed record JobRequest(string Kind, int? Days = null, bool SaveRaw = false, bool IncludeBasic = false, bool FreshSignIn = false);

/// <summary>Turns a job request into the engine operation it runs. Same operations as the CLI verbs.</summary>
public static class JobRequests
{
    public const string Collect = "collect";
    public const string Extract = "extract";
    public const string Refresh = "refresh";
    public const string Validate = "validate";

    public static readonly IReadOnlyList<string> Kinds = new[] { Collect, Extract, Refresh, Validate };

    public static Func<Action<string>, IProgress<JobStage>, CancellationToken, Task<string>> Work(
        JsonStore store, TokenStore tokens, string alias, JobRequest request)
    {
        CollectOptions Options() => new(request.Days, SaveRaw: request.SaveRaw, FreshSignIn: request.FreshSignIn);

        switch (request.Kind)
        {
            case Collect:
                return (log, stages, token) =>
                {
                    CollectStages.Announce(stages, withValueChecks: false);
                    return CollectOperation.RunAsync(store, tokens, alias, Options(), log, stages, null, token);
                };

            case Extract:
                return (log, stages, _) => Task.FromResult(ExtractStage(store, alias, log, stages, failJob: true));

            case Refresh:
                return async (log, stages, token) =>
                {
                    CollectStages.Announce(stages, withValueChecks: true);
                    var collected = await CollectOperation.RunAsync(store, tokens, alias, Options(), log, stages, null, token);
                    token.ThrowIfCancellationRequested();
                    log("");
                    log("extracting value checks from the collected rules...");
                    // The collected files are written already: a failing extraction (no schema) fails its stage, not the job.
                    var extracted = ExtractStage(store, alias, log, stages, failJob: false);
                    return $"{collected}. {extracted}.";
                };

            case Validate:
                return (log, _, token) => ValidateOperation.RunAsync(store, tokens, alias,
                    new ValidateOptions(request.Days, request.IncludeBasic, request.SaveRaw, request.FreshSignIn), log, null, token);

            default:
                throw new RuleHealthException($"Unknown job kind '{request.Kind}'. Use {string.Join(", ", Kinds)}.") { HttpStatus = 400 };
        }
    }

    private static string ExtractStage(JsonStore store, string alias, Action<string> log, IProgress<JobStage> stages, bool failJob)
    {
        stages.Report(new JobStage(CollectStages.ValueChecks, StageStates.Running));
        try
        {
            var (summary, checks) = ExtractOperation.Execute(store, alias, null, null, log);
            stages.Report(new JobStage(CollectStages.ValueChecks, StageStates.Succeeded, $"{checks} value checks", checks));
            return summary;
        }
        catch (RuleHealthException ex) when (!failJob)
        {
            log($"{CollectStages.ValueChecks}: {ex.Message}");
            stages.Report(new JobStage(CollectStages.ValueChecks, StageStates.Failed, ex.Message.Split('\n')[0]));
            return "Value checks not extracted";
        }
    }
}
