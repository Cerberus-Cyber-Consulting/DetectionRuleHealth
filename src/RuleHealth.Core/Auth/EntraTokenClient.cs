using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuleHealth.Core.Auth;

/// <summary>What the token endpoint returned. Lives in memory only (TokenStore), never serialized.</summary>
public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string? IdToken);

/// <summary>
/// An error from the Entra token endpoint, with the parts the UI shows: the AADSTS code, a plain
/// explanation (<see cref="AuthHints"/>) and the correlation id to quote to Microsoft support.
/// </summary>
public sealed partial class EntraException : RuleHealthException
{
    public EntraException(string error, string? description, string? code, string? correlationId)
        : base(Format(error, description, code, correlationId))
    {
        Error = error;
        Description = description;
        Code = code;
        CorrelationId = correlationId;
    }

    /// <summary>OAuth error: invalid_grant, interaction_required, invalid_client, ...</summary>
    public string Error { get; }
    public string? Description { get; }
    /// <summary>AADSTS code, e.g. AADSTS50011; null when Entra sent none.</summary>
    public string? Code { get; }
    public string? CorrelationId { get; }

    /// <summary>
    /// The refresh token no longer works without the user (expired, revoked, password changed, MFA or
    /// Conditional Access wants an interactive sign-in): the session is over, sign in again.
    /// </summary>
    public bool NeedsSignIn => Error is "invalid_grant" or "interaction_required" or "login_required" or "consent_required";

    /// <summary>Reads Entra's error JSON (token endpoint) into an exception.</summary>
    public static EntraException FromResponse(string body, int status)
    {
        string error = $"http_{status}", description = "";
        string? code = null, correlation = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            error = Jwt.Text(root, "error") ?? error;
            description = Jwt.Text(root, "error_description") ?? "";
            correlation = Jwt.Text(root, "correlation_id");
            if (root.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array &&
                codes.GetArrayLength() > 0 && codes[0].TryGetInt32(out var first))
                code = $"AADSTS{first}";
        }
        catch (JsonException)
        {
            description = "The sign-in service answered with something that is not JSON (a proxy?).";
        }
        return new EntraException(error, FirstLine(description), code ?? CodeIn(description), correlation ?? CorrelationIn(description));
    }

    /// <summary>The AADSTS code inside a description text (redirect errors carry only the text).</summary>
    public static string? CodeIn(string? text) => text is null ? null : AadstsPattern().Match(text) is { Success: true } m ? m.Value : null;

    public static string? CorrelationIn(string? text) =>
        text is null ? null : CorrelationPattern().Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// The message part of an Entra description, without the "Trace ID: … Correlation ID: … Timestamp: …"
    /// that follows it (on new lines in redirects, on the same line in some token endpoint answers).
    /// </summary>
    public static string FirstLine(string text)
    {
        var line = text.Split('\n')[0];
        var trace = line.IndexOf("Trace ID:", StringComparison.Ordinal);
        return (trace >= 0 ? line[..trace] : line).Trim();
    }

    private static string Format(string error, string? description, string? code, string? correlationId)
    {
        var text = $"Sign-in failed: {(string.IsNullOrEmpty(description) ? error : description)}";
        if (AuthHints.ForCode(code) is { } hint)
            text += "\n" + hint;
        if (correlationId is not null)
            text += $"\nCorrelation ID: {correlationId}";
        return text;
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadstsPattern();

    [GeneratedRegex(@"Correlation ID:\s*([0-9a-fA-F-]{36})")]
    private static partial Regex CorrelationPattern();
}

/// <summary>
/// The Entra v2.0 token endpoint, in raw HTTP: redeem an authorization code (with the PKCE verifier)
/// and redeem a refresh token for another resource. A public client: never a client_secret.
/// </summary>
public sealed class EntraTokenClient
{
    public const string Authority = "https://login.microsoftonline.com";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="http">For tests; default a shared client.</param>
    public EntraTokenClient(HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _http = http ?? Shared;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static string TokenUrl(string tenantId) => $"{Authority}/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";

    /// <summary>Authorization code → access token (Graph unless <paramref name="scope"/> says otherwise) + refresh token + id token.</summary>
    public Task<TokenResponse> RedeemCodeAsync(string tenantId, string clientId, string code, string redirectUri,
        string verifier, CancellationToken cancellationToken, string? scope = null) =>
        PostAsync(tenantId, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = scope ?? Scopes.For(ApiResource.Graph),
        }, cancellationToken);

    /// <summary>Refresh token → access token for one resource (and usually a newer refresh token).</summary>
    public Task<TokenResponse> RefreshAsync(string tenantId, string clientId, string refreshToken, ApiResource resource,
        CancellationToken cancellationToken) =>
        RefreshAsync(tenantId, clientId, refreshToken, Scopes.For(resource), cancellationToken);

    /// <summary>Refresh token → access token for the given scope (a client profile decides it).</summary>
    public Task<TokenResponse> RefreshAsync(string tenantId, string clientId, string refreshToken, string scope,
        CancellationToken cancellationToken) =>
        PostAsync(tenantId, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = scope,
        }, cancellationToken);

    private async Task<TokenResponse> PostAsync(string tenantId, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(TokenUrl(tenantId), new FormUrlEncodedContent(form), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RuleHealthException($"login.microsoftonline.com could not be reached: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw EntraException.FromResponse(body, (int)response.StatusCode);

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var accessToken = Jwt.Text(root, "access_token")
                ?? throw new RuleHealthException("The sign-in service returned no access token.");
            // expires_in is a number in v2.0 responses; older endpoints sent it as a string.
            var seconds = root.TryGetProperty("expires_in", out var expires)
                ? expires.ValueKind == JsonValueKind.Number ? expires.GetInt32() : int.TryParse(expires.GetString(), out var s) ? s : 3600
                : 3600;
            return new TokenResponse(accessToken, _now().AddSeconds(seconds), Jwt.Text(root, "refresh_token"), Jwt.Text(root, "id_token"));
        }
    }
}
