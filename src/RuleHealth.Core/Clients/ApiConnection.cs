using System.Net;
using System.Text;
using System.Text.Json;
using RuleHealth.Core.Auth;

namespace RuleHealth.Core.Clients;

/// <summary>What a call is for, used in error messages: a label and the permission/role it needs.</summary>
public sealed record ApiCall(string Label, string Needs);

/// <summary>
/// The three authenticated HttpClients (ARM, Graph, Log Analytics) plus the JSON helpers every client
/// uses: GET with error mapping, and paging over `value` + nextLink. Tests construct it with fake
/// handlers, so no client ever talks to HttpClient directly.
/// </summary>
public sealed class ApiConnection : IDisposable
{
    public const string ArmBase = "https://management.azure.com";
    public const string GraphBase = "https://graph.microsoft.com";
    public const string LogAnalyticsBase = "https://api.loganalytics.azure.com";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    public ApiConnection(HttpClient arm, HttpClient graph, HttpClient logAnalytics, RawResponseLog? raw = null)
    {
        Arm = arm;
        Graph = graph;
        LogAnalytics = logAnalytics;
        Raw = raw;
    }

    public HttpClient Arm { get; }
    public HttpClient Graph { get; }
    public HttpClient LogAnalytics { get; }

    /// <summary>When set, every response body is saved (for resolving VERIFY markers on a first live run).</summary>
    public RawResponseLog? Raw { get; }

    /// <summary>The three clients for a signed-in tenant: each API gets its own access token from the session.</summary>
    public static ApiConnection Create(TenantSession session, Action<string>? log, RawResponseLog? raw)
    {
        HttpClient Client(ApiResource resource) =>
            new(new BearerTokenHandler((force, token) => session.GetAccessTokenAsync(resource, force, token), log)) { Timeout = Timeout };
        return new ApiConnection(Client(ApiResource.Arm), Client(ApiResource.Graph), Client(ApiResource.LogAnalytics), raw);
    }

    /// <summary>GET a JSON document. Non-success statuses become a <see cref="RuleHealthException"/>.</summary>
    public async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, ApiCall call, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Graph security enums are evolvable: without this, Sentinel detection sources come back as
        // unknownFutureValue. Harmless for ARM and Log Analytics.
        request.Headers.TryAddWithoutValidation("Prefer", "include-unknown-enum-members");
        return await SendJsonAsync(client, request, call, cancellationToken);
    }

    /// <summary>POST a JSON body and read a JSON response.</summary>
    public async Task<JsonDocument> PostJsonAsync(HttpClient client, string url, object body, ApiCall call, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return await SendJsonAsync(client, request, call, cancellationToken);
    }

    /// <summary>
    /// Every item of a paged list. Follows <paramref name="nextLinkProperty"/> (ARM: nextLink,
    /// Graph: @odata.nextLink) until it is absent; never assumes a page size.
    /// The returned elements are cloned, so they outlive the page documents.
    /// </summary>
    public async Task<List<JsonElement>> GetAllPagesAsync(
        HttpClient client, string url, string nextLinkProperty, ApiCall call, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? next = url;
        var pages = 0;
        // The bearer token goes with every page: never follow a next link to another host.
        var host = (Uri.TryCreate(url, UriKind.Absolute, out var first) ? first : client.BaseAddress)?.Host;
        while (next is not null)
        {
            if (++pages > 1000)
                throw new RuleHealthException($"{call.Label}: more than 1000 pages, stopping (paging loop?).");
            using var page = await GetJsonAsync(client, next, call, cancellationToken);
            if (page.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                items.AddRange(value.EnumerateArray().Select(item => item.Clone()));
            next = page.RootElement.TryGetProperty(nextLinkProperty, out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
            if (string.IsNullOrEmpty(next))
                next = null;
            else if (host is not null && Uri.TryCreate(next, UriKind.Absolute, out var nextUri)
                     && !string.Equals(nextUri.Host, host, StringComparison.OrdinalIgnoreCase))
                throw new RuleHealthException($"{call.Label}: the next page is on another host ({nextUri.Host}), not followed.");
        }
        return items;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpClient client, HttpRequestMessage request, ApiCall call, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RuleHealthException($"{call.Label}: {request.RequestUri?.Host} could not be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RuleHealthException($"{call.Label}: no answer within {Timeout.TotalMinutes} minutes.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Raw?.Save(call.Label, request, (int)response.StatusCode, body);

            if (!response.IsSuccessStatusCode)
                throw new RuleHealthException(DescribeFailure(call, request, response, body))
                {
                    HttpStatus = (int)response.StatusCode,
                    Hint = HintFor(call, response.StatusCode, body),
                };

            // 204 No Content (Log Analytics: workspace without data) is an empty result, not an error.
            if (string.IsNullOrWhiteSpace(body))
                return JsonDocument.Parse("{}");

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new RuleHealthException($"{call.Label}: the response is not JSON ({ex.Message}).", ex);
            }
        }
    }

    /// <summary>
    /// One line a user can act on: what failed, the service's error code/message, what is needed
    /// (for 401/403) and the request id to quote to Microsoft support.
    /// </summary>
    public static string DescribeFailure(ApiCall call, HttpRequestMessage request, HttpResponseMessage response, string body)
    {
        var (code, message) = ReadError(body);
        var status = (int)response.StatusCode;
        var text = new StringBuilder($"{call.Label} failed: HTTP {status}");
        if (code is not null)
            text.Append(' ').Append(code);
        if (message is not null)
            text.Append(": ").Append(message.Length > 400 ? message[..400] + "..." : message);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            text.Append($"\n  Needs: {call.Needs}.");
            // Which app the token came from, its permissions and active roles: tells "the app lacks the
            // scope" apart from "the account lacks an (activated) role". Claims only, never the token.
            if (request.Headers.Authorization?.Parameter is { } token && TokenSummary.Describe(token) is { } summary)
                text.Append($"\n  {summary}");
            if (message is not null && AuthHints.For(message) is { } hint)
                text.Append($"\n  {hint}");
        }
        else if (response.StatusCode == HttpStatusCode.NotFound)
            text.Append($"\n  Not found: {request.RequestUri?.GetLeftPart(UriPartial.Path)}");

        var requestId = Header(response, "x-ms-request-id") ?? Header(response, "request-id") ?? Header(response, "x-ms-correlation-request-id");
        if (requestId is not null)
            text.Append($"\n  request-id: {requestId}");
        return text.ToString();
    }

    /// <summary>What to do about a 401/403, in one sentence; null for other failures.</summary>
    public static string? HintFor(ApiCall call, HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return null;
        var (_, message) = ReadError(body);
        return (message is not null ? AuthHints.For(message) : null) ?? $"Needs {call.Needs}.";
    }

    // ARM, Graph and Log Analytics all use { "error": { "code", "message" } }.
    private static (string? Code, string? Message) ReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                // Log Analytics puts the real reason in nested innererror objects
                // (BadArgumentError → SemanticError → "Failed to resolve column ..."): keep the chain.
                var messages = new List<string>();
                if (Text(error, "message") is { } top)
                    messages.Add(top);
                var inner = Path(error, "innererror") ?? Path(error, "innerError");
                for (var depth = 0; inner is { ValueKind: JsonValueKind.Object } current && depth < 5; depth++)
                {
                    if (Text(current, "message") is { } message && !messages.Contains(message))
                        messages.Add(message);
                    inner = Path(current, "innererror") ?? Path(current, "innerError");
                }
                return (Text(error, "code"), messages.Count > 0 ? string.Join(" → ", messages) : null);
            }
        }
        catch (JsonException)
        {
            // Not JSON (gateway errors): no details.
        }
        return (null, null);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>A string property, or null when it is missing, null or not a string.</summary>
    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Walks a property path (`properties`, `query`); null when any step is missing.</summary>
    public static JsonElement? Path(JsonElement element, params string[] properties)
    {
        var current = element;
        foreach (var property in properties)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.Null ? null : current;
    }

    public static string? PathText(JsonElement element, params string[] properties) =>
        Path(element, properties) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public void Dispose()
    {
        Arm.Dispose();
        Graph.Dispose();
        LogAnalytics.Dispose();
    }
}
