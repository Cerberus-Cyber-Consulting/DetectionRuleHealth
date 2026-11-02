using System.Collections.Concurrent;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Auth;

/// <summary>A sign-in step that was started and is waiting for Entra's redirect back.</summary>
/// <param name="ReturnTo">Where the UI continues afterwards: a key the server maps to a fixed route, never a URL.</param>
/// <param name="Step">1 = Graph (every profile); 2 = ARM through Azure PowerShell (Microsoft's apps only).</param>
/// <param name="FirstStep">Step 2 only: the Graph tokens of step 1 (memory only) and the account they belong to.</param>
public sealed record PendingSignIn(string Alias, string TenantId, ClientProfile Profile, string Verifier, string BrowserSession,
    DateTimeOffset CreatedAt, string ReturnTo = "data", int Step = 1, FirstStepResult? FirstStep = null)
{
    /// <summary>The client this step signs in with.</summary>
    public string ClientId => Profile.ClientFor(ClientProfile.ResourceOfStep(Step));
}

/// <summary>Step 1 of a two-step sign-in, carried into step 2.</summary>
public sealed record FirstStepResult(TokenResponse Graph, string? ObjectId, string? Upn);

/// <summary>What a redeemed code leads to: the finished sign-in, or the next step's authorize URL.</summary>
public sealed record SignInOutcome(TenantSession? Session, string? NextUrl);

/// <summary>
/// The OAuth 2.0 authorization code flow with PKCE, in raw HTTP (skill rulehealth-auth):
/// <see cref="Begin"/> builds the authorize URL the browser is sent to; <see cref="CompleteAsync"/>
/// redeems the code Entra sends back and stores the tokens in the <see cref="TokenStore"/>, or, for
/// Microsoft's apps after step 1, returns the authorize URL of step 2 (Azure PowerShell).
/// Works from inside a container because the browser, not this process, talks to Entra.
/// </summary>
public sealed class PkceAuthorizationService
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    // A page on another site can make the browser start sign-ins; cap what that can pile up.
    private const int MaxPending = 100;

    private readonly ConcurrentDictionary<string, PendingSignIn> _pending = new(StringComparer.Ordinal);
    private readonly TokenStore _tokens;
    private readonly string _publicUrl;

    /// <param name="publicUrl">RULEHEALTH_PUBLIC_URL (http://localhost:5180): the redirect URIs are built from it.</param>
    public PkceAuthorizationService(string publicUrl, TokenStore tokens)
    {
        _publicUrl = publicUrl.TrimEnd('/');
        _tokens = tokens;
    }

    /// <summary>The own app registration's redirect URI; it must be registered exactly like this (platform "Mobile and desktop applications").</summary>
    public string RedirectUri => ClientProfile.OwnApp("-").RedirectUri(_publicUrl);

    public string RedirectUriFor(ClientProfile profile) => profile.RedirectUri(_publicUrl);

    /// <summary>Starts a sign-in for <paramref name="tenant"/> from the browser session <paramref name="browserSession"/>.</summary>
    /// <param name="consent">Ask Entra to show the consent prompt (an administrator consenting for the organization).</param>
    /// <returns>The authorize URL to redirect the browser to.</returns>
    public string Begin(TenantDocument tenant, string browserSession, string returnTo = "data", bool consent = false)
    {
        var (tenantId, profile) = TenantAuth.Require(tenant);
        var pending = new PendingSignIn(tenant.Alias, tenantId, profile, Pkce.NewRandomValue(), browserSession, _tokens.Now(), returnTo);
        return Start(pending, consent ? "consent" : "select_account", loginHint: null);
    }

    private string Start(PendingSignIn pending, string? prompt, string? loginHint)
    {
        Purge();
        var state = Pkce.NewRandomValue();
        _pending[state] = pending;
        return AuthorizeUrl(pending.TenantId, pending.ClientId, RedirectUriFor(pending.Profile), state, Pkce.Challenge(pending.Verifier),
            pending.Profile.SignInScope(pending.Step), prompt, loginHint);
    }

    /// <summary>The authorize URL of the own app registration's (single) sign-in.</summary>
    public static string AuthorizeUrl(string tenantId, string clientId, string redirectUri, string state, string challenge) =>
        AuthorizeUrl(tenantId, clientId, redirectUri, state, challenge, Scopes.SignIn, "select_account", null);

    public static string AuthorizeUrl(string tenantId, string clientId, string redirectUri, string state, string challenge,
        string scope, string? prompt, string? loginHint) =>
        $"{EntraTokenClient.Authority}/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/authorize" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        "&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&response_mode=query" +
        $"&scope={Uri.EscapeDataString(scope)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={Uri.EscapeDataString(challenge)}" +
        "&code_challenge_method=S256" +
        // select_account: always show the account picker (the user may be signed in to Entra with another
        // account). consent: the admin consent prompt. Step 2 of Microsoft's apps sends no prompt but a
        // login_hint for the account of step 1, so Entra usually needs one click or none.
        (prompt is null ? "" : $"&prompt={prompt}") +
        (loginHint is null ? "" : $"&login_hint={Uri.EscapeDataString(loginHint)}");

    /// <summary>
    /// Takes the pending sign-in for <paramref name="state"/> out (single use). Null when it is unknown,
    /// expired or was started from another browser session: then nothing may be redeemed.
    /// </summary>
    public PendingSignIn? Take(string? state, string? browserSession)
    {
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending))
            return null;
        if (_tokens.Now() - pending.CreatedAt > StateLifetime || pending.BrowserSession != browserSession)
            return null;
        return pending;
    }

    /// <summary>
    /// Redeems the code of a pending step and checks the tenant (and, in step 2, the account). The last
    /// step stores the session; step 1 of Microsoft's apps returns the authorize URL of step 2.
    /// </summary>
    public async Task<SignInOutcome> CompleteAsync(PendingSignIn pending, string code, CancellationToken cancellationToken)
    {
        var resource = ClientProfile.ResourceOfStep(pending.Step);
        var response = await _tokens.Entra.RedeemCodeAsync(pending.TenantId, pending.ClientId, code, RedirectUriFor(pending.Profile),
            pending.Verifier, cancellationToken, pending.Profile.ScopeFor(resource));

        using var claims = Jwt.ReadClaims(response.IdToken)
            ?? throw new RuleHealthException("The sign-in returned no id token (the openid scope is missing?).");
        var tid = Jwt.Text(claims.RootElement, "tid");
        // An account from another tenant (B2B guest, a personal account) could otherwise end up
        // collecting from the wrong place: discard everything.
        if (!string.Equals(tid, pending.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new RuleHealthException(
                $"You signed in to a different tenant ({tid ?? "unknown"}) than the one configured for '{pending.Alias}' " +
                $"({pending.TenantId}). Sign in again with an account of that tenant.") { HttpStatus = 403 };
        var oid = Jwt.Text(claims.RootElement, "oid");
        var upn = Jwt.Text(claims.RootElement, "preferred_username");

        if (pending.Profile.SingleClient)
            return new SignInOutcome(_tokens.Add(pending.Alias, pending.TenantId, pending.Profile, upn, response, null), null);

        if (pending.Step == 1)
        {
            // Microsoft's apps: Graph is done; Azure PowerShell signs in next, for the same account.
            var next = pending with
            {
                Step = 2, Verifier = Pkce.NewRandomValue(), CreatedAt = _tokens.Now(), FirstStep = new FirstStepResult(response, oid, upn),
            };
            return new SignInOutcome(null, Start(next, prompt: null, loginHint: upn));
        }

        // Step 2 must be the same person as step 1: two accounts would mix whose roles the data shows.
        var first = pending.FirstStep ?? throw new InvalidOperationException("Step 2 without step 1.");
        if (first.ObjectId is null || !string.Equals(first.ObjectId, oid, StringComparison.OrdinalIgnoreCase))
            throw new RuleHealthException(
                $"You signed in with two different accounts ({first.Upn ?? "unknown"}, then {upn ?? "unknown"}). " +
                "Sign in again and pick the same account both times.") { HttpStatus = 403 };
        return new SignInOutcome(_tokens.Add(pending.Alias, pending.TenantId, pending.Profile, upn, first.Graph, response), null);
    }

    private void Purge()
    {
        var now = _tokens.Now();
        foreach (var (state, pending) in _pending)
            if (now - pending.CreatedAt > StateLifetime)
                _pending.TryRemove(state, out _);
        foreach (var (state, _) in _pending.OrderBy(p => p.Value.CreatedAt).Take(Math.Max(0, _pending.Count - MaxPending + 1)))
            _pending.TryRemove(state, out _);
    }
}
