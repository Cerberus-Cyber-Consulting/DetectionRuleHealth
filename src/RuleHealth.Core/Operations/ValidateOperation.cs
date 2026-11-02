using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;
using RuleHealth.Core.Validation;

namespace RuleHealth.Core.Operations;

/// <param name="Days">Validation window 1–30; null = tenant.json defaults.validationDays (30).</param>
/// <param name="IncludeBasic">Query Basic/Auxiliary tables through /search (billed per GB scanned).</param>
/// <param name="SaveRaw">Save every response body under data/&lt;alias&gt;/raw/.</param>
public sealed record ValidateOptions(int? Days = null, bool IncludeBasic = false, bool SaveRaw = false, bool FreshSignIn = false);

/// <summary>The plan plus remarks worth showing before running it (stale extraction).</summary>
public sealed record PreparedValidation(TenantDocument Tenant, ValidationPlan Plan, int Selected, IReadOnlyList<string> Notes);

/// <summary>
/// validate: run every check marked "validate" in selections.json → validation.json.
/// <see cref="Prepare"/> is the dry run (no sign-in, no IO beyond reading the tenant files): the CLI
/// prints it, the UI shows it as "Review queries".
/// </summary>
public static class ValidateOperation
{
    public static PreparedValidation Prepare(JsonStore store, string alias, ValidateOptions options)
    {
        var tenant = store.TryRead<TenantDocument>(alias, Documents.Tenant)
            ?? throw new RuleHealthException($"No tenant '{alias}' in {store.DataRoot}. Add it in the UI first.");

        var days = options.Days ?? tenant.Defaults?.ValidationDays ?? 30;
        if (days is < 1 or > 30)
            throw new RuleHealthException("The validation window must be 1 to 30 days (advanced hunting keeps 30 days of native XDR data; Basic tables allow 30).");

        var rules = Require<RulesDocument>(store, alias, Documents.Rules, "Collect from the tenant first");
        var schema = Require<SchemaDocument>(store, alias, Documents.Schema, "Collect from the tenant first");
        var candidates = Require<CandidatesDocument>(store, alias, Documents.Candidates, "Extract the value checks first");
        var selections = store.TryRead<SelectionsDocument>(alias, Documents.Selections)
            ?? throw new RuleHealthException(
                $"No selections.json for '{alias}': mark the values that must always be present as Validate in Value checks and save first.");

        var notes = new List<string>();
        if (candidates.GeneratedAt < rules.GeneratedAt)
            notes.Add("candidates.json is older than rules.json: extract again to validate the current rules.");

        var plan = ValidationPlanner.Plan(selections, candidates, rules.Rules, schema, days, options.IncludeBasic);
        var selected = selections.Decisions.Values.Count(d => d.State == DecisionStates.Validate);
        return new PreparedValidation(tenant, plan, selected, notes);
    }

    /// <returns>A one-line summary.</returns>
    public static async Task<string> RunAsync(JsonStore store, TokenStore tokens, string alias, ValidateOptions options, Action<string> log,
        Action<string>? httpLog, CancellationToken cancellationToken)
    {
        var prepared = Prepare(store, alias, options);
        if (prepared.Tenant.Auth.Mode == AuthModes.Offline)
            throw new RuleHealthException($"'{alias}' is an offline tenant: there is no tenant data to validate against.");
        foreach (var note in prepared.Notes)
            log("note: " + note);
        var plan = prepared.Plan;
        log(Describe(prepared));

        RawResponseLog? raw = null;
        if (options.SaveRaw)
        {
            raw = new RawResponseLog(Path.Combine(store.TenantDirectory(alias), "raw", "validate-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            log($"saving raw responses to {raw.Folder}");
        }

        ValidationDocument document;
        if (plan.Batches.Count == 0)
        {
            log("nothing to query.");
            document = new ValidationDocument { Tenant = alias, RunAt = DateTime.UtcNow, WindowDays = plan.Days, Results = plan.Decided.ToList() };
        }
        else
        {
            using var api = TenantConnection.Connect(prepared.Tenant, tokens, log, httpLog, raw, options.FreshSignIn);
            var workspaceIds = (prepared.Tenant.Workspaces ?? new List<WorkspaceRef>())
                .ToDictionary(w => w.WorkspaceName, w => w.WorkspaceId, StringComparer.OrdinalIgnoreCase);
            var runner = new ValidationRunner(new ApiValidationExecutor(api, workspaceIds), line => log("  " + line));
            document = await runner.RunAsync(alias, plan, DateTime.UtcNow, cancellationToken);
        }

        store.Write(alias, Documents.Validation, document);
        var counts = string.Join(", ", document.Results.GroupBy(r => r.Verdict).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
        log($"wrote {store.DocumentPath(alias, Documents.Validation)} ({counts})");
        return $"Validated {document.Results.Count} values: {counts}";
    }

    /// <summary>One line: how many checks run in how many queries, how many are skipped or do not bind.</summary>
    public static string Describe(PreparedValidation prepared)
    {
        var plan = prepared.Plan;
        return $"{prepared.Selected} checks marked validate: {plan.Batches.Sum(b => b.Checks.Count)} to run in {plan.Batches.Count} queries, " +
               $"{plan.Decided.Count(r => r.Verdict == Verdicts.Skipped)} skipped, {plan.Decided.Count(r => r.Verdict == Verdicts.Error)} do not bind";
    }

    private static T Require<T>(JsonStore store, string alias, string document, string hint) where T : class =>
        store.TryRead<T>(alias, document)
        ?? throw new RuleHealthException($"No {document}.json for '{alias}'. {hint}.");
}
