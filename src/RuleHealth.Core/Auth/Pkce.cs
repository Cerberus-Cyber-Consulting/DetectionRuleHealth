using System.Security.Cryptography;
using System.Text;

namespace RuleHealth.Core.Auth;

/// <summary>
/// The random values of a sign-in and the PKCE challenge (RFC 7636, method S256). PKCE lets a public
/// client (no secret) prove at the token endpoint that it is the one that started the sign-in: only
/// the challenge travels through the browser, the verifier stays in this process.
/// </summary>
public static class Pkce
{
    /// <summary>32 cryptographically random bytes, base64url: used for state, verifier, session and CSRF values.</summary>
    public static string NewRandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>base64url(SHA-256(verifier)) without padding.</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
