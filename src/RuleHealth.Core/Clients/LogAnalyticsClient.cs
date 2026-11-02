using System.Globalization;
using System.Text.Json;

namespace RuleHealth.Core.Clients;

/// <summary>One result row: column name → value (cloned, so it outlives the response).</summary>
public sealed class QueryRow : Dictionary<string, JsonElement>
{
    public QueryRow() : base(StringComparer.Ordinal) { }

    public QueryRow(StringComparer comparer) : base(comparer) { }

    public string? Text(string column) =>
        TryGetValue(column, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public long? Long(string column) =>
        TryGetValue(column, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    public DateTime? Timestamp(string column) =>
        Text(column) is { Length: > 0 } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;

    /// <summary>
    /// A dynamic column. Log Analytics returns dynamic values as JSON *text* ("[\"a\",\"b\"]"), so a
    /// string is parsed; an array/object is returned as-is.
    /// </summary>
    public JsonElement? Dynamic(string column)
    {
        if (!TryGetValue(column, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                using var json = JsonDocument.Parse(text);
                return json.RootElement.Clone();
            }
            catch (JsonException)
            {
                return value;
            }
        }
        return value.ValueKind == JsonValueKind.Null ? null : value;
    }
}

/// <summary>
/// Rows of the first result table. <paramref name="PartialError"/> is set when the service returned
/// 200 with an `error` (PartialError: limits hit, a shard failed): the rows are kept but may be
/// incomplete, and the caller must tell the user.
/// </summary>
public sealed record QueryResult(List<QueryRow> Rows, string? PartialError);

/// <summary>Runs KQL against a Log Analytics workspace (POST /v1/workspaces/{id}/query).</summary>
public sealed class LogAnalyticsClient
{
    private static readonly ApiCall Call = new(
        "Log Analytics query",
        "Log Analytics API delegated permission Data.Read (admin consent) and Log Analytics Reader " +
        "(or Microsoft Sentinel Reader) on the workspace");

    private readonly ApiConnection _api;

    public LogAnalyticsClient(ApiConnection api) => _api = api;

    /// <param name="timespan">
    /// ISO-8601 duration (P90D). Applied on top of the query's own time filter (on TimeGenerated), so
    /// it must be at least as wide.
    /// </param>
    public async Task<QueryResult> QueryAsync(string workspaceId, string query, string timespan, string label, CancellationToken cancellationToken)
    {
        var url = $"{ApiConnection.LogAnalyticsBase}/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/query";
        using var json = await _api.PostJsonAsync(_api.LogAnalytics, url, new { query, timespan }, Call with { Label = label }, cancellationToken);
        return ReadPrimaryTable(json.RootElement);
    }

    /// <summary>
    /// Basic and Auxiliary plan tables: POST /v1/workspaces/{id}/search?timespan=… (timespan in the
    /// query string; a filter in the KQL does not replace it). Full KQL, at most 30 days, two concurrent
    /// queries per user, **billed per GB scanned**. Response assumed to have the /query shape (VERIFY).
    /// </summary>
    public async Task<QueryResult> SearchAsync(string workspaceId, string query, string timespan, string label, CancellationToken cancellationToken)
    {
        var url = $"{ApiConnection.LogAnalyticsBase}/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/search?timespan={Uri.EscapeDataString(timespan)}";
        using var json = await _api.PostJsonAsync(_api.LogAnalytics, url, new { query }, Call with { Label = label }, cancellationToken);
        return ReadPrimaryTable(json.RootElement);
    }

    /// <summary>
    /// `{ tables: [ { name, columns: [{name,type}], rows: [[...]] } ], error? }` → rows of the first table.
    /// No tables (204 No Content for a workspace without data) → no rows.
    /// </summary>
    public static QueryResult ReadPrimaryTable(JsonElement root)
    {
        string? partialError = null;
        if (ApiConnection.Path(root, "error") is { ValueKind: JsonValueKind.Object } error)
        {
            var message = ApiConnection.Text(error, "message") ?? "unknown error";
            var code = ApiConnection.Text(error, "code");
            // details[].message usually says which limit was hit.
            var details = ApiConnection.Path(error, "details") is { ValueKind: JsonValueKind.Array } list
                ? string.Join("; ", list.EnumerateArray().Select(d => ApiConnection.Text(d, "message")).Where(m => m is not null))
                : "";
            partialError = $"{code}: {message}" + (details.Length > 0 ? $" ({details})" : "");
        }

        if (ApiConnection.Path(root, "tables") is not { ValueKind: JsonValueKind.Array } tables || tables.GetArrayLength() == 0)
            return new QueryResult(new List<QueryRow>(), partialError);

        var table = tables[0];
        var columns = ApiConnection.Path(table, "columns") is { ValueKind: JsonValueKind.Array } columnList
            ? columnList.EnumerateArray().Select(c => ApiConnection.Text(c, "name") ?? "").ToList()
            : new List<string>();

        var rows = new List<QueryRow>();
        if (ApiConnection.Path(table, "rows") is { ValueKind: JsonValueKind.Array } rowList)
        {
            foreach (var row in rowList.EnumerateArray())
            {
                var values = new QueryRow();
                var index = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    if (index < columns.Count)
                        values[columns[index]] = cell.Clone();
                    index++;
                }
                rows.Add(values);
            }
        }
        return new QueryResult(rows, partialError);
    }
}
