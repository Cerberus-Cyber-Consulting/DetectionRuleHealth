using RuleHealth.Core.Clients;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Collection;

/// <summary>What `collect` gathered, plus warnings the user should read (unknown types, duplicates, partial schema).</summary>
/// <param name="DefenderLinks">Custom detection rule id → what links its alerts to it, for linking incidents.</param>
public sealed record CollectedRules(
    RulesDocument Document, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, DefenderLink> DefenderLinks);

/// <summary>Alert-linking fields of a custom detection rule that are not part of rules.json.</summary>
public sealed record DefenderLink(string? AlertTitle, string? DetectorId);

public sealed record CollectedSchema(SchemaDocument Document, IReadOnlyList<string> Warnings);

/// <summary>
/// Collects rules and the table schema of one tenant. Pure orchestration over the clients; writing the
/// files is the caller's job, so a failed run never leaves a half-updated tenant folder.
/// </summary>
public sealed class TenantCollector
{
    private readonly ApiConnection _api;
    private readonly Action<string> _progress;

    public TenantCollector(ApiConnection api, Action<string> progress)
    {
        _api = api;
        _progress = progress;
    }

    /// <summary>
    /// Sentinel rules from every workspace, then Defender custom detections; a failing source throws.
    /// CollectOperation calls the two sources separately so that one refused source (no Sentinel role)
    /// does not stop the other, and reports which one failed.
    /// </summary>
    public async Task<CollectedRules> CollectRulesAsync(
        TenantDocument tenant, bool includeSentinel, bool includeDefender, CancellationToken cancellationToken)
    {
        var sentinel = includeSentinel ? await CollectSentinelRulesAsync(tenant, cancellationToken) : null;
        var defender = includeDefender ? await CollectDefenderRulesAsync(cancellationToken) : null;
        return Combine(tenant, sentinel, defender);
    }

    /// <summary>Sentinel analytics rules of every workspace in tenant.json, as (workspace name, rule).</summary>
    public async Task<List<(string Workspace, Rule Rule)>> CollectSentinelRulesAsync(TenantDocument tenant, CancellationToken cancellationToken)
    {
        var client = new SentinelRulesClient(_api);
        var rules = new List<(string, Rule)>();
        foreach (var workspace in tenant.Workspaces ?? new List<WorkspaceRef>())
        {
            var found = await client.ListAsync(workspace, cancellationToken);
            // Per-kind counts show on the first live run whether the GA api-version returns NRT rules.
            _progress($"sentinel {workspace.WorkspaceName}: {found.Count} rules" + (found.Count > 0 ? $" ({KindSummary(found)})" : ""));
            rules.AddRange(found.Select(r => (workspace.WorkspaceName, r)));
        }
        return rules;
    }

    public async Task<List<DefenderRule>> CollectDefenderRulesAsync(CancellationToken cancellationToken)
    {
        var found = await new DetectionRulesClient(_api).ListAsync(cancellationToken);
        var autoDisabled = found.Count(r => r.Rule.Status == RuleStatuses.AutoDisabled);
        _progress($"defender: {found.Count} custom detection rules" + (autoDisabled > 0 ? $" ({autoDisabled} auto-disabled)" : ""));
        return found;
    }

    /// <summary>rules.json from what each source returned (null = source not collected).</summary>
    public static CollectedRules Combine(TenantDocument tenant, IEnumerable<(string Workspace, Rule Rule)>? sentinel, IEnumerable<DefenderRule>? defender)
    {
        var warnings = new List<string>();
        var rules = new List<Rule>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool Add(Rule rule, string from)
        {
            // Sentinel incidents reference rules by GUID only, so the same GUID in two workspaces cannot
            // be told apart later. Keep the first and say so.
            if (seen.TryGetValue(rule.RuleId, out var first))
            {
                warnings.Add($"rule id {rule.RuleId} ('{rule.DisplayName}') exists in {first} and {from}; kept the one from {first}");
                return false;
            }
            seen[rule.RuleId] = from;
            rules.Add(rule);
            return true;
        }

        foreach (var (workspace, rule) in sentinel ?? Enumerable.Empty<(string, Rule)>())
            Add(rule, $"workspace {workspace}");

        var links = new Dictionary<string, DefenderLink>(StringComparer.Ordinal);
        foreach (var found in defender ?? Enumerable.Empty<DefenderRule>())
            if (Add(found.Rule, "Defender"))
                links.TryAdd(found.Rule.RuleId, new DefenderLink(found.AlertTitle, found.DetectorId));

        var document = new RulesDocument { Tenant = tenant.Alias, GeneratedAt = DateTime.UtcNow, Rules = rules };
        return new CollectedRules(document, warnings, links);
    }

    /// <summary>
    /// The unified schema: workspace tables (ARM) merged with the advanced hunting tables (Graph).
    /// Either source may fail (missing permission) as long as the other works: binding against part of
    /// the schema only costs bind errors on rules that use the missing tables. Both failing is an error.
    /// </summary>
    public async Task<CollectedSchema> CollectSchemaAsync(TenantDocument tenant, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var client = new SchemaClient(_api);
        var workspaces = tenant.Workspaces ?? new List<WorkspaceRef>();

        TableSchemas? workspaceTables = null;
        var perWorkspace = new List<(WorkspaceRef Workspace, TableSchemas Tables)>();
        if (workspaces.Count > 0)
        {
            try
            {
                foreach (var workspace in workspaces)
                {
                    var tables = await client.GetWorkspaceTablesAsync(workspace, warnings, cancellationToken);
                    _progress($"schema {workspace.WorkspaceName}: {tables.Count} workspace tables");
                    perWorkspace.Add((workspace, tables));
                }
                workspaceTables = SchemaClient.Union(perWorkspace.Select(p => p.Tables));
            }
            catch (RuleHealthException ex)
            {
                warnings.Add($"workspace tables not collected: {ex.Message}");
            }
        }

        TableSchemas? huntingTables = null;
        var workspaceId = workspaces.FirstOrDefault()?.WorkspaceId;
        try
        {
            // Scoped to the first workspace: advanced hunting only knows the one onboarded workspace.
            huntingTables = await client.GetHuntingTablesAsync(workspaceId, warnings, cancellationToken);
            _progress($"schema advanced hunting: {huntingTables.Count} tables");
        }
        catch (RuleHealthException ex) when (ex.HttpStatus == 400 && workspaceId is not null)
        {
            // Seen live: 400 "Workspace '<id>' is not available to the caller" when the workspace is not
            // onboarded to the Defender portal (or the user has no access to it there). The XDR tables
            // are still worth having: retry unscoped.
            warnings.Add($"advanced hunting does not know workspace {workspaceId} (not onboarded to the Defender portal?); " +
                          "collected the XDR tables only");
            try
            {
                huntingTables = await client.GetHuntingTablesAsync(null, warnings, cancellationToken);
                _progress($"schema advanced hunting (no workspace): {huntingTables.Count} tables");
            }
            catch (RuleHealthException retry)
            {
                warnings.Add($"advanced hunting schema not collected: {retry.Message}");
            }
        }
        catch (RuleHealthException ex)
        {
            warnings.Add($"advanced hunting schema not collected: {ex.Message}");
        }

        if (workspaceTables is null && huntingTables is null)
            throw new RuleHealthException("No table schema could be collected:\n  " + string.Join("\n  ", warnings));

        var sources = new List<string>();
        if (workspaceTables is not null)
            sources.Add("arm-tables");
        if (huntingTables is not null)
            sources.Add("graph-hunting");

        var document = new SchemaDocument
        {
            Tenant = tenant.Alias,
            GeneratedAt = DateTime.UtcNow,
            Source = string.Join(" + ", sources),
            Tables = SchemaFileReader.MergeUnified(workspaceTables ?? new TableSchemas(), huntingTables ?? new TableSchemas()),
            TableInfo = BuildTableInfo(perWorkspace, huntingTables),
            // FileProfile/DeviceFromIP exist only where advanced hunting does.
            Functions = huntingTables is not null ? SchemaFileReader.UnifiedFunctions.ToList() : new List<string>(),
        };
        return new CollectedSchema(document, warnings);
    }

    /// <summary>Where each table can be queried: the validate command routes its queries with this.</summary>
    public static Dictionary<string, TableInfo> BuildTableInfo(
        IEnumerable<(WorkspaceRef Workspace, TableSchemas Tables)> perWorkspace, TableSchemas? huntingTables)
    {
        var workspacesByTable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var planByTable = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (workspace, tables) in perWorkspace)
        {
            foreach (var table in tables.Keys)
            {
                if (!workspacesByTable.TryGetValue(table, out var names))
                    workspacesByTable[table] = names = new List<string>();
                names.Add(workspace.WorkspaceName);
                planByTable.TryAdd(table, tables.Plans.GetValueOrDefault(table));
            }
        }

        var info = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
        foreach (var table in workspacesByTable.Keys.Concat(huntingTables?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal))
        {
            info[table] = new TableInfo
            {
                Workspaces = workspacesByTable.GetValueOrDefault(table) ?? new List<string>(),
                Plan = planByTable.GetValueOrDefault(table),
                Hunting = huntingTables?.ContainsKey(table) ?? false,
            };
        }
        return info;
    }

    private static string KindSummary(IEnumerable<Rule> rules) =>
        string.Join(", ", rules.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
}
