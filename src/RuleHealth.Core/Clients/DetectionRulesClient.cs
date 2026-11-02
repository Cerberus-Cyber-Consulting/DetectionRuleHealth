using System.Text.Json;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Clients;

/// <summary>
/// A custom detection rule plus what links its alerts back to it (kept in memory, not in rules.json):
/// the rule's internal `detectorId` (alert.detectorId equals it, confirmed on a live tenant 2026-10-16)
/// and the alert title template (fallback).
/// </summary>
public sealed record DefenderRule(Rule Rule, string? AlertTitle, string? DetectorId);

/// <summary>Defender XDR custom detection rules (Graph beta security/rules/detectionRules).</summary>
public sealed class DetectionRulesClient
{
    public const string ListUrl = ApiConnection.GraphBase + "/beta/security/rules/detectionRules";

    private static readonly ApiCall Call = new(
        "List Defender custom detection rules",
        "Graph delegated permission CustomDetection.Read.All (admin consent) and Security Reader " +
        "(or a Defender Unified RBAC role with detection tuning read). The Azure CLI app cannot be consented for it: sign in with your app registration (auth browser) " +
        "or with Azure CLI + Microsoft Graph Command Line Tools (auth cli-graph)");

    private readonly ApiConnection _api;

    public DetectionRulesClient(ApiConnection api) => _api = api;

    public async Task<List<DefenderRule>> ListAsync(CancellationToken cancellationToken)
    {
        // @odata.nextLink is not documented for this list; follow it when present.
        var items = await _api.GetAllPagesAsync(_api.Graph, ListUrl, "@odata.nextLink", Call, cancellationToken);
        return items.Select(item => new DefenderRule(ToRule(item), AlertTitle(item), ApiConnection.Text(item, "detectorId"))).ToList();
    }

    /// <summary>
    /// The alert title template. Not part of rules.json; only used in memory to link alerts to rules
    /// when alert.detectorId does not match (see IncidentCollector).
    /// </summary>
    public static string? AlertTitle(JsonElement item) =>
        ApiConnection.PathText(item, "detectionAction", "alertTemplate", "title");

    /// <summary>
    /// One detectionRule → Rule. Reads `status` and `schedule.frequency` only: isEnabled, lastRunDetails
    /// and schedule.period are deprecated (announced removal 2026-10-01, still returned on 2026-10-16).
    /// `detectorId` is deprecated too but is the only field alerts carry; it is read separately and
    /// optionally. `id` is a client-chosen string (Learn's example "office-encoded-powershell"; numbers
    /// in the first live tenant), never assume a GUID. `schedule.frequency` PT0S = continuous (NRT).
    /// </summary>
    public static Rule ToRule(JsonElement item)
    {
        var id = ApiConnection.Text(item, "id")
            ?? throw new RuleHealthException("A Defender detection rule in the response has no id.");
        var status = ApiConnection.Text(item, "status");

        return new Rule
        {
            RuleId = id,
            Source = RuleSources.Defender,
            Kind = "customDetection",
            DisplayName = ApiConnection.Text(item, "displayName") ?? id,
            Description = SentinelRulesClient.EmptyToNull(
                ApiConnection.Text(item, "description")
                ?? ApiConnection.PathText(item, "detectionAction", "alertTemplate", "description")),
            Enabled = status == RuleStatuses.Enabled,
            // unknownFutureValue (or a new value) is left out rather than written into the contract enum.
            Status = status is RuleStatuses.Enabled or RuleStatuses.Disabled or RuleStatuses.AutoDisabled ? status : null,
            Severity = ApiConnection.PathText(item, "detectionAction", "alertTemplate", "severity")?.ToLowerInvariant(),
            QueryText = SentinelRulesClient.EmptyToNull(ApiConnection.PathText(item, "queryCondition", "queryText")),
            // ISO-8601 duration. VERIFY how continuous (NRT) custom detections are represented.
            Frequency = ApiConnection.PathText(item, "schedule", "frequency"),
            LastModifiedAt = SentinelRulesClient.ParseTimestamp(ApiConnection.Text(item, "lastModifiedDateTime")),
            // Custom detections run in the Defender portal: XDR tables plus the onboarded workspace.
            SchemaContext = SchemaContexts.Unified,
            Origin = $"{ListUrl}/{Uri.EscapeDataString(id)}",
        };
    }
}
