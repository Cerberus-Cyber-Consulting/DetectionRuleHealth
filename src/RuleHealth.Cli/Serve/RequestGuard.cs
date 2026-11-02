using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RuleHealth.Core.Auth;

namespace RuleHealth.Cli.Serve;

public enum HostVerdict
{
    Allowed,
    /// <summary>127.0.0.1 / [::1]: same machine, wrong name. Send the browser to localhost (cookie and redirect URI live there).</summary>
    RedirectToPublic,
    /// <summary>Any other name: a DNS-rebinding page or a typo. Answer 421 and do nothing.</summary>
    Misdirected,
}

/// <summary>What the last sign-in, sign-in error or admin consent of a browser session ended with; shown once by the UI.</summary>
/// <param name="Message">For an Entra error: Entra's own first sentence (shown small, as the detail).</param>
/// <param name="Hint">What it means and what to do (AuthHints), the line the UI leads with; null when unknown.</param>
public sealed record SignInNotice(string? Alias, bool Ok, string Message, string? Code = null, string? CorrelationId = null, string? Hint = null);

/// <summary>One browser (cookie). Holds the CSRF token; never a token for an API.</summary>
public sealed class BrowserSession(string id, string csrf, DateTimeOffset createdAt)
{
    private SignInNotice? _notice;

    public string Id { get; } = id;
    public string Csrf { get; } = csrf;
    public DateTimeOffset CreatedAt { get; } = createdAt;

    public void SetNotice(SignInNotice notice) => Interlocked.Exchange(ref _notice, notice);

    /// <summary>The notice, once: a reload must not show it again.</summary>
    public SignInNotice? TakeNotice() => Interlocked.Exchange(ref _notice, null);
}

/// <summary>
/// Protection for a server on localhost. localhost is not a security boundary for browsers: any page
/// the user visits can send requests to http://localhost:5180. So (skill rulehealth-auth):
/// a Host check against DNS rebinding, a session cookie with a CSRF token that every write must send
/// back in a header together with the right Origin, no CORS headers at all, and strict security headers.
/// </summary>
public sealed class RequestGuard
{
    public const string CookieName = "drh_session";
    public const string CsrfHeader = "X-DRH-CSRF";

    // Every page on any site can make the browser create sessions; keep the newest ones.
    private const int MaxSessions = 1000;

    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        // 'unsafe-eval' only because Alpine.js evaluates its attribute expressions.
        "script-src 'self' 'unsafe-eval'; connect-src 'self'; frame-ancestors 'none'; " +
        "form-action 'self' https://login.microsoftonline.com";

    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);

    /// <param name="publicUrl">RULEHEALTH_PUBLIC_URL: the only origin the browser may use.</param>
    public RequestGuard(string publicUrl)
    {
        var uri = new Uri(publicUrl);
        PublicOrigin = uri.GetLeftPart(UriPartial.Authority);
        PublicHost = $"{uri.Host}:{uri.Port}";
        Port = uri.Port;
    }

    /// <summary>http://localhost:5180</summary>
    public string PublicOrigin { get; }
    /// <summary>localhost:5180 — the only accepted Host header.</summary>
    public string PublicHost { get; }
    public int Port { get; }

    public HostVerdict ClassifyHost(string? host) => ClassifyHost(host, PublicHost, Port);

    public static HostVerdict ClassifyHost(string? host, string publicHost, int port)
    {
        if (string.Equals(host, publicHost, StringComparison.OrdinalIgnoreCase))
            return HostVerdict.Allowed;
        if (string.Equals(host, $"127.0.0.1:{port}", StringComparison.Ordinal) || string.Equals(host, $"[::1]:{port}", StringComparison.Ordinal))
            return HostVerdict.RedirectToPublic;
        return HostVerdict.Misdirected;
    }

    /// <summary>The session of the request's cookie, or null.</summary>
    public BrowserSession? Find(HttpListenerRequest request) =>
        request.Cookies[CookieName]?.Value is { Length: > 0 } id && _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>The request's session, or a new one with its cookie set on the response.</summary>
    public BrowserSession Ensure(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (Find(request) is { } existing)
            return existing;
        var session = new BrowserSession(Pkce.NewRandomValue(), Pkce.NewRandomValue(), DateTimeOffset.UtcNow);
        if (_sessions.Count >= MaxSessions)
            foreach (var old in _sessions.Values.OrderBy(s => s.CreatedAt).Take(_sessions.Count - MaxSessions + 1))
                _sessions.TryRemove(old.Id, out _);
        _sessions[session.Id] = session;
        // Lax, not Strict: the sign-in callback is a top-level GET navigation from login.microsoftonline.com
        // and must carry the cookie. Not Secure: the app runs on http://localhost. HttpOnly: scripts never see it.
        response.AppendHeader("Set-Cookie", $"{CookieName}={session.Id}; HttpOnly; SameSite=Lax; Path=/");
        return session;
    }

    /// <summary>
    /// Null when a write (POST/PUT/DELETE) may go ahead; otherwise why not. Needs Origin = the public
    /// origin (browsers always send it on these methods) and the session's CSRF token in X-DRH-CSRF.
    /// A page on another site can send neither.
    /// </summary>
    public string? RefuseWrite(HttpListenerRequest request)
    {
        if (!string.Equals(request.Headers["Origin"], PublicOrigin, StringComparison.Ordinal))
            return $"Refused: writes are only accepted from {PublicOrigin} (Origin header).";
        var session = Find(request);
        var sent = request.Headers[CsrfHeader];
        if (session is null || sent is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sent), Encoding.ASCII.GetBytes(session.Csrf)))
            return $"Refused: missing or expired {CsrfHeader} token. Reload the page.";
        return null;
    }

    public static void AddSecurityHeaders(HttpListenerResponse response)
    {
        response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
