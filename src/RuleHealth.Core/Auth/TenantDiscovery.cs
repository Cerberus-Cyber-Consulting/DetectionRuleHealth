using System.Net;

namespace RuleHealth.Core.Auth;

/// <summary>
/// Checks that a tenant id exists in Entra ID before any sign-in, through the public, anonymous OpenID
/// discovery document (no credentials, no tenant data). Seen 2026-10-26: the user's own Object ID was
/// entered as the tenant ID; the Azure CLI then failed with a long Python traceback around
/// "AADSTS90002: Tenant not found". This turns that into one clear sentence before anything else runs.
/// </summary>
public static partial class TenantDiscovery
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Throws <see cref="RuleHealthException"/> (HttpStatus 400) when Entra ID says the tenant does not
    /// exist. Any other outcome (network down, proxy, unexpected answer) is not a verdict: returns false
    /// and lets the sign-in report what is wrong.
    /// </summary>
    public static async Task<bool> EnsureExistsAsync(string tenantId, CancellationToken cancellationToken, HttpClient? http = null)
    {
        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/v2.0/.well-known/openid-configuration";
        HttpResponseMessage response;
        try
        {
            response = await (http ?? Shared).GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return true;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("AADSTS90002", StringComparison.Ordinal))
                throw new RuleHealthException(NotFoundMessage(tenantId)) { HttpStatus = 400 };
            return false;
        }
    }

    /// <summary>
    /// The tenant id (GUID) of a verified domain such as contoso.onmicrosoft.com, from the issuer of the
    /// same anonymous discovery document. Throws <see cref="RuleHealthException"/> with HttpStatus 400
    /// for a malformed name, 404 when Entra ID knows no such tenant, 502 when the answer is unusable.
    /// </summary>
    public static async Task<string> ResolveDomainAsync(string domain, CancellationToken cancellationToken, HttpClient? http = null)
    {
        var name = domain.Trim().ToLowerInvariant();
        if (!DomainPattern().IsMatch(name))
            throw new RuleHealthException($"'{domain}' is neither a tenant ID (GUID) nor a domain such as contoso.onmicrosoft.com.") { HttpStatus = 400 };

        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(name)}/v2.0/.well-known/openid-configuration";
        HttpResponseMessage response;
        try
        {
            response = await (http ?? Shared).GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RuleHealthException($"login.microsoftonline.com could not be reached to look up {name}: {ex.Message}") { HttpStatus = 502 };
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("AADSTS90002", StringComparison.Ordinal))
                throw new RuleHealthException($"No Entra ID tenant has the domain {name}. Check the spelling, or enter the tenant ID.") { HttpStatus = 404 };
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(body);
                    // issuer: https://login.microsoftonline.com/{tenant id}/v2.0
                    if (Jwt.Text(json.RootElement, "issuer") is { } issuer && IssuerPattern().Match(issuer) is { Success: true } m)
                        return m.Groups[1].Value.ToLowerInvariant();
                }
                catch (System.Text.Json.JsonException) { /* fall through */ }
            }
            throw new RuleHealthException($"Could not look up the tenant ID of {name} (HTTP {(int)response.StatusCode}). Enter the tenant ID instead.") { HttpStatus = 502 };
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial System.Text.RegularExpressions.Regex DomainPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^https://[^/]+/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/")]
    private static partial System.Text.RegularExpressions.Regex IssuerPattern();

    public static string NotFoundMessage(string tenantId) =>
        $"Tenant {tenantId} does not exist in Entra ID. Use the Tenant ID (Entra ID → Overview → Tenant ID), " +
        "not a user's Object ID, an app's client ID or a subscription ID.";
}
