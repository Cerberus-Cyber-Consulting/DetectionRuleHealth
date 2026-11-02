using System.Globalization;
using System.Text.Json;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Clients;

/// <summary>Microsoft Sentinel analytics rules of one workspace (ARM alertRules).</summary>
public sealed class SentinelRulesClient
{
    // Current GA. Its spec only models the Scheduled, Fusion and MicrosoftSecurityIncidentCreation kinds;
    // VERIFY on a live tenant that NRT/ML/TI rules are returned too (else use 2025-07-01-preview).
    public const string ApiVersion = "2025-09-01";

    private static readonly ApiCall Call = new(
        "List Sentinel analytics rules",
        "Microsoft Sentinel Reader on the workspace (ARM user_impersonation)");

    private readonly ApiConnection _api;

    public SentinelRulesClient(ApiConnection api) => _api = api;

    public async Task<List<Rule>> ListAsync(WorkspaceRef workspace, CancellationToken cancellationToken)
    {
        var url = $"{ApiConnection.ArmBase}{WorkspaceResourceId.From(workspace).Path}" +
                  $"/providers/Microsoft.SecurityInsights/alertRules?api-version={ApiVersion}";
        var call = Call with { Label = $"List Sentinel analytics rules ({workspace.WorkspaceName})" };
        var items = await _api.GetAllPagesAsync(_api.Arm, url, "nextLink", call, cancellationToken);
        return items.Select(ToRule).ToList();
    }

    /// <summary>
    /// One alertRule resource → Rule. Every field is read null-safe: which properties exist depends on
    /// the kind (NRT has no queryFrequency, MicrosoftSecurityIncidentCreation has no severity, only
    /// Scheduled and NRT have a query), and unknown kinds must not fail the collection.
    /// </summary>
    public static Rule ToRule(JsonElement item)
    {
        var name = ApiConnection.Text(item, "name")
            ?? throw new RuleHealthException("A Sentinel alert rule in the response has no name.");
        var properties = ApiConnection.Path(item, "properties");
        string? Prop(string property) => properties is { } p ? ApiConnection.Text(p, property) : null;

        return new Rule
        {
            RuleId = name,
            Source = RuleSources.Sentinel,
            Kind = ApiConnection.Text(item, "kind") ?? "Unknown",
            DisplayName = Prop("displayName") ?? name,
            Description = EmptyToNull(Prop("description")),
            Enabled = properties is { } props && ApiConnection.Path(props, "enabled") is { ValueKind: JsonValueKind.True },
            Severity = Prop("severity")?.ToLowerInvariant(),
            QueryText = EmptyToNull(Prop("query")),
            Frequency = Prop("queryFrequency"),
            LastModifiedAt = ParseTimestamp(Prop("lastModifiedUtc")),
            // Analytics rules run in the Log Analytics workspace, so they bind against workspace tables.
            SchemaContext = SchemaContexts.Sentinel,
            Origin = ApiConnection.Text(item, "id"),
        };
    }

    internal static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    internal static DateTime? ParseTimestamp(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;
}
