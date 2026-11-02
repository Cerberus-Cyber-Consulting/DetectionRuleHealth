using System.Globalization;
using System.Text.Json;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Collection;

public sealed record CollectedIncidents(IncidentsDocument Document, IReadOnlyList<string> Warnings);

/// <summary>
/// Collects the incidents of the window and links each one to the rules that raised it:
///
/// - Sentinel analytics rules: Log Analytics `SecurityIncident`, latest state per incident,
///   `RelatedAnalyticRuleIds` → rule GUIDs. Incidents merged into another one are closed with the
///   label "redirected" and skipped (they would count twice and skew the closed stats).
/// - Defender custom detections: Graph incidents with alerts. Only alerts with detectionSource
///   customDetection count; alert → rule by `alert.detectorId == rule.detectorId` (confirmed live),
///   then `== rule id`, then the alert title template. Incidents with status `redirected` (merged)
///   are skipped.
/// - One incident in both sources is merged when SecurityIncident.ProviderIncidentId equals the
///   Graph incident id (confirmed live: every Graph incident had its SecurityIncident copy).
/// - SecurityIncident rows with Status "Deleted" are skipped (seen live, not in the documented enum).
/// - Incidents linked to no rule are left out: incidents.json is about the rules in rules.json.
///
/// The progress lines report how alerts were matched, so the first live run settles the VERIFYs.
/// </summary>
public sealed class IncidentCollector
{
    /// <summary>See references/queries.md in the ms-security-apis skill.</summary>
    public static string SentinelQuery(int days) => $$"""
        SecurityIncident
        | where CreatedTime >= ago({{days}}d)
        | summarize arg_max(TimeGenerated, *) by IncidentName
        // A merged incident is closed with the label "redirected"; flag it so it can be skipped and counted.
        | extend Redirected = tostring(Labels) has "redirected"
        | project IncidentName, IncidentNumber, Title, Severity, Status, Classification, CreatedTime, ClosedTime,
                  RelatedAnalyticRuleIds, ProviderName, ProviderIncidentId, Redirected
        """;

    private readonly ApiConnection _api;
    private readonly Action<string> _progress;

    public IncidentCollector(ApiConnection api, Action<string> progress)
    {
        _api = api;
        _progress = progress;
    }

    public async Task<CollectedIncidents> CollectAsync(
        TenantDocument tenant, CollectedRules rules, int days, bool includeSentinel, bool includeDefender,
        DateTime now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var window = new CollectionWindow { From = now.AddDays(-days), To = now, Days = days };

        var sentinel = new List<(Incident Incident, string? ProviderIncidentId)>();
        if (includeSentinel)
        {
            var resolver = new SentinelRuleIds(rules.Document.Rules);
            var client = new LogAnalyticsClient(_api);
            foreach (var workspace in tenant.Workspaces ?? new List<WorkspaceRef>())
            {
                var result = await client.QueryAsync(
                    workspace.WorkspaceId, SentinelQuery(days), $"P{days + 1}D", $"Sentinel incidents ({workspace.WorkspaceName})", cancellationToken);
                if (result.PartialError is not null)
                    warnings.Add($"Sentinel incidents of {workspace.WorkspaceName} may be incomplete: {result.PartialError}");

                var redirected = result.Rows.Count(row => IsTrue(row, "Redirected"));
                var deleted = result.Rows.Count(IsDeleted);
                var linked = result.Rows
                    .Where(row => !IsTrue(row, "Redirected") && !IsDeleted(row))
                    .Select(row => FromSecurityIncident(row, resolver))
                    .Where(x => x.Incident.RuleIds.Count > 0)
                    .ToList();
                sentinel.AddRange(linked);
                _progress($"incidents sentinel {workspace.WorkspaceName}: {result.Rows.Count} in {days} days, " +
                          $"{linked.Count} linked to analytics rules, {redirected} merged into others and {deleted} deleted (skipped)");
            }
        }

        var defender = new List<Incident>();
        if (includeDefender)
        {
            var matcher = new DefenderAlertMatcher(rules.Document.Rules, rules.DefenderLinks);
            var listed = await new IncidentsClient(_api).ListWithAlertsAsync(window.From, cancellationToken);
            var raw = DistinctById(listed, out var duplicates);
            if (duplicates > 0)
            {
                // Graph pages with $skip offsets: incidents created while paging shift the pages, so one can
                // be listed twice (seen live, 2026-10-19) and, by the same shift, another can be missed.
                warnings.Add($"Graph listed {duplicates} incident(s) twice while paging (new incidents arrived during the collect); " +
                             "kept one copy each. An incident may also have been missed: collect again if counts look off.");
            }
            var redirected = 0;
            foreach (var item in raw)
            {
                if (FromGraphIncident(item, matcher, warnings) is { } incident)
                {
                    if (incident.RuleIds.Count > 0)
                        defender.Add(incident);
                }
                else
                {
                    redirected++;
                }
            }
            _progress($"incidents defender: {raw.Count} in {days} days, {defender.Count} with custom detection alerts, " +
                      $"{redirected} merged into others (skipped)" + (duplicates > 0 ? $", {duplicates} listed twice by Graph (kept once)" : ""));
            _progress($"custom detection alerts → rules: {matcher.ByDetectorId} by detectorId, {matcher.ByRuleId} by rule id, " +
                      $"{matcher.ByTitle} by title, {matcher.Unmatched.Count} unmatched");
            if (matcher.Unmatched.Count > 0)
            {
                warnings.Add($"{matcher.Unmatched.Count} custom detection alerts matched no rule (deleted rule, or a dynamic alert title). " +
                             "Titles: " + string.Join(" | ", matcher.Unmatched.Distinct(StringComparer.Ordinal).Take(5)));
            }
        }

        var (incidents, merged) = Merge(sentinel, defender);
        if (includeSentinel && includeDefender)
            _progress($"incidents in both sources (ProviderIncidentId = Graph id): {merged} merged");

        var document = new IncidentsDocument
        {
            Tenant = tenant.Alias,
            GeneratedAt = now,
            Window = window,
            Incidents = incidents.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.IncidentId, StringComparer.Ordinal).ToList(),
        };
        return new CollectedIncidents(document, warnings);
    }

    /// <summary>
    /// Sentinel copies whose ProviderIncidentId is a collected Graph incident fold into that incident
    /// (rule ids combined; status and verdict from Graph, the source of truth in the unified portal).
    /// </summary>
    public static (List<Incident> Incidents, int Merged) Merge(
        IEnumerable<(Incident Incident, string? ProviderIncidentId)> sentinel, IEnumerable<Incident> defender)
    {
        // Not ToDictionary: a repeated id must combine, never crash the whole collect.
        var byGraphId = new Dictionary<string, Incident>(StringComparer.Ordinal);
        foreach (var incident in defender)
        {
            byGraphId[incident.IncidentId] = byGraphId.TryGetValue(incident.IncidentId, out var existing)
                ? existing with { RuleIds = existing.RuleIds.Union(incident.RuleIds, StringComparer.Ordinal).ToList() }
                : incident;
        }
        var result = new List<Incident>();
        var merged = 0;
        foreach (var (incident, providerId) in sentinel)
        {
            if (providerId is not null && byGraphId.TryGetValue(providerId, out var graph))
            {
                byGraphId[providerId] = graph with { RuleIds = graph.RuleIds.Union(incident.RuleIds, StringComparer.Ordinal).ToList() };
                merged++;
            }
            else
            {
                result.Add(incident);
            }
        }
        result.AddRange(byGraphId.Values);
        return (result, merged);
    }

    /// <summary>
    /// One Graph incident per id, in first-seen order. Of repeated copies the one with the latest
    /// lastUpdateDateTime wins (the most recent state, e.g. resolved after being active).
    /// </summary>
    public static List<JsonElement> DistinctById(IReadOnlyList<JsonElement> incidents, out int duplicates)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var withoutId = new List<JsonElement>();
        duplicates = 0;
        foreach (var item in incidents)
        {
            if (ApiConnection.Text(item, "id") is not { } id)
            {
                withoutId.Add(item); // FromGraphIncident reports these
                continue;
            }
            if (!byId.TryGetValue(id, out var existing))
            {
                byId[id] = item;
                order.Add(id);
                continue;
            }
            duplicates++;
            var current = ParseTimestamp(ApiConnection.Text(existing, "lastUpdateDateTime")) ?? DateTime.MinValue;
            var candidate = ParseTimestamp(ApiConnection.Text(item, "lastUpdateDateTime")) ?? DateTime.MinValue;
            if (candidate > current)
                byId[id] = item;
        }
        return order.Select(id => byId[id]).Concat(withoutId).ToList();
    }

    public static (Incident Incident, string? ProviderIncidentId) FromSecurityIncident(QueryRow row, SentinelRuleIds resolver)
    {
        var closed = string.Equals(row.Text("Status"), "Closed", StringComparison.OrdinalIgnoreCase);
        var ruleIds = row.Dynamic("RelatedAnalyticRuleIds") is { ValueKind: JsonValueKind.Array } ids
            ? ids.EnumerateArray()
                .Where(id => id.ValueKind == JsonValueKind.String)
                .Select(id => resolver.Resolve(id.GetString()!))
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : new List<string>();
        var number = row.Long("IncidentNumber");

        var incident = new Incident
        {
            IncidentId = row.Text("IncidentName") ?? throw new RuleHealthException("A SecurityIncident row has no IncidentName."),
            Source = RuleSources.Sentinel,
            Number = number is { } n && n <= int.MaxValue ? (int)n : null,
            Title = row.Text("Title") ?? "",
            Severity = EmptyToNull(row.Text("Severity"))?.ToLowerInvariant(),
            CreatedAt = row.Timestamp("CreatedTime") ?? throw new RuleHealthException("A SecurityIncident row has no CreatedTime."),
            ClosedAt = closed ? row.Timestamp("ClosedTime") : null,
            Status = closed ? IncidentStatuses.Closed : IncidentStatuses.Open,
            Classification = closed ? SentinelClassification(row.Text("Classification")) : null,
            RuleIds = ruleIds,
        };
        return (incident, EmptyToNull(row.Text("ProviderIncidentId")));
    }

    /// <summary>
    /// SecurityIncident values. A closed incident without a verdict counts as Undetermined (the XDR
    /// sync also maps "Not set" to Undetermined).
    /// </summary>
    public static string SentinelClassification(string? value) => value switch
    {
        "TruePositive" => Classifications.TruePositive,
        "BenignPositive" => Classifications.BenignPositive,
        "FalsePositive" => Classifications.FalsePositive,
        _ => Classifications.Undetermined,
    };

    /// <summary>A Graph incident → Incident with the custom detection rules among its alerts; null when redirected (merged).</summary>
    public static Incident? FromGraphIncident(JsonElement item, DefenderAlertMatcher matcher, List<string> warnings)
    {
        var status = ApiConnection.Text(item, "status");
        if (status == "redirected")
            return null;
        // resolved is the only closed status; active, inProgress and awaitingAction are open.
        var closed = status == "resolved";

        var ruleIds = new List<string>();
        if (ApiConnection.Path(item, "alerts") is { ValueKind: JsonValueKind.Array } alerts)
        {
            foreach (var alert in alerts.EnumerateArray())
            {
                if (ApiConnection.Text(alert, "detectionSource") != "customDetection")
                    continue;
                if (matcher.Match(alert) is { } ruleId && !ruleIds.Contains(ruleId, StringComparer.Ordinal))
                    ruleIds.Add(ruleId);
            }
        }

        var id = ApiConnection.Text(item, "id") ?? throw new RuleHealthException("A Graph incident has no id.");
        var created = ParseTimestamp(ApiConnection.Text(item, "createdDateTime"))
            ?? throw new RuleHealthException($"Graph incident {id} has no createdDateTime.");
        return new Incident
        {
            IncidentId = id,
            Source = RuleSources.Defender,
            Number = int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null,
            Title = ApiConnection.Text(item, "displayName") ?? "",
            Severity = ApiConnection.Text(item, "severity") is { } severity && severity != "unknown" ? severity.ToLowerInvariant() : null,
            CreatedAt = created,
            // Graph incidents have no resolved timestamp; the last update of a resolved incident is the closest.
            ClosedAt = closed ? ParseTimestamp(ApiConnection.Text(item, "lastUpdateDateTime")) : null,
            Status = closed ? IncidentStatuses.Closed : IncidentStatuses.Open,
            Classification = closed ? GraphClassification(ApiConnection.Text(item, "classification"), id, warnings) : null,
            RuleIds = ruleIds,
        };
    }

    public static string GraphClassification(string? value, string incidentId, List<string> warnings)
    {
        switch (value)
        {
            case "truePositive": return Classifications.TruePositive;
            case "informationalExpectedActivity": return Classifications.BenignPositive;
            case "falsePositive": return Classifications.FalsePositive;
            case "unknown": case null: return Classifications.Undetermined;
            default:
                // A value added after this code was written: count it as no verdict and say so once.
                var prefix = $"unknown incident classification '{value}'";
                if (!warnings.Any(w => w.StartsWith(prefix, StringComparison.Ordinal)))
                    warnings.Add($"{prefix} (first on incident {incidentId}); counted as Undetermined");
                return Classifications.Undetermined;
        }
    }

    private static bool IsDeleted(QueryRow row) =>
        string.Equals(row.Text("Status"), "Deleted", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(QueryRow row, string column) =>
        row.TryGetValue(column, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static DateTime? ParseTimestamp(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;
}

/// <summary>
/// RelatedAnalyticRuleIds → the ruleId used in rules.json. The table may hold bare GUIDs or full ARM
/// resource ids (not documented): take the last path segment and match case-insensitively. A GUID with
/// no rule in rules.json (deleted rule, other workspace) is kept in lower case.
/// </summary>
public sealed class SentinelRuleIds
{
    private readonly Dictionary<string, string> _byGuid = new(StringComparer.OrdinalIgnoreCase);

    public SentinelRuleIds(IEnumerable<Rule> rules)
    {
        foreach (var rule in rules.Where(r => r.Source == RuleSources.Sentinel))
            _byGuid.TryAdd(rule.RuleId, rule.RuleId);
    }

    public string Resolve(string relatedId)
    {
        var guid = relatedId.Trim().TrimEnd('/');
        var slash = guid.LastIndexOf('/');
        if (slash >= 0)
            guid = guid[(slash + 1)..];
        return _byGuid.TryGetValue(guid, out var ruleId) ? ruleId : guid.ToLowerInvariant();
    }
}

/// <summary>
/// Links a customDetection alert to a rule, in this order:
/// 1. `alert.detectorId == rule.detectorId` (case-insensitive). Confirmed on a live tenant (438/438),
///    though the rule field is deprecated: if Microsoft removes it, step 3 still works.
/// 2. `alert.detectorId == rule id` (what Learn's wording suggests; matched nothing live).
/// 3. `alert.title == the rule's alert title template` when that title belongs to exactly one rule
///    (fails for titles with dynamic parts).
/// Counts how each alert matched; the counts are printed on every collect.
/// </summary>
public sealed class DefenderAlertMatcher
{
    private readonly Dictionary<string, string> _byDetectorId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byTitle = new(StringComparer.Ordinal);

    public DefenderAlertMatcher(IEnumerable<Rule> rules, IReadOnlyDictionary<string, DefenderLink> links)
    {
        foreach (var rule in rules.Where(r => r.Source == RuleSources.Defender))
            _byId.TryAdd(rule.RuleId, rule.RuleId);

        foreach (var (ruleId, link) in links)
        {
            if (!string.IsNullOrEmpty(link.DetectorId))
                _byDetectorId.TryAdd(link.DetectorId, ruleId);
        }

        // A title used by two rules cannot tell them apart: leave it out.
        foreach (var group in links.Where(l => !string.IsNullOrEmpty(l.Value.AlertTitle)).GroupBy(l => l.Value.AlertTitle!, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
                _byTitle[group.Key] = group.First().Key;
        }
    }

    public int ByDetectorId { get; private set; }
    public int ByRuleId { get; private set; }
    public int ByTitle { get; private set; }

    /// <summary>Titles of alerts that matched no rule.</summary>
    public List<string> Unmatched { get; } = new();

    public string? Match(JsonElement alert)
    {
        var detectorId = ApiConnection.Text(alert, "detectorId");
        if (detectorId is not null && _byDetectorId.TryGetValue(detectorId, out var ruleId))
        {
            ByDetectorId++;
            return ruleId;
        }
        if (detectorId is not null && _byId.TryGetValue(detectorId, out ruleId))
        {
            ByRuleId++;
            return ruleId;
        }
        var title = ApiConnection.Text(alert, "title");
        if (title is not null && _byTitle.TryGetValue(title, out ruleId))
        {
            ByTitle++;
            return ruleId;
        }
        Unmatched.Add(title ?? "(no title)");
        return null;
    }
}
