using System.Diagnostics;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Validation;

/// <summary>Result of running one batch query: the single summarize row, or an error.</summary>
public sealed record BatchOutcome(QueryRow? Row, string? Error, int? HttpStatus, string? Warning);

/// <summary>Runs a batch query against its target. Production: <see cref="ApiValidationExecutor"/>; tests fake it.</summary>
public interface IValidationExecutor
{
    Task<BatchOutcome> RunAsync(ValidationBatch batch, string kql, int days, CancellationToken cancellationToken);
}

/// <summary>
/// Executes a <see cref="ValidationPlan"/> and builds validation.json.
/// - Batches run one at a time (advanced hunting allows ~45 calls a minute and has a CPU quota).
/// - A batch the service rejects as a bad request (400) is split in half until the failing check is
///   isolated, so one bad predicate cannot turn 49 good checks into errors. Other failures (403, 429,
///   timeouts) mark the whole batch as error: splitting would not help.
/// - Verdicts: TableRows == 0 → table-empty; count > 0 → present (+ lastSeenAt); 0 → absent.
/// </summary>
public sealed class ValidationRunner
{
    private readonly IValidationExecutor _executor;
    private readonly Action<string> _progress;

    public ValidationRunner(IValidationExecutor executor, Action<string> progress)
    {
        _executor = executor;
        _progress = progress;
    }

    public async Task<ValidationDocument> RunAsync(string tenant, ValidationPlan plan, DateTime runAt, CancellationToken cancellationToken)
    {
        var results = new List<ValidationResult>(plan.Decided);
        var queries = new List<ValidationQuery>();
        var number = 0;
        foreach (var batch in plan.Batches)
        {
            number++;
            _progress($"[{number}/{plan.Batches.Count}] {batch.Table}: {batch.Checks.Count} checks via {batch.Target.Describe()}");
            await RunBatchAsync(batch, plan.Days, results, queries, cancellationToken);
        }

        return new ValidationDocument
        {
            Tenant = tenant,
            RunAt = runAt,
            WindowDays = plan.Days,
            Results = results.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.CheckId, StringComparer.Ordinal).ToList(),
            Queries = queries,
        };
    }

    private async Task RunBatchAsync(ValidationBatch batch, int days, List<ValidationResult> results, List<ValidationQuery> queries,
        CancellationToken cancellationToken)
    {
        var kql = batch.Kql(days);
        var watch = Stopwatch.StartNew();
        var outcome = await _executor.RunAsync(batch, kql, days, cancellationToken);
        queries.Add(new ValidationQuery
        {
            Table = batch.Table,
            Engine = batch.Target.Engine,
            Kql = kql,
            DurationMs = (int)watch.ElapsedMilliseconds,
            Error = outcome.Error,
        });
        if (outcome.Warning is not null)
            _progress($"  warning: {outcome.Warning}");

        if (outcome.Error is not null)
        {
            if (outcome.HttpStatus == 400 && batch.Checks.Count > 1)
            {
                _progress($"  rejected ({Shorten(outcome.Error)}); splitting {batch.Checks.Count} checks to find the bad one");
                var half = batch.Checks.Count / 2;
                await RunBatchAsync(batch with { Checks = batch.Checks.Take(half).ToList() }, days, results, queries, cancellationToken);
                await RunBatchAsync(batch with { Checks = batch.Checks.Skip(half).ToList() }, days, results, queries, cancellationToken);
                return;
            }
            _progress($"  error: {Shorten(outcome.Error)}");
            results.AddRange(batch.Checks.Select(c =>
                ValidationPlanner.Result(c.Check, batch.Target.Engine, Verdicts.Error, outcome.Error)));
            return;
        }

        results.AddRange(Verdict(batch, outcome.Row));
    }

    /// <summary>The summarize row → one result per check.</summary>
    public static IEnumerable<ValidationResult> Verdict(ValidationBatch batch, QueryRow? row)
    {
        // A summarize without by always returns one row; no row = the table returned nothing at all.
        var tableRows = row?.Long("TableRows") ?? 0;
        foreach (var planned in batch.Checks)
        {
            var id = planned.Check.CheckId;
            var count = row?.Long($"c_{id}") ?? 0;
            var verdict = tableRows == 0 ? Verdicts.TableEmpty : count > 0 ? Verdicts.Present : Verdicts.Absent;
            yield return ValidationPlanner.Result(planned.Check, batch.Target.Engine, verdict, null) with
            {
                MatchCount = count,
                TableRowCount = tableRows,
                // maxif over no matching rows gives null (or an empty value): no lastSeen then.
                LastSeenAt = count > 0 ? row?.Timestamp($"l_{id}") : null,
            };
        }
    }

    private static string Shorten(string text)
    {
        var line = text.Split('\n')[0];
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}

/// <summary>Runs batches through the real APIs.</summary>
public sealed class ApiValidationExecutor : IValidationExecutor
{
    private readonly LogAnalyticsClient _logAnalytics;
    private readonly HuntingClient _hunting;
    private readonly IReadOnlyDictionary<string, string> _workspaceIds;

    /// <param name="workspaceIds">tenant.json workspace name → workspace (customer) id.</param>
    public ApiValidationExecutor(ApiConnection api, IReadOnlyDictionary<string, string> workspaceIds)
    {
        _logAnalytics = new LogAnalyticsClient(api);
        _hunting = new HuntingClient(api);
        _workspaceIds = workspaceIds;
    }

    public async Task<BatchOutcome> RunAsync(ValidationBatch batch, string kql, int days, CancellationToken cancellationToken)
    {
        var label = $"Validate {batch.Table}";
        var timespan = $"P{days}D";
        try
        {
            QueryResult result;
            if (batch.Target.Engine == QueryEngines.Hunting)
            {
                result = await _hunting.RunAsync(kql, timespan, label, cancellationToken);
            }
            else
            {
                var workspaceId = _workspaceIds.GetValueOrDefault(batch.Target.WorkspaceName ?? "")
                    ?? throw new RuleHealthException($"Workspace {batch.Target.WorkspaceName} is not in tenant.json.");
                result = batch.Target.Search
                    ? await _logAnalytics.SearchAsync(workspaceId, kql, timespan, label, cancellationToken)
                    : await _logAnalytics.QueryAsync(workspaceId, kql, timespan, label, cancellationToken);
            }
            var warning = result.PartialError is null ? null : $"partial result for {batch.Table}: {result.PartialError}";
            return new BatchOutcome(result.Rows.FirstOrDefault(), null, null, warning);
        }
        catch (RuleHealthException ex)
        {
            return new BatchOutcome(null, ex.Message, ex.HttpStatus, null);
        }
    }
}
