using System.Globalization;
using System.Text.Json;

namespace RuleHealth.Core.Clients;

/// <summary>Defender XDR incidents with their alerts (Graph v1.0 security/incidents?$expand=alerts).</summary>
public sealed class IncidentsClient
{
    private static readonly ApiCall Call = new(
        "List Defender incidents",
        "Graph delegated permission SecurityIncident.Read.All (and probably SecurityAlert.Read.All for the alerts) " +
        "with admin consent, and Security Reader (or a Defender Unified RBAC role that can read incidents)");

    private readonly ApiConnection _api;

    public IncidentsClient(ApiConnection api) => _api = api;

    /// <summary>
    /// Every incident created at or after <paramref name="from"/>, alerts included. No $top: Graph
    /// documents no maximum page size, so the client just follows @odata.nextLink. ApiConnection sends
    /// `Prefer: include-unknown-enum-members`, without which Sentinel sources and the awaitingAction
    /// status come back as unknownFutureValue.
    /// </summary>
    public async Task<List<JsonElement>> ListWithAlertsAsync(DateTime from, CancellationToken cancellationToken)
    {
        // OData datetime literals are unquoted.
        var filter = "createdDateTime ge " + from.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var url = $"{ApiConnection.GraphBase}/v1.0/security/incidents?$filter={Uri.EscapeDataString(filter)}&$expand=alerts";
        return await _api.GetAllPagesAsync(_api.Graph, url, "@odata.nextLink", Call, cancellationToken);
    }
}
