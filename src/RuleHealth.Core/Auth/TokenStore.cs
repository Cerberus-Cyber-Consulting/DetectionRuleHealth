using System.Collections.Concurrent;

namespace RuleHealth.Core.Auth;

/// <summary>
/// One tenant's sign-in: a refresh token per signing-in client and an access token per API. In
/// memory only, never written anywhere: a restart means signing in again (the price of never putting
/// tokens on disk). With the own app registration there is one client; with Microsoft's apps two
/// (Graph Command Line Tools for Graph, Azure PowerShell for ARM and Log Analytics).
/// </summary>
public sealed class TenantSession
{
    // Get a new access token this long before the current one expires.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly TokenStore _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<ApiResource, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();
    private readonly Dictionary<string, string> _refreshTokens = new(StringComparer.OrdinalIgnoreCase);

    internal TenantSession(TokenStore store, string alias, string tenantId, ClientProfile profile, string? upn, DateTimeOffset signedInAt)
    {
        _store = store;
        Alias = alias;
        TenantId = tenantId;
        Profile = profile;
        Upn = upn;
        SignedInAt = signedInAt;
    }

    public string Alias { get; }
    public string TenantId { get; }
    /// <summary>Which client signs in for which API (own app registration or Microsoft's apps).</summary>
    public ClientProfile Profile { get; }
    /// <summary>preferred_username of the id token: shown in the UI, never used for decisions.</summary>
    public string? Upn { get; }
    public DateTimeOffset SignedInAt { get; }

    /// <summary>
    /// A valid access token for <paramref name="resource"/>: the cached one, or a new one from the
    /// refresh token of the client that serves it. <paramref name="forceRefresh"/> skips the cache
    /// (after a 401). When the refresh token no longer works, the session ends: it is removed and
    /// the error says to sign in again.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(ApiResource resource, bool forceRefresh, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _tokens.TryGetValue(resource, out var cached) && cached.ExpiresAt - ExpiryMargin > _store.Now())
                return cached.Token;

            var clientId = Profile.ClientFor(resource);
            if (!_refreshTokens.TryGetValue(clientId, out var refreshToken))
                throw new RuleHealthException($"Signed out of '{Alias}': no sign-in for this API. Sign in again to continue.") { HttpStatus = 401 };

            TokenResponse response;
            try
            {
                response = await _store.Entra.RefreshAsync(TenantId, clientId, refreshToken, Profile.ScopeFor(resource), cancellationToken);
            }
            catch (EntraException ex) when (ex.NeedsSignIn)
            {
                _store.Remove(Alias);
                throw new RuleHealthException($"Signed out of '{Alias}': {ex.Message}\nSign in again to continue.", ex) { HttpStatus = 401 };
            }
            Put(resource, response);
            return response.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// "Sign in again and retry": drop the cached access tokens, so the next call gets newly issued ones
    /// from the refresh token (a token issued before a PIM activation does not carry the new role).
    /// </summary>
    public void ForgetAccessTokens()
    {
        _lock.Wait();
        try { _tokens.Clear(); }
        finally { _lock.Release(); }
    }

    internal void Put(ApiResource resource, TokenResponse response)
    {
        _tokens[resource] = (response.AccessToken, response.ExpiresAt);
        // Entra rotates refresh tokens: always keep the newest one of the client that issued it.
        if (!string.IsNullOrEmpty(response.RefreshToken))
            _refreshTokens[Profile.ClientFor(resource)] = response.RefreshToken;
    }
}

/// <summary>
/// The signed-in tenants of this process, keyed by tenant alias. Memory only; nothing here is ever
/// serialized (a test checks that a sign-in writes no file). Cleared by sign-out and by exiting.
/// </summary>
public sealed class TokenStore
{
    private readonly ConcurrentDictionary<string, TenantSession> _sessions = new(StringComparer.Ordinal);

    public TokenStore(EntraTokenClient entra, Func<DateTimeOffset>? now = null)
    {
        Entra = entra;
        Now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public EntraTokenClient Entra { get; }
    internal Func<DateTimeOffset> Now { get; }

    public TenantSession? Get(string alias) => _sessions.TryGetValue(alias, out var session) ? session : null;

    public IReadOnlyList<TenantSession> All => _sessions.Values.OrderBy(s => s.Alias, StringComparer.Ordinal).ToList();

    /// <summary>A sign-in with the user's own app registration: its first, Graph, token.</summary>
    public TenantSession Add(string alias, string tenantId, string clientId, string? upn, TokenResponse graph) =>
        Add(alias, tenantId, ClientProfile.OwnApp(clientId), upn, graph, null);

    /// <summary>
    /// Stores a new sign-in (replacing an older one for the alias) with the token of each sign-in step:
    /// Graph, and for Microsoft's apps also ARM (from Azure PowerShell).
    /// </summary>
    public TenantSession Add(string alias, string tenantId, ClientProfile profile, string? upn, TokenResponse graph, TokenResponse? arm)
    {
        if (string.IsNullOrEmpty(graph.RefreshToken) || (!profile.SingleClient && string.IsNullOrEmpty(arm?.RefreshToken)))
            throw new RuleHealthException("The sign-in returned no refresh token, so tokens for the other APIs cannot be requested. " +
                                          "offline_access must be allowed (it is by default).");
        var session = new TenantSession(this, alias, tenantId, profile, upn, Now());
        session.Put(ApiResource.Graph, graph);
        if (arm is not null)
            session.Put(ApiResource.Arm, arm);
        _sessions[alias] = session;
        return session;
    }

    public bool Remove(string alias) => _sessions.TryRemove(alias, out _);
}
