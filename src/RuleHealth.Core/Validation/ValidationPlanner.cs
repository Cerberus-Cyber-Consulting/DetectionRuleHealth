using System.Text.RegularExpressions;
using Kusto.Language;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Validation;

/// <summary>Where a batch runs: Log Analytics (/query or, for Basic/Auxiliary tables, /search) or advanced hunting.</summary>
public sealed record QueryTarget(string Engine, string? WorkspaceName, bool Search)
{
    public string Describe() => Engine == QueryEngines.Hunting
        ? "advanced hunting"
        : $"Log Analytics {(Search ? "/search" : "/query")} ({WorkspaceName})";
}

/// <summary>One query to run: checks on one table through one target.</summary>
public sealed record ValidationBatch(string Table, QueryTarget Target, string TimeColumn, IReadOnlyList<PlannedCheck> Checks)
{
    public string Kql(int days) => ValidationQueryBuilder.BatchQuery(
        Table, TimeColumn, timeFilterInQuery: Target.Engine == QueryEngines.LogAnalytics, days,
        Checks.Select(c => (c.Check.CheckId, c.Predicate)).ToList());
}

public sealed record PlannedCheck(ValueCheck Check, string Predicate);

/// <summary>The checks that will run (in batches) and the ones decided without a query (skipped/error).</summary>
public sealed record ValidationPlan(int Days, IReadOnlyList<ValidationBatch> Batches, IReadOnlyList<ValidationResult> Decided);

/// <summary>
/// Decides, for every check the user marked "validate", whether and where it runs. Pure (no IO), so
/// the dry run and the tests see exactly what a real run would do.
///
/// Routing follows what the rules read (Microsoft Learn: an XDR table can differ between advanced
/// hunting and its copy streamed to Log Analytics):
/// 1. Used by a Sentinel analytics rule and the table is in a workspace → Log Analytics, preferably
///    the workspace of that rule.
/// 2. Otherwise, a table advanced hunting knows → advanced hunting (custom detections on XDR tables).
/// 3. Otherwise, a workspace table → Log Analytics (custom detection on a Sentinel table).
/// Basic/Auxiliary tables need /search, which is billed per GB scanned: only with includeBasic.
/// </summary>
public static partial class ValidationPlanner
{
    public static ValidationPlan Plan(
        SelectionsDocument selections, CandidatesDocument candidates, IEnumerable<Rule> rules, SchemaDocument schema,
        int days, bool includeBasic)
    {
        if (schema.TableInfo is null)
            throw new RuleHealthException(
                "schema.json has no tableInfo (written by `collect` since M5), so validate cannot tell where each table lives. " +
                "Run Update from tenant (Data view) once.");

        var checks = candidates.Checks.ToDictionary(c => c.CheckId, StringComparer.Ordinal);
        var rulesById = rules.ToDictionary(r => r.RuleId, StringComparer.Ordinal);
        var decided = new List<ValidationResult>();
        var runnable = new List<(PlannedCheck Planned, QueryTarget Target, string TimeColumn)>();

        foreach (var (checkId, decision) in selections.Decisions.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (decision.State != DecisionStates.Validate)
                continue;

            if (!checks.TryGetValue(checkId, out var check))
            {
                decided.Add(Skipped(checkId, decision.Check, QueryEngines.LogAnalytics,
                    "no rule uses this value any more (not in candidates.json)"));
                continue;
            }

            if (OnlyUsedByDisabledRules(check, rulesById))
            {
                // A disabled rule does not run, so a value it needs cannot break anything; validating it
                // only produced noise (live 2026-10-19: absent/table-empty results for switched-off rules).
                // The decision is kept: once a rule using the value is enabled again, it is validated.
                decided.Add(Skipped(check, QueryEngines.LogAnalytics,
                    "only used by disabled rules: they do not run, so a missing value cannot break them"));
                continue;
            }

            var target = Route(check, rulesById, schema.TableInfo.GetValueOrDefault(check.Table), includeBasic, out var problem);
            var engine = target?.Engine ?? QueryEngines.LogAnalytics;
            if (target is null)
            {
                decided.Add(Skipped(check, engine, problem!));
                continue;
            }
            if (!check.Validatable)
            {
                decided.Add(Skipped(check, engine,
                    "the rule compares a value derived from the column (expression lineage); not validated in v1"));
                continue;
            }

            var columns = schema.Tables.GetValueOrDefault(check.Table);
            var (predicate, predicateProblem) = ValidationQueryBuilder.Predicate(check, columns?.GetValueOrDefault(check.Column));
            if (predicate is null)
            {
                decided.Add(Skipped(check, engine, predicateProblem!));
                continue;
            }

            var timeColumn = TimeColumn(target, columns);
            if (timeColumn is null)
            {
                decided.Add(Skipped(check, engine, $"{check.Table} has no TimeGenerated or Timestamp column in schema.json"));
                continue;
            }
            runnable.Add((new PlannedCheck(check, predicate), target, timeColumn));
        }

        var globals = SchemaBuilder.Build(schema);
        var batches = new List<ValidationBatch>();
        foreach (var group in runnable.GroupBy(r => (r.Planned.Check.Table, r.Target, r.TimeColumn)))
        {
            var bindable = new List<PlannedCheck>();
            foreach (var item in group)
            {
                // Pre-flight: a predicate that does not bind against the schema would fail the whole batch.
                var single = new ValidationBatch(group.Key.Table, group.Key.Target, group.Key.TimeColumn, new[] { item.Planned });
                var error = FirstBindError(single.Kql(days), globals);
                if (error is null)
                    bindable.Add(item.Planned);
                else
                    decided.Add(Result(item.Planned.Check, group.Key.Target.Engine, Verdicts.Error, $"query does not bind: {error}"));
            }
            foreach (var chunk in bindable.Chunk(ValidationQueryBuilder.MaxChecksPerQuery))
                batches.Add(new ValidationBatch(group.Key.Table, group.Key.Target, group.Key.TimeColumn, chunk));
        }

        return new ValidationPlan(days, batches, decided);
    }

    /// <summary>Where a check runs, or null with the reason it cannot run.</summary>
    public static QueryTarget? Route(ValueCheck check, IReadOnlyDictionary<string, Rule> rulesById, TableInfo? table,
        bool includeBasic, out string? problem)
    {
        problem = null;
        if (table is null)
        {
            problem = $"table {check.Table} is in no workspace and not in advanced hunting (schema.json)";
            return null;
        }

        var occurrenceRules = check.Occurrences
            .Select(o => rulesById.GetValueOrDefault(o.RuleId))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        var sentinelWorkspaces = occurrenceRules
            .Where(r => r.Source == RuleSources.Sentinel)
            .Select(r => WorkspaceOf(r.Origin))
            .Where(w => w is not null)
            .ToList();
        var usedBySentinel = occurrenceRules.Any(r => r.Source == RuleSources.Sentinel);

        QueryTarget? target = null;
        if (usedBySentinel && table.Workspaces.Count > 0)
        {
            var workspace = table.Workspaces.FirstOrDefault(w => sentinelWorkspaces.Contains(w, StringComparer.OrdinalIgnoreCase))
                            ?? table.Workspaces[0];
            target = new QueryTarget(QueryEngines.LogAnalytics, workspace, Search: false);
        }
        else if (table.Hunting)
        {
            target = new QueryTarget(QueryEngines.Hunting, null, Search: false);
        }
        else if (table.Workspaces.Count > 0)
        {
            target = new QueryTarget(QueryEngines.LogAnalytics, table.Workspaces[0], Search: false);
        }

        if (target is null)
        {
            problem = $"table {check.Table} has no workspace and is not in advanced hunting";
            return null;
        }

        if (target.Engine == QueryEngines.LogAnalytics && table.Plan is TablePlans.Basic or TablePlans.Auxiliary)
        {
            if (!includeBasic)
            {
                problem = $"{check.Table} is a {table.Plan} plan table: it can only be queried with /search, billed per GB scanned. " +
                          "Run validate with --include-basic to include it.";
                return null;
            }
            target = target with { Search = true };
        }
        return target;
    }

    /// <summary>
    /// True when every rule that uses the value is in rules.json and disabled. Rules missing from
    /// rules.json (stale candidates) do not count as disabled: unknown is not "off".
    /// </summary>
    public static bool OnlyUsedByDisabledRules(ValueCheck check, IReadOnlyDictionary<string, Rule> rulesById)
    {
        var ruleIds = check.Occurrences.Select(o => o.RuleId).Distinct(StringComparer.Ordinal).ToList();
        return ruleIds.Count > 0 && ruleIds.All(id => rulesById.TryGetValue(id, out var rule) && !rule.Enabled);
    }

    /// <summary>Log Analytics tables all have TimeGenerated; XDR tables in advanced hunting use Timestamp.</summary>
    private static string? TimeColumn(QueryTarget target, Dictionary<string, string>? columns)
    {
        if (target.Engine == QueryEngines.Hunting && columns?.ContainsKey("Timestamp") == true)
            return "Timestamp";
        if (columns is null || columns.ContainsKey("TimeGenerated"))
            return "TimeGenerated";
        return columns.ContainsKey("Timestamp") ? "Timestamp" : null;
    }

    private static string? FirstBindError(string kql, GlobalState globals) =>
        KustoCode.ParseAndAnalyze(kql, globals).GetDiagnostics()
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.Message;

    /// <summary>The workspace name in a Sentinel rule's ARM id (…/workspaces/{name}/providers/Microsoft.SecurityInsights/…).</summary>
    public static string? WorkspaceOf(string? origin)
    {
        if (origin is null)
            return null;
        var match = WorkspaceInResourceId().Match(origin);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static ValidationResult Result(ValueCheck check, string engine, string verdict, string? error) => new()
    {
        CheckId = check.CheckId,
        Table = check.Table,
        Column = check.Column,
        Path = check.Path,
        OperatorClass = check.OperatorClass,
        Value = check.Value,
        Verdict = verdict,
        MatchCount = null,
        TableRowCount = null,
        LastSeenAt = null,
        Engine = engine,
        Error = error,
    };

    private static ValidationResult Skipped(ValueCheck check, string engine, string reason) =>
        Result(check, engine, Verdicts.Skipped, reason);

    private static ValidationResult Skipped(string checkId, CheckKey key, string engine, string reason) => new()
    {
        CheckId = checkId,
        Table = key.Table,
        Column = key.Column,
        Path = key.Path,
        OperatorClass = key.OperatorClass,
        Value = key.Value,
        Verdict = Verdicts.Skipped,
        MatchCount = null,
        TableRowCount = null,
        LastSeenAt = null,
        Engine = engine,
        Error = reason,
    };

    [GeneratedRegex("/workspaces/([^/]+)/providers/Microsoft\\.SecurityInsights/", RegexOptions.IgnoreCase)]
    private static partial Regex WorkspaceInResourceId();
}
