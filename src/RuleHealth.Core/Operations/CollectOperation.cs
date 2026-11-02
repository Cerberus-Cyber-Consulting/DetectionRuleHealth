using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Collection;
using RuleHealth.Core.Jobs;
using RuleHealth.Core.Model;
using RuleHealth.Core.Stats;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Operations;

/// <param name="Days">Incident window; null = tenant.json defaults.incidentDays (90).</param>
/// <param name="Skip">Any of sentinel, defender, schema, incidents.</param>
/// <param name="SaveRaw">Save every response body under data/&lt;alias&gt;/raw/.</param>
public sealed record CollectOptions(int? Days = null, IReadOnlySet<string>? Skip = null, bool SaveRaw = false, bool FreshSignIn = false);

/// <summary>The stages of a collect (and refresh) job, in the order the wizard lists them.</summary>
public static class CollectStages
{
    public const string Sentinel = "Microsoft Sentinel analytics rules";
    public const string Defender = "Defender XDR custom detections";
    public const string Schema = "Table schema";
    public const string Incidents = "Incidents";
    public const string RuleStats = "Rule statistics";
    /// <summary>Extraction; only in a refresh job (collect + extract).</summary>
    public const string ValueChecks = "Value checks";

    /// <summary>Lists every stage as pending, so the UI shows the whole list from the start.</summary>
    public static void Announce(IProgress<JobStage> stages, bool withValueChecks)
    {
        foreach (var name in new[] { Sentinel, Defender, Schema, Incidents, RuleStats })
            stages.Report(new JobStage(name, StageStates.Pending));
        if (withValueChecks)
            stages.Report(new JobStage(ValueChecks, StageStates.Pending));
    }
}

/// <summary>
/// collect: rules (Sentinel + Defender), the table schema, the incidents of the window and the
/// per-rule stats → rules.json, schema.json, incidents.json, rule-stats.json. Run as a serve job
/// (it needs the sign-in in the TokenStore); progress goes to the log callback and the stages.
///
/// A source the account may not read (403: no Sentinel role, no Defender role) fails only its own
/// stage; the others continue and their files are written. The job summary and the stage say what
/// is missing. Anything else (signed out, network, a bug) fails the whole run before any file is
/// written, so a failure halfway never leaves a half-updated tenant folder.
/// </summary>
public static class CollectOperation
{
    public static readonly IReadOnlyList<string> Sources = new[] { "sentinel", "defender", "schema", "incidents" };

    private sealed class NoStages : IProgress<JobStage>
    {
        public void Report(JobStage value) { }
    }

    /// <returns>A one-line summary of what was written.</returns>
    public static async Task<string> RunAsync(JsonStore store, TokenStore tokens, string alias, CollectOptions options, Action<string> log,
        IProgress<JobStage>? stages, Action<string>? httpLog, CancellationToken cancellationToken)
    {
        var tenant = store.TryRead<TenantDocument>(alias, Documents.Tenant)
            ?? throw new RuleHealthException($"No tenant '{alias}' in {store.DataRoot}. Add it in the UI first.");
        if (tenant.Auth.Mode == AuthModes.Offline)
            throw new RuleHealthException($"'{alias}' is an offline tenant (markdown rules): there is no tenant to collect from.");

        // Raw responses go to data/<alias>/raw/<timestamp>/: tenant data, git-ignored, never served.
        RawResponseLog? raw = null;
        if (options.SaveRaw)
        {
            raw = new RawResponseLog(Path.Combine(store.TenantDirectory(alias), "raw", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            log($"saving raw responses to {raw.Folder}");
        }

        using var api = TenantConnection.Connect(tenant, tokens, log, httpLog, raw, options.FreshSignIn);
        return await RunAsync(store, tenant, api, options, log, stages, cancellationToken);
    }

    /// <summary>The collection itself, on an already connected <paramref name="api"/> (tests pass a fake one).</summary>
    public static async Task<string> RunAsync(JsonStore store, TenantDocument tenant, ApiConnection api, CollectOptions options,
        Action<string> log, IProgress<JobStage>? stages, CancellationToken cancellationToken)
    {
        stages ??= new NoStages();
        var alias = tenant.Alias;
        var skip = options.Skip ?? new HashSet<string>(StringComparer.Ordinal);
        var unknown = skip.Where(s => !Sources.Contains(s)).ToList();
        if (unknown.Count > 0)
            throw new RuleHealthException($"skip accepts {string.Join(", ", Sources)}; got {string.Join(", ", unknown)}.");

        var days = options.Days ?? tenant.Defaults?.IncidentDays ?? 90;
        if (days < 1)
            throw new RuleHealthException("The incident window must be at least 1 day.");

        var collector = new TenantCollector(api, line => log("  " + line));
        var failed = new List<string>();
        var workspaces = tenant.Workspaces?.Count ?? 0;

        void Report(string name, string state, string? detail = null, int? count = null, string? hint = null) =>
            stages.Report(new JobStage(name, state, detail, count, hint));

        // A refused source fails its own stage; the run continues with the others.
        async Task<T?> Stage<T>(string name, Func<Task<T>> run, Func<T, (string Detail, int Count)> done) where T : class
        {
            Report(name, StageStates.Running);
            try
            {
                var result = await run();
                var (detail, count) = done(result);
                Report(name, StageStates.Succeeded, detail, count);
                return result;
            }
            catch (RuleHealthException ex) when (IsRefused(ex))
            {
                log($"{name}: {ex.Message}");
                Report(name, StageStates.Failed, FirstLine(ex.Message), null, ex.Hint);
                failed.Add(name);
                return null;
            }
        }

        // ---- 1-2. rules, one source at a time. Collect everything first, then write.
        List<(string Workspace, Rule Rule)>? sentinel = null;
        if (skip.Contains("sentinel"))
            Report(CollectStages.Sentinel, StageStates.Skipped, "Left out on request");
        else if (workspaces == 0)
        {
            Report(CollectStages.Sentinel, StageStates.Skipped, "No Sentinel workspace selected");
            log("note: tenant.json has no workspaces, so there are no Sentinel rules to collect.");
        }
        else
            sentinel = await Stage(CollectStages.Sentinel, () => collector.CollectSentinelRulesAsync(tenant, cancellationToken),
                r => ($"{r.Count} rules in {workspaces} workspace{(workspaces == 1 ? "" : "s")}", r.Count));

        List<DefenderRule>? defender = null;
        if (skip.Contains("defender"))
            Report(CollectStages.Defender, StageStates.Skipped, "Left out on request");
        else
            defender = await Stage(CollectStages.Defender, () => collector.CollectDefenderRulesAsync(cancellationToken),
                r => ($"{r.Count} custom detection rules", r.Count));

        if (sentinel is null && defender is null && failed.Count > 0)
        {
            // No rule source worked: there is nothing to write, so this is a failed run, not a partial one.
            throw new RuleHealthException($"No rules could be collected ({string.Join(", ", failed)} refused).\n  Nothing was written.")
            {
                HttpStatus = 403,
                Hint = "Check the roles of the account you signed in with: Microsoft Sentinel Reader on the workspace, Security Reader for Defender XDR.",
            };
        }
        var rules = sentinel is null && defender is null ? null : TenantCollector.Combine(tenant, sentinel, defender);

        // ---- 3. schema (tolerates one of its two sources failing; throws only when both do)
        CollectedSchema? schema = null;
        if (skip.Contains("schema"))
            Report(CollectStages.Schema, StageStates.Skipped, "Left out on request");
        else
        {
            Report(CollectStages.Schema, StageStates.Running);
            try
            {
                schema = await collector.CollectSchemaAsync(tenant, cancellationToken);
                Report(CollectStages.Schema, StageStates.Succeeded, $"{schema.Document.Tables.Count} tables", schema.Document.Tables.Count);
            }
            catch (RuleHealthException ex) when (ex is not { HttpStatus: 401, Hint: null })
            {
                log($"{CollectStages.Schema}: {ex.Message}");
                Report(CollectStages.Schema, StageStates.Failed, FirstLine(ex.Message), null,
                    "Needs ThreatHunting.Read.All (advanced hunting) or Reader on a workspace (workspace tables).");
                failed.Add(CollectStages.Schema);
            }
        }

        // ---- 4-5. incidents need this run's rules: the Defender alert links are not in rules.json.
        CollectedIncidents? incidents = null;
        RuleStatsDocument? stats = null;
        if (skip.Contains("incidents"))
            Report(CollectStages.Incidents, StageStates.Skipped, "Left out on request");
        else if (rules is null)
            Report(CollectStages.Incidents, StageStates.Skipped, "No rules collected");
        else
        {
            var fromSentinel = sentinel is not null;
            var fromDefender = defender is not null;
            incidents = await Stage(CollectStages.Incidents,
                () => new IncidentCollector(api, line => log("  " + line)).CollectAsync(
                    tenant, rules, days, fromSentinel, fromDefender, DateTime.UtcNow, cancellationToken),
                r => ($"{r.Document.Incidents.Count} in {days} days", r.Document.Incidents.Count));
        }

        if (incidents is null || rules is null)
            Report(CollectStages.RuleStats, StageStates.Skipped, "Needs the incidents");
        else
        {
            stats = RuleStatsCalculator.Calculate(alias, incidents.Document, rules.Document.Rules, DateTime.UtcNow);
            var raised = stats.Rules.Count(r => r.Incidents > 0);
            Report(CollectStages.RuleStats, StageStates.Succeeded, $"{raised} of {stats.Rules.Count} rules raised incidents", raised);
        }

        // ---- write
        var summary = new List<string>();
        if (rules is not null)
        {
            var previous = store.TryRead<RulesDocument>(alias, Documents.Rules);
            store.Write(alias, Documents.Rules, rules.Document);
            var all = rules.Document.Rules;
            log($"wrote {store.DocumentPath(alias, Documents.Rules)} ({all.Count} rules, " +
                $"{all.Count(r => r.QueryText is not null)} with a query, {all.Count(r => r.Enabled)} enabled)");
            Warn(log, rules.Warnings);
            // Say so when a refused source drops rules the previous collection had.
            foreach (var (stage, source) in new[] { (CollectStages.Sentinel, RuleSources.Sentinel), (CollectStages.Defender, RuleSources.Defender) })
                if (failed.Contains(stage) && previous?.Rules.Any(r => r.Source == source) == true)
                    log($"warning: rules.json no longer has the {stage} of the previous collection: that source was refused this time.");
            summary.Add($"{all.Count} rules");
        }
        if (schema is not null)
        {
            store.Write(alias, Documents.Schema, schema.Document);
            log($"wrote {store.DocumentPath(alias, Documents.Schema)} ({schema.Document.Tables.Count} tables from {schema.Document.Source})");
            Warn(log, schema.Warnings);
            summary.Add($"{schema.Document.Tables.Count} tables");
        }
        if (incidents is not null && stats is not null)
        {
            store.Write(alias, Documents.Incidents, incidents.Document);
            var all = incidents.Document.Incidents;
            log($"wrote {store.DocumentPath(alias, Documents.Incidents)} ({all.Count} incidents in {days} days, " +
                $"{all.Count(i => i.Status == IncidentStatuses.Closed)} closed)");
            store.Write(alias, Documents.RuleStats, stats);
            log($"wrote {store.DocumentPath(alias, Documents.RuleStats)} " +
                $"({stats.Rules.Count(r => r.Incidents > 0)} of {stats.Rules.Count} rules raised incidents)");
            Warn(log, incidents.Warnings);
            summary.Add($"{all.Count} incidents in {days} days");
        }

        var text = summary.Count > 0 ? "Collected " + string.Join(", ", summary) : "Nothing collected (everything skipped)";
        return failed.Count > 0 ? $"{text}. Not collected: {string.Join(", ", failed)}" : text;
    }

    /// <summary>
    /// The account or app may not read this source (403), or the service refused the token for it
    /// (401 from an API, which carries a hint). A 401 without a hint is the session itself ending
    /// (signed out): that stops the whole run.
    /// </summary>
    private static bool IsRefused(RuleHealthException ex) => ex.HttpStatus == 403 || (ex.HttpStatus == 401 && ex.Hint is not null);

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private static void Warn(Action<string> log, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
            log($"warning: {warning}");
    }
}
