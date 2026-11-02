using System.Net;
using System.Text;
using System.Text.Json;
using RuleHealth.Cli.Serve;
using RuleHealth.Core;
using RuleHealth.Core.Auth;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>
/// The PKCE sign-in and the request protection (skill rulehealth-auth, "Tests"). Entra is replaced by
/// <see cref="FakeEntra"/> replaying the token endpoint fixtures; nothing goes to the network.
/// </summary>
public sealed class AuthTests : IDisposable
{
    private const string TenantId = "aaaaaaaa-1111-4aaa-8aaa-111111111111";
    private const string ClientId = "bbbbbbbb-2222-4bbb-8bbb-222222222222";
    private const string Upn = "analyst@contoso.example";

    private readonly string _data = TestPaths.NewTempDirectory();
    private readonly FakeEntra _entra = new();
    private readonly StaticServer _server;

    public AuthTests()
    {
        new JsonStore(_data).Write("contoso", Documents.Tenant, new TenantDocument
        {
            Alias = "contoso",
            TenantId = TenantId,
            Auth = new AuthSettings { Mode = AuthModes.Pkce, ClientId = ClientId },
        });
        _server = TestServer.Start(_data, new EntraTokenClient(new HttpClient(_entra)));
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    // ------------------------------------------------------------------ authorize URL and PKCE

    [Fact]
    public void Pkce_challenge_is_base64url_sha256_of_the_verifier_without_padding()
    {
        // SHA-256("abc") = ba7816bf…f20015ad (FIPS 180-2); base64 "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=":
        // base64url swaps + and /, and drops the padding.
        Assert.Equal("ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0", Pkce.Challenge("abc"));

        var value = Pkce.NewRandomValue();
        Assert.Equal(43, value.Length); // 32 bytes
        Assert.DoesNotContain('=', value);
        Assert.DoesNotContain('+', value);
        Assert.DoesNotContain('/', value);
    }

    [Fact]
    public void Authorize_url_has_exactly_the_documented_parameters_and_encoding()
    {
        var url = PkceAuthorizationService.AuthorizeUrl(TenantId, ClientId, "http://localhost:5180/auth/callback", "st-1", "ch_1");

        Assert.Equal(
            $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={ClientId}&response_type=code" +
            "&redirect_uri=http%3A%2F%2Flocalhost%3A5180%2Fauth%2Fcallback&response_mode=query" +
            "&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default%20offline_access%20openid%20profile" +
            "&state=st-1&code_challenge=ch_1&code_challenge_method=S256&prompt=select_account", url);
    }

    [Fact]
    public void Begin_keeps_the_verifier_whose_challenge_goes_to_entra()
    {
        var service = new PkceAuthorizationService("http://localhost:5180/", new TokenStore(new EntraTokenClient(new HttpClient(_entra))));

        var query = Query(service.Begin(Contoso, "browser-1"));
        var pending = service.Take(query["state"], "browser-1");

        Assert.Equal("http://localhost:5180/auth/callback", service.RedirectUri);
        Assert.NotNull(pending);
        Assert.Equal(Pkce.Challenge(pending.Verifier), query["code_challenge"]);
        Assert.NotEqual(query["state"], pending.Verifier);
        Assert.Equal(("contoso", TenantId, ClientId), (pending.Alias, pending.TenantId, pending.ClientId));
    }

    // ------------------------------------------------------------------ state

    [Fact]
    public void State_is_single_use()
    {
        var service = new PkceAuthorizationService("http://localhost:5180", new TokenStore(new EntraTokenClient(new HttpClient(_entra))));
        var state = Query(service.Begin(Contoso, "browser-1"))["state"];

        Assert.NotNull(service.Take(state, "browser-1"));
        Assert.Null(service.Take(state, "browser-1"));
    }

    [Fact]
    public void State_expires_after_ten_minutes()
    {
        var now = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
        var service = new PkceAuthorizationService("http://localhost:5180", new TokenStore(new EntraTokenClient(new HttpClient(_entra)), () => now));
        var fresh = Query(service.Begin(Contoso, "browser-1"))["state"];
        var stale = Query(service.Begin(Contoso, "browser-1"))["state"];

        now += TimeSpan.FromMinutes(9);
        Assert.NotNull(service.Take(fresh, "browser-1"));
        now += TimeSpan.FromMinutes(2);
        Assert.Null(service.Take(stale, "browser-1"));
    }

    [Theory]
    [InlineData("browser-2")] // started in another browser (or by a page that made this browser start it)
    [InlineData(null)]        // callback without the session cookie
    public void State_from_another_browser_session_is_refused_and_used_up(string? session)
    {
        var service = new PkceAuthorizationService("http://localhost:5180", new TokenStore(new EntraTokenClient(new HttpClient(_entra))));
        var state = Query(service.Begin(Contoso, "browser-1"))["state"];

        Assert.Null(service.Take(state, session));
        Assert.Null(service.Take(state, "browser-1"));
    }

    // ------------------------------------------------------------------ token endpoint (fixtures)

    [Fact]
    public async Task Code_redemption_reads_the_success_fixture_and_sends_no_secret()
    {
        _entra.Reply(HttpStatusCode.OK, SuccessBody());
        var now = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

        var token = await new EntraTokenClient(new HttpClient(_entra), () => now)
            .RedeemCodeAsync(TenantId, ClientId, "the-code", "http://localhost:5180/auth/callback", "the-verifier", CancellationToken.None);

        Assert.Equal(("fake-graph-access-token", "fake-refresh-token-1"), (token.AccessToken, token.RefreshToken));
        Assert.Equal(now.AddSeconds(4523), token.ExpiresAt);
        var request = Assert.Single(_entra.Requests);
        Assert.Equal($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token", request.Url);
        Assert.Equal(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = "the-code",
            ["redirect_uri"] = "http://localhost:5180/auth/callback",
            ["code_verifier"] = "the-verifier",
            ["scope"] = "https://graph.microsoft.com/.default offline_access",
        }, request.Form);
    }

    [Theory]
    [InlineData("token-invalid-grant.json", "invalid_grant", "AADSTS70043", "00000000-0000-0000-0000-0000000000c1", "The refresh token has expired")]
    [InlineData("token-interaction-required.json", "interaction_required", "AADSTS50076", "00000000-0000-0000-0000-0000000000c2", "Multi-factor authentication is required")]
    public async Task Token_endpoint_errors_carry_code_correlation_and_hint(string fixture, string error, string code, string correlation, string mentions)
    {
        _entra.Reply(HttpStatusCode.BadRequest, Fixture(fixture));

        var ex = await Assert.ThrowsAsync<EntraException>(() => new EntraTokenClient(new HttpClient(_entra))
            .RefreshAsync(TenantId, ClientId, "rt", ApiResource.Arm, CancellationToken.None));

        Assert.Equal((error, code, correlation), (ex.Error, ex.Code, ex.CorrelationId));
        Assert.True(ex.NeedsSignIn);
        Assert.Contains(mentions, ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message); // only the first line of the description
    }

    [Fact]
    public async Task Other_resources_come_from_the_refresh_token_and_the_newest_one_is_kept()
    {
        var store = new TokenStore(new EntraTokenClient(new HttpClient(_entra)));
        var session = store.Add("contoso", TenantId, ClientId, Upn,
            new TokenResponse("graph-at", DateTimeOffset.UtcNow.AddHours(1), "rt-1", null));
        _entra.Reply(HttpStatusCode.OK, """{ "access_token": "arm-at", "expires_in": 3600, "refresh_token": "rt-2" }""");
        _entra.Reply(HttpStatusCode.OK, """{ "access_token": "la-at", "expires_in": 3600 }""");

        Assert.Equal("graph-at", await session.GetAccessTokenAsync(ApiResource.Graph, false, CancellationToken.None)); // cached, no request
        Assert.Equal("arm-at", await session.GetAccessTokenAsync(ApiResource.Arm, false, CancellationToken.None));
        Assert.Equal("la-at", await session.GetAccessTokenAsync(ApiResource.LogAnalytics, false, CancellationToken.None));

        Assert.Equal(2, _entra.Requests.Count);
        Assert.Equal(("refresh_token", "rt-1", "https://management.azure.com/.default offline_access"),
            (_entra.Requests[0].Form["grant_type"], _entra.Requests[0].Form["refresh_token"], _entra.Requests[0].Form["scope"]));
        Assert.Equal(("rt-2", "https://api.loganalytics.io/.default offline_access"),
            (_entra.Requests[1].Form["refresh_token"], _entra.Requests[1].Form["scope"]));
        Assert.All(_entra.Requests, r => Assert.False(r.Form.ContainsKey("client_secret")));
    }

    [Fact]
    public async Task A_refused_refresh_token_ends_the_session()
    {
        var store = new TokenStore(new EntraTokenClient(new HttpClient(_entra)));
        var session = store.Add("contoso", TenantId, ClientId, Upn, new TokenResponse("graph-at", DateTimeOffset.UtcNow.AddHours(1), "rt-1", null));
        _entra.Reply(HttpStatusCode.BadRequest, Fixture("token-invalid-grant.json"));

        var ex = await Assert.ThrowsAsync<RuleHealthException>(() => session.GetAccessTokenAsync(ApiResource.Arm, false, CancellationToken.None));

        Assert.Equal(401, ex.HttpStatus);
        Assert.Contains("Sign in again", ex.Message);
        Assert.Null(store.Get("contoso"));
    }

    // ------------------------------------------------------------------ sign-in through the server

    [Fact]
    public async Task Sign_in_end_to_end_keeps_the_tokens_in_memory_only()
    {
        var before = Snapshot();
        using var browser = TestServer.Browser(_server, followRedirects: false);

        var login = await browser.GetAsync("/auth/login?tenant=contoso");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorize = login.Headers.Location!.ToString();
        Assert.StartsWith($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={ClientId}&", authorize);
        var query = Query(authorize);
        Assert.Equal($"{TestServer.Origin(_server)}/auth/callback", query["redirect_uri"]);

        _entra.Reply(HttpStatusCode.OK, SuccessBody());
        var callback = await browser.GetAsync($"/auth/callback?code=the-code&state={Uri.EscapeDataString(query["state"]!)}&session_state=x");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/#/data", callback.Headers.Location?.OriginalString);
        // The code was redeemed with the verifier behind the challenge the browser carried.
        Assert.Equal(query["code_challenge"], Pkce.Challenge(Assert.Single(_entra.Requests).Form["code_verifier"]));

        var sessionText = await browser.GetStringAsync("/api/session");
        using (var session = JsonDocument.Parse(sessionText))
        {
            var signedIn = Assert.Single(session.RootElement.GetProperty("signedIn").EnumerateArray());
            Assert.Equal(("contoso", Upn, TenantId), (signedIn.GetProperty("alias").GetString(), signedIn.GetProperty("upn").GetString(), signedIn.GetProperty("tenantId").GetString()));
            Assert.True(session.RootElement.GetProperty("signIn").GetProperty("ok").GetBoolean());
        }
        using (var again = JsonDocument.Parse(await browser.GetStringAsync("/api/session")))
            Assert.Equal(JsonValueKind.Null, again.RootElement.GetProperty("signIn").ValueKind); // the notice is shown once
        Assert.DoesNotContain("fake-graph-access-token", sessionText);
        Assert.DoesNotContain("fake-refresh-token-1", sessionText);

        // Nothing written to the data folder, and no token in any file there.
        Assert.Equal(before, Snapshot());
        foreach (var file in Directory.GetFiles(_data, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("fake-graph-access-token", text);
            Assert.DoesNotContain("fake-refresh-token-1", text);
        }
        Assert.NotNull(_server.Tokens.Get("contoso"));
    }

    [Theory]
    [InlineData("setup", true, "/#/setup/workspaces")]
    [InlineData("setup", false, "/#/setup/signin")]
    [InlineData("settings", true, "/#/settings")]
    [InlineData("https://attacker.example/", true, "/#/data")]   // never a URL from the request
    [InlineData(null, true, "/#/data")]
    public async Task Callback_lands_where_the_sign_in_was_started_from(string? next, bool ok, string landing)
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);
        var login = await browser.GetAsync("/auth/login?tenant=contoso" + (next is null ? "" : $"&next={Uri.EscapeDataString(next)}"));
        var state = Uri.EscapeDataString(Query(login.Headers.Location!.ToString())["state"]!);
        _entra.Reply(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest, ok ? SuccessBody() : Fixture("token-invalid-grant.json"));

        var callback = await browser.GetAsync($"/auth/callback?code=c&state={state}");

        Assert.Equal(landing, callback.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Callback_with_an_unknown_or_reused_state_is_400_and_redeems_nothing()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);
        var state = Query((await browser.GetAsync("/auth/login?tenant=contoso")).Headers.Location!.ToString())["state"]!;
        _entra.Reply(HttpStatusCode.OK, SuccessBody());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync($"/auth/callback?code=c&state={Uri.EscapeDataString(state)}")).StatusCode);

        var reused = await browser.GetAsync($"/auth/callback?code=c&state={Uri.EscapeDataString(state)}");
        var unknown = await browser.GetAsync("/auth/callback?code=c&state=not-a-state");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (reused.StatusCode, unknown.StatusCode));
        Assert.Single(_entra.Requests);
    }

    [Fact]
    public async Task Callback_in_another_browser_is_400_and_redeems_nothing()
    {
        using var victim = TestServer.Browser(_server, followRedirects: false);
        using var other = TestServer.Browser(_server, followRedirects: false);
        var state = Query((await other.GetAsync("/auth/login?tenant=contoso")).Headers.Location!.ToString())["state"]!;

        var response = await victim.GetAsync($"/auth/callback?code=c&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_entra.Requests);
        Assert.Null(_server.Tokens.Get("contoso"));
    }

    [Fact]
    public async Task Sign_in_to_a_different_tenant_is_discarded()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);
        var state = Query((await browser.GetAsync("/auth/login?tenant=contoso")).Headers.Location!.ToString())["state"]!;
        _entra.Reply(HttpStatusCode.OK, SuccessBody(tid: "03dd93c4-dee8-44d6-ad99-baf2644d63f3"));

        await browser.GetAsync($"/auth/callback?code=c&state={Uri.EscapeDataString(state)}");

        Assert.Null(_server.Tokens.Get("contoso"));
        using var session = JsonDocument.Parse(await browser.GetStringAsync("/api/session"));
        var notice = session.RootElement.GetProperty("signIn");
        Assert.False(notice.GetProperty("ok").GetBoolean());
        Assert.Contains("different tenant", notice.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Entra_error_redirect_is_explained_once_in_the_ui()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);
        var state = Query((await browser.GetAsync("/auth/login?tenant=contoso")).Headers.Location!.ToString())["state"]!;
        var description = "AADSTS50011: The redirect URI 'http://localhost:5180/auth/callback' specified in the request does not match.\r\n" +
                          "Trace ID: x Correlation ID: 00000000-0000-0000-0000-0000000000c3 Timestamp: 2026-10-28";

        var response = await browser.GetAsync($"/auth/callback?error=invalid_request&error_description={Uri.EscapeDataString(description)}&state={Uri.EscapeDataString(state)}");

        Assert.Equal("/#/data", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain("AADSTS", response.Headers.Location!.OriginalString); // never in a URL or the history
        using var session = JsonDocument.Parse(await browser.GetStringAsync("/api/session"));
        var notice = session.RootElement.GetProperty("signIn");
        Assert.Equal(("contoso", "AADSTS50011", "00000000-0000-0000-0000-0000000000c3"),
            (notice.GetProperty("alias").GetString(), notice.GetProperty("code").GetString(), notice.GetProperty("correlationId").GetString()));
        Assert.Contains("Mobile and desktop applications", notice.GetProperty("hint").GetString());
        Assert.StartsWith("AADSTS50011: The redirect URI", notice.GetProperty("message").GetString());
        Assert.Empty(_entra.Requests);
    }

    [Fact]
    public async Task Admin_consent_return_is_handed_to_the_ui_without_redeeming_anything()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);

        var response = await browser.GetAsync($"/auth/callback?admin_consent=True&tenant={TenantId}");

        Assert.Equal("/#/data", response.Headers.Location?.OriginalString);
        using var session = JsonDocument.Parse(await browser.GetStringAsync("/api/session"));
        Assert.Equal("Admin consent granted. You can sign in now.", session.RootElement.GetProperty("signIn").GetProperty("message").GetString());
        Assert.Empty(_entra.Requests);
    }

    [Fact]
    public async Task Logout_drops_the_tenants_tokens()
    {
        _server.Tokens.Add("contoso", TenantId, ClientId, Upn, new TokenResponse("at", DateTimeOffset.UtcNow.AddHours(1), "rt", null));
        using var browser = TestServer.Browser(_server);

        var response = await browser.PostAsync("/auth/logout?tenant=contoso", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(_server.Tokens.Get("contoso"));
    }

    // ------------------------------------------------------------------ Host / Origin / CSRF

    [Theory]
    [InlineData("localhost:5180", HostVerdict.Allowed)]
    [InlineData("LOCALHOST:5180", HostVerdict.Allowed)]
    [InlineData("127.0.0.1:5180", HostVerdict.RedirectToPublic)]
    [InlineData("[::1]:5180", HostVerdict.RedirectToPublic)]
    [InlineData("localhost", HostVerdict.Misdirected)]
    [InlineData("localhost:5181", HostVerdict.Misdirected)]
    [InlineData("attacker.example:5180", HostVerdict.Misdirected)] // DNS rebinding
    [InlineData("localhost.attacker.example:5180", HostVerdict.Misdirected)]
    [InlineData(null, HostVerdict.Misdirected)]
    public void Host_header_matrix(string? host, HostVerdict expected)
    {
        Assert.Equal(expected, new RequestGuard("http://localhost:5180").ClassifyHost(host));
    }

    public enum WriteCase { Allowed, NoCsrf, WrongCsrf, NoOrigin, WrongOrigin, NullOrigin, NoCookie }

    [Theory]
    [InlineData(WriteCase.Allowed, HttpStatusCode.NoContent)]
    [InlineData(WriteCase.NoCsrf, HttpStatusCode.Forbidden)]
    [InlineData(WriteCase.WrongCsrf, HttpStatusCode.Forbidden)]
    [InlineData(WriteCase.NoOrigin, HttpStatusCode.Forbidden)]
    [InlineData(WriteCase.WrongOrigin, HttpStatusCode.Forbidden)]
    [InlineData(WriteCase.NullOrigin, HttpStatusCode.Forbidden)]  // sandboxed iframe / file:// page
    [InlineData(WriteCase.NoCookie, HttpStatusCode.Forbidden)]    // the token of another session
    public async Task Write_matrix_refuses_without_side_effects(WriteCase write, HttpStatusCode expected)
    {
        _server.Tokens.Add("contoso", TenantId, ClientId, Upn, new TokenResponse("at", DateTimeOffset.UtcNow.AddHours(1), "rt", null));
        using var browser = TestServer.Browser(_server);
        var csrf = browser.DefaultRequestHeaders.GetValues(RequestGuard.CsrfHeader).Single();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout?tenant=contoso");
        using var client = write == WriteCase.NoCookie ? new HttpClient { BaseAddress = new Uri(_server.BaseUrl) } : null;
        if (write == WriteCase.NoCookie)
        {
            request.Headers.Add("Origin", TestServer.Origin(_server));
            request.Headers.Add(RequestGuard.CsrfHeader, csrf);
        }
        else
        {
            browser.DefaultRequestHeaders.Remove(RequestGuard.CsrfHeader);
            browser.DefaultRequestHeaders.Remove("Origin");
            if (write != WriteCase.NoCsrf)
                request.Headers.Add(RequestGuard.CsrfHeader, write == WriteCase.WrongCsrf ? Pkce.NewRandomValue() : csrf);
            if (write != WriteCase.NoOrigin)
                request.Headers.Add("Origin", write switch
                {
                    WriteCase.WrongOrigin => "http://attacker.example",
                    WriteCase.NullOrigin => "null",
                    _ => TestServer.Origin(_server),
                });
        }

        var response = await (client ?? browser).SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.NoContent, _server.Tokens.Get("contoso") is null);
    }

    [Fact]
    public async Task Wrong_host_is_refused_before_any_route()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Host = $"attacker.example:{new Uri(_server.BaseUrl).Port}";

        var response = await http.SendAsync(request);

        // 421 from our Host check (container, http://+:5180/). For a host the prefix does not cover,
        // HttpListener itself refuses first: HTTP.sys on Windows with 400, the managed listener on Linux with 404.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.MisdirectedRequest, HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/session")]
    [InlineData("/api/tenants")]
    [InlineData("/nothing-here")]
    public async Task Every_response_has_the_security_headers_and_no_cors(string path)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", "http://attacker.example");

        var response = await http.SendAsync(request);

        Assert.Equal("default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-eval'; " +
                     "connect-src 'self'; frame-ancestors 'none'; form-action 'self' https://login.microsoftonline.com",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        if (path.StartsWith("/api", StringComparison.Ordinal))
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Session_cookie_is_http_only_and_lax()
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_server.BaseUrl) };

        var response = await http.GetAsync("/api/session");

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Matches(@"^drh_session=[A-Za-z0-9_-]{43}; HttpOnly; SameSite=Lax; Path=/$", cookie);
    }

    // ------------------------------------------------------------------ helpers

    private static TenantDocument Contoso => new()
    {
        Alias = "contoso",
        TenantId = TenantId,
        Auth = new AuthSettings { Mode = AuthModes.Pkce, ClientId = ClientId },
    };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FakeApi.Fixtures, name));

    private static string SuccessBody(string tid = TenantId) =>
        Fixture("token-success.json").Replace("{{id_token}}", Jwt.EncodeUnsigned(new { tid, preferred_username = Upn }));

    private static System.Collections.Specialized.NameValueCollection Query(string url) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);

    /// <summary>Every file under the data folder with its size and write time.</summary>
    private List<string> Snapshot() =>
        Directory.GetFiles(_data, "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(_data, f)}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f):O}")
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The Entra token endpoint: replies in order and records each form it was sent.</summary>
    private sealed class FakeEntra : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _replies = new();

        public List<(string Url, Dictionary<string, string> Form)> Requests { get; } = new();

        public void Reply(HttpStatusCode status, string body) => _replies.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            Requests.Add((request.RequestUri!.ToString(), form.AllKeys.ToDictionary(k => k!, k => form[k]!)));
            var (status, reply) = _replies.Count > 0 ? _replies.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
