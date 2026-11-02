using System.Text;
using System.Text.Json;

namespace RuleHealth.Core.Auth;

/// <summary>
/// Reads the claims of a JWT (access or id token). No signature check: the engine only reads tokens it
/// received itself over TLS from login.microsoftonline.com (OpenID Connect allows that for the token
/// endpoint response), and it reads claims only to show or compare them, never to grant access.
/// </summary>
public static class Jwt
{
    /// <summary>The payload as JSON, or null when the text is not a readable JWT.</summary>
    public static JsonDocument? ReadClaims(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: >= 2 })
            return null;
        try
        {
            return JsonDocument.Parse(Base64UrlDecode(parts[1]));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static string? Text(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>For tests: an unsigned JWT with the given claims.</summary>
    public static string EncodeUnsigned(object payload) =>
        "eyJhbGciOiJub25lIn0." + Pkce.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))) + ".";

    private static byte[] Base64UrlDecode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}
