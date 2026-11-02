using System.Text.Json;

namespace RuleHealth.Core.Auth;

/// <summary>
/// One readable line about an access token, for 401/403 messages: which app it was issued to, which
/// delegated permissions it carries and which Entra directory roles were active in it. Reads only the
/// claims; the token itself is never logged or stored.
///
/// Why (2026-10-26): Defender's 403 "Missing user permissions … user permissions: ." looks the same
/// whether the app lacks the scope or the account lacks an (activated) role. The claims tell them apart.
/// </summary>
public static class TokenSummary
{
    // Directory role template ids (the `wids` claim) that matter for Defender XDR access.
    private static readonly Dictionary<string, string> KnownRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["62e90394-69f5-4237-9190-012177145e10"] = "Global Administrator",
        ["f2ef992c-3afb-46b9-b7cf-a126ee74c451"] = "Global Reader",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Security Administrator",
        ["5f2222b1-57c3-48ba-8ad5-d4759f1fde6f"] = "Security Operator",
        ["5d6b6bb7-de71-4623-b4af-96380a352509"] = "Security Reader",
    };

    // Always present in a user token: "User" directory role template; not worth listing.
    private const string DefaultUserRole = "b79fbf4d-3ef9-4689-8143-76b194e85509";

    /// <summary>The summary, or null when the token is not a readable JWT.</summary>
    public static string? Describe(string token)
    {
        using var claims = Jwt.ReadClaims(token);
        if (claims is null)
            return null;
        var root = claims.RootElement;
        var appId = Jwt.Text(root, "appid") ?? Jwt.Text(root, "azp");
        var app = appId is null ? "unknown app" : ClientProfile.KnownAppName(appId) ?? $"app {appId}";
        var scopes = Jwt.Text(root, "scp") ?? "(none)";
        var roleIds = root.TryGetProperty("wids", out var wids) && wids.ValueKind == JsonValueKind.Array
            ? wids.EnumerateArray().Select(w => w.GetString()).Where(w => w is not null && w != DefaultUserRole).Select(w => w!).ToList()
            : new List<string>();
        var roles = roleIds.Count == 0
            ? "none"
            : string.Join(", ", roleIds.Select(id => KnownRoles.TryGetValue(id, out var role) ? role : "other role"));
        return $"Token used: {app}; permissions: {scopes}; directory roles active in the token: {roles}.";
    }
}
