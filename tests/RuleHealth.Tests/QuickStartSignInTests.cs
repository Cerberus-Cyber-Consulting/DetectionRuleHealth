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
/// The quick start sign-in through Microsoft's apps (auth.mode "microsoft-apps", step 3b): Graph
/// Command Line Tools for Graph, then Azure PowerShell for ARM and Log Analytics, two PKCE sign-ins
/// in a row, back to the site root. Entra is the fake token endpoint; nothing goes to the network.
/// </summary>
public sealed class QuickStartSignInTests : IDisposable
{
    private const string TenantId = "aaaaaaaa-1111-4aaa-8aaa-111111111111";
    private const string Upn = "analyst@contoso.example";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";

    private readonly string _data = TestPaths.NewTempDirectory();
    private readonly FakeEntra _entra = new();
    private readonly StaticServer _server;

    public QuickStartSignInTests()
    {
        new JsonStore(_data).Write("contoso", Documents.Tenant, QuickStartTenant);
        _server = TestServer.Start(_data, new EntraTokenClient(new HttpClient(_entra)));
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    private static TenantDocument QuickStartTenant => new()
    {
        Alias = "contoso", TenantId = TenantId, Auth = new AuthSettings { Mode = AuthModes.MicrosoftApps },
    };

    // ------------------------------------------------------------------ profile

    [Fact]
    public void Microsoft_apps_use_graph_command_line_tools_for_graph_and_azure_powershell_for_the_rest()
    {
        var (tenantId, profile) = TenantAuth.Require(QuickStartTenant);

        Assert.Equal(TenantId, tenantId);
        Assert.Equal(ClientProfile.MicrosoftApps, profile);
        Assert.False(profile.SingleClient);
        Assert.Equal(ClientProfile.GraphCommandLineTools, profile.ClientFor(ApiResource.Graph));
        Assert.Equal(ClientProfile.AzurePowerShell, profile.ClientFor(ApiResource.Arm));
        Assert.Equal(ClientProfile.AzurePowerShell, profile.ClientFor(ApiResource.LogAnalytics));
        // Graph by name (.default would only give what was consented to Graph Command Line Tools before).
        Assert.Equal("https://graph.microsoft.com/CustomDetection.Read.All https://graph.microsoft.com/ThreatHunting.Read.All " +
                     "https://graph.microsoft.com/SecurityIncident.Read.All https://graph.microsoft.com/SecurityAlert.Read.All offline_access",
            profile.ScopeFor(ApiResource.Graph));
        Assert.Equal("https://management.azure.com/.default offline_access", profile.ScopeFor(ApiResource.Arm));
        Assert.Equal("https://api.loganalytics.io/.default offline_access", profile.ScopeFor(ApiResource.LogAnalytics));
        // Their registered redirect URI is http://localhost without a path: Entra ignores the port only.
        Assert.Equal("http://localhost:5180", profile.RedirectUri("http://localhost:5180/"));
        Assert.Equal("http://localhost:5180/auth/callback", ClientProfile.OwnApp("x").RedirectUri("http://localhost:5180"));
    }

    [Theory]
    [InlineData(false, "&prompt=select_account")]
    [InlineData(true, "&prompt=consent")]   // an administrator consenting for the organization
    public void Step_one_signs_in_as_graph_command_line_tools(bool consent, string prompt)
    {
        var service = new PkceAuthorizationService("http://localhost:5180", new TokenStore(new EntraTokenClient(new HttpClient(_entra))));

        var url = service.Begin(QuickStartTenant, "browser-1", consent: consent);

        Assert.StartsWith($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize?client_id={ClientProfile.GraphCommandLineTools}" +
                          "&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A5180&response_mode=query" +
                          "&scope=https%3A%2F%2Fgraph.microsoft.com%2FCustomDetection.Read.All%20", url);
        Assert.Contains("%20offline_access%20openid%20profile&state=", url);
        Assert.EndsWith(prompt, url);
    }

    // ------------------------------------------------------------------ two sign-ins through the server

    [Fact]
    public async Task Two_sign_ins_in_a_row_end_on_the_wizard_with_a_token_per_client()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);
        var redirect = TestServer.Origin(_server);

        var step1 = Query((await browser.GetAsync("/auth/login?tenant=contoso&next=setup")).Headers.Location!.ToString());
        Assert.Equal(ClientProfile.GraphCommandLineTools, step1["client_id"]);
        Assert.Equal(redirect, step1["redirect_uri"]);

        // Step 1 comes back to the site root and goes straight on to step 2.
        _entra.Reply(HttpStatusCode.OK, TokenBody("graph-at", "rt-graph", Oid));
        var afterStep1 = await browser.GetAsync($"/?code=c1&state={Uri.EscapeDataString(step1["state"]!)}");
        Assert.Equal(HttpStatusCode.Redirect, afterStep1.StatusCode);
        var step2 = Query(afterStep1.Headers.Location!.ToString());
        Assert.Equal(ClientProfile.AzurePowerShell, step2["client_id"]);
        Assert.Equal(Upn, step2["login_hint"]);
        Assert.Null(step2["prompt"]);
        Assert.Equal("https://management.azure.com/.default offline_access openid profile", step2["scope"]);
        Assert.Null(_server.Tokens.Get("contoso")); // not signed in before both steps are done

        _entra.Reply(HttpStatusCode.OK, TokenBody("arm-at", "rt-arm", Oid));
        var afterStep2 = await browser.GetAsync($"/?code=c2&state={Uri.EscapeDataString(step2["state"]!)}&session_state=x");
        Assert.Equal("/#/setup/workspaces", afterStep2.Headers.Location?.OriginalString);

        Assert.Equal(new[] { ClientProfile.GraphCommandLineTools, ClientProfile.AzurePowerShell }, _entra.Requests.Select(r => r.Form["client_id"]));
        Assert.All(_entra.Requests, r => Assert.Equal(redirect, r.Form["redirect_uri"]));
        Assert.Equal("https://management.azure.com/.default offline_access", _entra.Requests[1].Form["scope"]);
        Assert.Equal(Pkce.Challenge(_entra.Requests[0].Form["code_verifier"]), step1["code_challenge"]);
        Assert.Equal(Pkce.Challenge(_entra.Requests[1].Form["code_verifier"]), step2["code_challenge"]);

        // Each API gets its token from the refresh token of its own client.
        var session = _server.Tokens.Get("contoso")!;
        Assert.Equal(ClientProfile.MicrosoftApps, session.Profile);
        Assert.Equal("graph-at", await session.GetAccessTokenAsync(ApiResource.Graph, false, CancellationToken.None));
        Assert.Equal("arm-at", await session.GetAccessTokenAsync(ApiResource.Arm, false, CancellationToken.None));
        _entra.Reply(HttpStatusCode.OK, """{ "access_token": "la-at", "expires_in": 3600 }""");
        _entra.Reply(HttpStatusCode.OK, """{ "access_token": "graph-at-2", "expires_in": 3600 }""");
        Assert.Equal("la-at", await session.GetAccessTokenAsync(ApiResource.LogAnalytics, false, CancellationToken.None));
        Assert.Equal("graph-at-2", await session.GetAccessTokenAsync(ApiResource.Graph, true, CancellationToken.None));
        Assert.Equal((ClientProfile.AzurePowerShell, "rt-arm", "https://api.loganalytics.io/.default offline_access"),
            (_entra.Requests[2].Form["client_id"], _entra.Requests[2].Form["refresh_token"], _entra.Requests[2].Form["scope"]));
        Assert.Equal((ClientProfile.GraphCommandLineTools, "rt-graph"), (_entra.Requests[3].Form["client_id"], _entra.Requests[3].Form["refresh_token"]));

        using var json = JsonDocument.Parse(await browser.GetStringAsync("/api/session"));
        Assert.Equal(Upn, json.RootElement.GetProperty("signedIn")[0].GetProperty("upn").GetString());
    }

    [Fact]
    public async Task A_different_account_in_step_two_discards_the_sign_in()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);
        var step1 = Query((await browser.GetAsync("/auth/login?tenant=contoso&next=setup")).Headers.Location!.ToString());
        _entra.Reply(HttpStatusCode.OK, TokenBody("graph-at", "rt-graph", Oid));
        var step2 = Query((await browser.GetAsync($"/?code=c1&state={Uri.EscapeDataString(step1["state"]!)}")).Headers.Location!.ToString());

        _entra.Reply(HttpStatusCode.OK, TokenBody("arm-at", "rt-arm", "bbbbbbbb-0000-0000-0000-000000000002", "other@contoso.example"));
        var done = await browser.GetAsync($"/?code=c2&state={Uri.EscapeDataString(step2["state"]!)}");

        Assert.Equal("/#/setup/signin", done.Headers.Location?.OriginalString);
        Assert.Null(_server.Tokens.Get("contoso"));
        using var json = JsonDocument.Parse(await browser.GetStringAsync("/api/session"));
        Assert.Contains("two different accounts", json.RootElement.GetProperty("signIn").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/?code=abc")]                 // no state: the UI, not a callback
    [InlineData("/?state=abc")]                // no code or error
    [InlineData("/")]
    public async Task The_site_root_is_only_a_callback_with_state_and_code_or_error(string path)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };

        var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Empty(_entra.Requests);
    }

    [Fact]
    public async Task An_unknown_state_on_the_site_root_is_refused_like_on_the_callback_route()
    {
        using var browser = TestServer.Browser(_server, followRedirects: false);

        var response = await browser.GetAsync("/?code=c&state=not-a-state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_entra.Requests);
    }

    // ------------------------------------------------------------------ adding a tenant

    [Theory]
    [InlineData("""{ "alias": "fabrikam", "tenantId": "03dd93c4-dee8-44d6-ad99-baf2644d63f3", "signIn": "microsoft-apps" }""", HttpStatusCode.Created)]
    [InlineData("""{ "alias": "fabrikam", "tenantId": "03dd93c4-dee8-44d6-ad99-baf2644d63f3", "signIn": "own-app" }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "alias": "fabrikam", "tenantId": "03dd93c4-dee8-44d6-ad99-baf2644d63f3" }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "alias": "fabrikam", "tenantId": "03dd93c4-dee8-44d6-ad99-baf2644d63f3", "signIn": "cli-graph" }""", HttpStatusCode.BadRequest)]
    public async Task The_client_id_is_only_required_for_the_own_app_registration(string body, HttpStatusCode expected)
    {
        _server.TenantDiscoveryHttp = new HttpClient(new FakeEntra());
        using var browser = TestServer.Browser(_server);

        var response = await browser.PostAsync("/api/tenants", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Created)
        {
            var tenant = new JsonStore(_data).Read<TenantDocument>("fabrikam", Documents.Tenant);
            Assert.Equal((AuthModes.MicrosoftApps, (string?)null), (tenant.Auth.Mode, tenant.Auth.ClientId));
        }
    }

    [Fact]
    public async Task Switching_the_sign_in_option_ends_the_old_sign_in()
    {
        _server.Tokens.Add("contoso", TenantId, ClientProfile.MicrosoftApps, Upn,
            new TokenResponse("g", DateTimeOffset.UtcNow.AddHours(1), "rt-g", null), new TokenResponse("a", DateTimeOffset.UtcNow.AddHours(1), "rt-a", null));
        _server.TenantDiscoveryHttp = new HttpClient(new FakeEntra());
        using var browser = TestServer.Browser(_server);

        var response = await browser.PostAsync("/api/tenants", new StringContent(
            $$"""{ "alias": "contoso", "tenantId": "{{TenantId}}", "clientId": "bbbbbbbb-2222-4bbb-8bbb-222222222222", "signIn": "own-app" }""",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(_server.Tokens.Get("contoso"));
    }

    [Fact]
    public void Token_summary_names_microsofts_apps()
    {
        var token = Jwt.EncodeUnsigned(new { appid = ClientProfile.AzurePowerShell, scp = "user_impersonation", wids = Array.Empty<string>() });

        Assert.StartsWith("Token used: Microsoft Azure PowerShell; permissions: user_impersonation", TokenSummary.Describe(token));
    }

    // ------------------------------------------------------------------ helpers

    private static string TokenBody(string accessToken, string refreshToken, string oid, string upn = Upn) =>
        JsonSerializer.Serialize(new
        {
            token_type = "Bearer", expires_in = 3600, access_token = accessToken, refresh_token = refreshToken,
            id_token = Jwt.EncodeUnsigned(new { tid = TenantId, oid, preferred_username = upn }),
        });

    private static System.Collections.Specialized.NameValueCollection Query(string url) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);

    /// <summary>The Entra token endpoint: replies in order and records each form it was sent (an empty reply list answers 200 {}).</summary>
    private sealed class FakeEntra : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _replies = new();

        public List<(string Url, Dictionary<string, string> Form)> Requests { get; } = new();

        public void Reply(HttpStatusCode status, string body) => _replies.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            if (request.Method == HttpMethod.Post)
                Requests.Add((request.RequestUri!.ToString(), form.AllKeys.ToDictionary(k => k!, k => form[k]!)));
            var (status, reply) = _replies.Count > 0 ? _replies.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
