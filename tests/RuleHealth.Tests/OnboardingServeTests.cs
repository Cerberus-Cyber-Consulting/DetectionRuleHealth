using System.Net;
using System.Text;
using System.Text.Json;
using RuleHealth.Cli.Serve;
using RuleHealth.Core.Auth;
using RuleHealth.Core.Model;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>
/// The wizard's serve routes: tenants list with demo data and setup steps, tenant validation, settings,
/// removal, tenant id lookup. The demo source is a temp copy of samples/, the data folder a temp folder.
/// </summary>
public sealed class OnboardingServeTests : IDisposable
{
    private const string TenantId = "aaaaaaaa-1111-4aaa-8aaa-111111111111";
    private const string ClientId = "bbbbbbbb-2222-4bbb-8bbb-222222222222";

    private readonly string _data = TestPaths.NewTempDirectory();
    private readonly string _samples = TestPaths.NewTempDirectory();
    private readonly StaticServer _server;
    private readonly HttpClient _http;

    public OnboardingServeTests()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.Samples, "hybridbrothers")))
        {
            Directory.CreateDirectory(Path.Combine(_samples, "hybridbrothers"));
            File.Copy(file, Path.Combine(_samples, "hybridbrothers", Path.GetFileName(file)));
        }
        new JsonStore(_data).Write("contoso", Documents.Tenant, new TenantDocument
        {
            Alias = "contoso", TenantId = TenantId, DisplayName = "Contoso",
            Auth = new AuthSettings { Mode = AuthModes.Pkce, ClientId = ClientId },
        });
        _server = TestServer.Start(_data, samples: _samples);
        _http = TestServer.Browser(_server);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        Directory.Delete(_data, recursive: true);
        Directory.Delete(_samples, recursive: true);
    }

    private async Task<JsonElement> Tenant(string alias)
    {
        using var json = JsonDocument.Parse(await _http.GetStringAsync("/api/tenants"));
        return json.RootElement.EnumerateArray().Single(t => t.GetProperty("alias").GetString() == alias).Clone();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ------------------------------------------------------------------ tenants list

    [Fact]
    public async Task Tenants_list_own_tenants_and_the_read_only_demo_with_their_setup_step()
    {
        var contoso = await Tenant("contoso");
        var demo = await Tenant("hybridbrothers");

        Assert.Equal((false, false, SetupProgress.SignIn), (contoso.GetProperty("demo").GetBoolean(), contoso.GetProperty("readOnly").GetBoolean(), contoso.GetProperty("setupStep").GetString()));
        Assert.Equal((true, true, SetupProgress.Done), (demo.GetProperty("demo").GetBoolean(), demo.GetProperty("readOnly").GetBoolean(), demo.GetProperty("setupStep").GetString()));
        Assert.Equal(73, JsonDocument.Parse(await _http.GetStringAsync("/api/tenants/hybridbrothers/rules")).RootElement.GetProperty("rules").GetArrayLength());

        _server.Tokens.Add("contoso", TenantId, ClientId, null, new TokenResponse("at", DateTimeOffset.UtcNow.AddHours(1), "rt", null));
        Assert.Equal(SetupProgress.Workspaces, (await Tenant("contoso")).GetProperty("setupStep").GetString());
    }

    [Theory]
    [InlineData("PUT", "/api/tenants/hybridbrothers/selections", """{ "schemaVersion": 1, "tenant": "hybridbrothers", "updatedAt": "2026-10-28T06:00:00Z", "decisions": {} }""")]
    [InlineData("POST", "/api/tenants/hybridbrothers/jobs", """{ "kind": "extract" }""")]
    [InlineData("PUT", "/api/tenants/hybridbrothers/settings", """{ "displayName": "x" }""")]
    [InlineData("PUT", "/api/tenants/hybridbrothers/workspaces", """{ "workspaces": [] }""")]
    [InlineData("DELETE", "/api/tenants/hybridbrothers", null)]
    public async Task Demo_data_is_read_only(string method, string path, string? body)
    {
        var before = Directory.GetFiles(_samples, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : Json(body) };

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("read-only", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, Directory.GetFiles(_samples, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList());
        Assert.Empty(_server.Jobs.ForTenant("hybridbrothers"));
    }

    // ------------------------------------------------------------------ POST /api/tenants

    [Theory]
    [InlineData("../contoso")]
    [InlineData("..")]
    [InlineData(".app")]
    [InlineData("con/toso")]
    [InlineData("con\\\\toso")]
    [InlineData("CONTOSO")]
    [InlineData("")]
    public async Task Add_tenant_refuses_aliases_that_could_leave_the_data_folder(string alias)
    {
        var response = await _http.PostAsync("/api/tenants", Json($$"""{ "alias": "{{alias}}", "tenantId": "{{TenantId}}", "clientId": "{{ClientId}}" }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new[] { "contoso" }, Directory.GetDirectories(_data).Select(Path.GetFileName));
        // "../contoso" must not have created a folder next to the data folder either.
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_data)!, "contoso")));
    }

    [Fact]
    public async Task Add_tenant_refuses_the_alias_of_the_demo_data()
    {
        var response = await _http.PostAsync("/api/tenants", Json($$"""{ "alias": "hybridbrothers", "tenantId": "{{TenantId}}", "clientId": "{{ClientId}}" }"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_data, "hybridbrothers")));
    }

    [Fact]
    public async Task Adding_the_same_tenant_again_keeps_its_workspaces()
    {
        // The wizard's Back button: correct the client id and continue.
        var store = new JsonStore(_data);
        store.Write("contoso", Documents.Tenant, store.Read<TenantDocument>("contoso", Documents.Tenant) with { Workspaces = new List<WorkspaceRef>() });
        _server.TenantDiscoveryHttp = new HttpClient(new StubHandler(HttpStatusCode.OK, "{}"));

        var response = await _http.PostAsync("/api/tenants", Json($$"""{ "alias": "contoso", "tenantId": "{{TenantId}}", "clientId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" }"""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = store.Read<TenantDocument>("contoso", Documents.Tenant);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", tenant.Auth.ClientId);
        Assert.NotNull(tenant.Workspaces);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_update_the_name_and_windows()
    {
        var response = await _http.PutAsync("/api/tenants/contoso/settings",
            Json("""{ "displayName": "Contoso production", "defaults": { "incidentDays": 180, "validationDays": 14 } }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenant = new JsonStore(_data).Read<TenantDocument>("contoso", Documents.Tenant);
        Assert.Equal(("Contoso production", 180, 14), (tenant.DisplayName, tenant.Defaults!.IncidentDays, tenant.Defaults.ValidationDays));
        Assert.Equal(ClientId, tenant.Auth.ClientId); // nothing else changes
    }

    [Theory]
    [InlineData("""{ "defaults": { "validationDays": 31 } }""", "1 to 30")]
    [InlineData("""{ "defaults": { "incidentDays": 0 } }""", "1 to 365")]
    [InlineData("""{ "displayName": "x", "clientId": "y" }""", "Invalid settings")]
    public async Task Settings_refuse_bad_values(string body, string mentions)
    {
        var response = await _http.PutAsync("/api/tenants/contoso/settings", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(mentions, await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ remove tenant

    [Fact]
    public async Task Removing_a_tenant_deletes_its_folder_and_its_sign_in()
    {
        _server.Tokens.Add("contoso", TenantId, ClientId, null, new TokenResponse("at", DateTimeOffset.UtcNow.AddHours(1), "rt", null));

        var response = await _http.DeleteAsync("/api/tenants/contoso");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_data, "contoso")));
        Assert.Null(_server.Tokens.Get("contoso"));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync("/api/tenants/contoso")).StatusCode);
    }

    [Fact]
    public async Task A_tenant_with_a_running_job_is_not_removed()
    {
        var gate = new TaskCompletionSource<string>();
        _server.Jobs.Start("contoso", "collect", (_, _) => gate.Task);

        var response = await _http.DeleteAsync("/api/tenants/contoso");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(_data, "contoso")));
        gate.SetResult("ok");
    }

    [Theory]
    [InlineData("/api/tenants/..%2Fcontoso")]
    [InlineData("/api/tenants/.app")]
    [InlineData("/api/tenants/nobody")]
    public async Task Remove_refuses_unknown_and_unsafe_aliases(string path)
    {
        var response = await _http.DeleteAsync(path);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        Assert.True(Directory.Exists(Path.Combine(_data, "contoso")));
    }

    // ------------------------------------------------------------------ lookup and discover

    [Fact]
    public async Task Lookup_turns_a_domain_into_the_tenant_id()
    {
        _server.TenantDiscoveryHttp = new HttpClient(new StubHandler(HttpStatusCode.OK, $$"""{"issuer":"https://login.microsoftonline.com/{{TenantId}}/v2.0"}"""));

        using var json = JsonDocument.Parse(await _http.GetStringAsync("/api/lookup-tenant?domain=contoso.onmicrosoft.com"));
        var bad = await _http.GetAsync("/api/lookup-tenant?domain=not%20a%20domain");

        Assert.Equal(TenantId, json.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Discover_needs_a_sign_in()
    {
        var response = await _http.GetAsync("/api/tenants/contoso/discover");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Not signed in", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task App_route_names_the_data_folder()
    {
        using var json = JsonDocument.Parse(await _http.GetStringAsync("/api/app"));

        Assert.Equal(Path.GetFullPath(_data), json.RootElement.GetProperty("dataFolder").GetString());
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
