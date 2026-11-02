using System.Text.Json;

namespace RuleHealth.Core.Clients;

/// <summary>
/// Advanced hunting (Graph v1.0 security/runHuntingQuery). Body `{ "Query", "Timespan" }` in
/// PascalCase as documented. No `workspaceId`: when the workspace is not available the service
/// silently falls back to the caller's primary workspace, so results could come from the wrong one;
/// hunting is used for XDR tables only.
/// Limits: 30 days of native XDR data, 100 000 rows, 50 MB, 3 minutes, ~45 calls a minute, and a CPU
/// quota checked every 15 minutes (429 when used up).
/// </summary>
public sealed class HuntingClient
{
    public const string Url = ApiConnection.GraphBase + "/v1.0/security/runHuntingQuery";

    private static readonly ApiCall Call = new(
        "Advanced hunting query",
        "Graph delegated permission ThreatHunting.Read.All (admin consent) and a Defender role with advanced hunting " +
        "(Unified RBAC 'Security data basics (read)'; Email & collaboration tables also need 'Email & collaboration metadata (read)')");

    private readonly ApiConnection _api;

    public HuntingClient(ApiConnection api) => _api = api;

    /// <param name="timespan">ISO-8601 duration (P30D); the shorter of this and a filter in the query wins.</param>
    public async Task<QueryResult> RunAsync(string query, string timespan, string label, CancellationToken cancellationToken)
    {
        using var json = await _api.PostJsonAsync(_api.Graph, Url, new { Query = query, Timespan = timespan }, Call with { Label = label }, cancellationToken);
        return ReadResults(json.RootElement);
    }

    /// <summary>
    /// `{ schema: [{name,type}], results: [ {col: value} ] }`. Learn's own example returns camelCase
    /// keys in `results` next to PascalCase schema names, so rows match column names case-insensitively.
    /// Values are read by JSON kind, not by the (incompletely documented) schema type names.
    /// </summary>
    public static QueryResult ReadResults(JsonElement root)
    {
        var rows = new List<QueryRow>();
        if (ApiConnection.Path(root, "results") is { ValueKind: JsonValueKind.Array } results)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object)
                    continue;
                var row = new QueryRow(StringComparer.OrdinalIgnoreCase);
                foreach (var property in result.EnumerateObject())
                    row[property.Name] = property.Value.Clone();
                rows.Add(row);
            }
        }
        return new QueryResult(rows, null);
    }
}
