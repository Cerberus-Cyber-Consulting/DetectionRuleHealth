using System.Net;
using System.Text.Json;
using RuleHealth.Core;
using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;

namespace RuleHealth.Tests;

/// <summary>API clients parse recorded/doc-shaped responses from tests/Fixtures. No live calls.</summary>
public sealed class ApiClientTests
{
    private static readonly WorkspaceRef Workspace = new()
    {
        SubscriptionId = "11111111-1111-1111-1111-111111111111",
        ResourceGroup = "rg-sentinel",
        WorkspaceName = "law-Sentinel",
        WorkspaceId = "22222222-2222-2222-2222-222222222222",
    };

    private static FakeApi SentinelPages() => new FakeApi()
        .Route("skipToken=page2", "sentinel-alertrules-page2.json")
        .Route("/alertRules?", "sentinel-alertrules-page1.json");

    [Fact]
    public async Task A_next_link_to_another_host_is_not_followed()
    {
        // The token is sent with every page, so a next link must stay on the API's own host.
        using var api = new FakeApi().RouteBody("/alertRules?", HttpStatusCode.OK,
            """{ "value": [], "nextLink": "https://attacker.example/steal?api-version=2025-09-01" }""");

        var ex = await Assert.ThrowsAsync<RuleHealthException>(() => new SentinelRulesClient(api.Connection).ListAsync(Workspace, CancellationToken.None));

        Assert.Contains("another host (attacker.example)", ex.Message);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Sentinel_rules_follow_nextLink_and_read_each_kind_null_safe()
    {
        using var api = SentinelPages();

        var rules = await new SentinelRulesClient(api.Connection).ListAsync(Workspace, CancellationToken.None);

        Assert.Equal(2, api.Requests.Count);
        Assert.Contains("api-version=2025-09-01", api.Requests[0]);
        Assert.Equal(new[] { "Scheduled", "MicrosoftSecurityIncidentCreation", "NRT", "Fusion" }, rules.Select(r => r.Kind));

        var scheduled = rules[0];
        Assert.Equal("73e01a99-5cd7-4139-a149-9f2736ff2ab5", scheduled.RuleId);
        Assert.Equal(RuleSources.Sentinel, scheduled.Source);
        Assert.Equal("high", scheduled.Severity);
        Assert.Equal("PT1H", scheduled.Frequency);
        Assert.Null(scheduled.Description); // "" in the response
        Assert.True(scheduled.Enabled);
        Assert.StartsWith("DeviceProcessEvents", scheduled.QueryText);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), scheduled.LastModifiedAt);
        Assert.Equal(SchemaContexts.Sentinel, scheduled.SchemaContext);
        Assert.EndsWith("/alertRules/73e01a99-5cd7-4139-a149-9f2736ff2ab5", scheduled.Origin);

        var msic = rules[1];
        Assert.Null(msic.Severity); // only severitiesFilter
        Assert.Null(msic.QueryText);
        Assert.Null(msic.Description);

        var nrt = rules[2];
        Assert.False(nrt.Enabled);
        Assert.Null(nrt.Frequency); // NRT has no queryFrequency
        Assert.NotNull(nrt.QueryText);

        Assert.Null(rules[3].QueryText); // Fusion
    }

    [Fact]
    public async Task Detection_rules_read_status_frequency_and_slug_ids()
    {
        using var api = new FakeApi().Route("/security/rules/detectionRules", "detectionrules.json");

        var rules = (await new DetectionRulesClient(api.Connection).ListAsync(CancellationToken.None)).Select(d => d.Rule).ToList();

        Assert.Equal(3, rules.Count);
        var first = rules[0];
        Assert.Equal("office-encoded-powershell", first.RuleId);
        Assert.Equal(RuleSources.Defender, first.Source);
        Assert.Equal("customDetection", first.Kind);
        Assert.True(first.Enabled);
        Assert.Equal(RuleStatuses.Enabled, first.Status);
        Assert.Equal("high", first.Severity);
        Assert.Equal("PT1H", first.Frequency);
        Assert.Equal("An Office application started PowerShell with an encoded command.", first.Description);
        Assert.Equal(SchemaContexts.Unified, first.SchemaContext);

        var autoDisabled = rules[1];
        Assert.False(autoDisabled.Enabled);
        Assert.Equal(RuleStatuses.AutoDisabled, autoDisabled.Status);
        Assert.Equal("medium", autoDisabled.Severity);

        var unknown = rules[2];
        Assert.False(unknown.Enabled);
        Assert.Null(unknown.Status); // unknownFutureValue is not written into the contract enum
        Assert.Null(unknown.QueryText); // empty query
        Assert.EndsWith("/detectionRules/future%2Fstatus%20rule", unknown.Origin);
    }

    [Fact]
    public async Task Workspace_tables_combine_standard_and_custom_columns()
    {
        using var api = new FakeApi().Route("/tables?api-version=2026-03-01", "workspace-tables.json");
        var warnings = new List<string>();

        var tables = await new SchemaClient(api.Connection).GetWorkspaceTablesAsync(Workspace, warnings, CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal(new[] { "MyApp_CL", "SigninLogs" }, tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("datetime", tables["SigninLogs"]["TimeGenerated"]);
        Assert.Equal("bool", tables["SigninLogs"]["IsInteractive"]);
        Assert.Equal("guid", tables["SigninLogs"]["CorrelationId"]);
        // columns: null for Microsoft tables; custom tables have both lists. Every table also gets the
        // Log Analytics standard columns the API leaves out (Type, _ResourceId, ...): 6 listed + 8 added.
        Assert.Equal(14, tables["SigninLogs"].Count);
        Assert.Equal(new[] { "TimeGenerated", "TenantId", "Action", "Score", "Count" }, tables["MyApp_CL"].Keys.Take(5));
        Assert.Equal("string", tables["MyApp_CL"]["Type"]);
        Assert.Equal("real", tables["MyApp_CL"]["Score"]);
    }

    [Fact]
    public async Task Hunting_schema_maps_types_and_warns_once_per_unknown_type()
    {
        using var api = new FakeApi().Route("getHuntingSchemaTables(workspaceId=22222222", "hunting-schema-tables.json");
        var warnings = new List<string>();

        var tables = await new SchemaClient(api.Connection).GetHuntingTablesAsync(Workspace.WorkspaceId, warnings, CancellationToken.None);

        var process = tables["DeviceProcessEvents"];
        Assert.Equal("datetime", process["Timestamp"]);
        Assert.Equal("string", process["ActionType"]);
        Assert.Equal("long", process["ProcessId"]);
        Assert.Equal("dynamic", process["AdditionalFields"]);
        Assert.Equal("bool", process["IsElevated"]);
        Assert.Equal("dynamic", process["Embedding"]);
        var warning = Assert.Single(warnings);
        Assert.Contains("'Vector16'", warning);
        Assert.Contains("DeviceProcessEvents.Embedding", warning);
    }

    [Fact]
    public async Task Forbidden_names_what_is_needed_and_the_request_id()
    {
        using var api = new FakeApi().RouteBody("/detectionRules", HttpStatusCode.Forbidden,
            """{"error":{"code":"Forbidden","message":"Missing scope permissions on the request."}}""", requestId: "abc-123");

        var ex = await Assert.ThrowsAsync<RuleHealthException>(
            () => new DetectionRulesClient(api.Connection).ListAsync(CancellationToken.None));

        Assert.Contains("HTTP 403 Forbidden: Missing scope permissions", ex.Message);
        Assert.Contains("CustomDetection.Read.All", ex.Message);
        Assert.Contains("request-id: abc-123", ex.Message);
    }

    [Theory]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-sentinel/providers/Microsoft.OperationalInsights/workspaces/law-Sentinel")]
    [InlineData("subscriptions/11111111-1111-1111-1111-111111111111/resourcegroups/rg-sentinel/providers/microsoft.operationalinsights/workspaces/law-Sentinel/")]
    // Portal URLs: the workspace page, and a Sentinel blade with the id URL-encoded.
    [InlineData("https://portal.azure.com/#@contoso.onmicrosoft.com/resource/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-sentinel/providers/Microsoft.OperationalInsights/workspaces/law-Sentinel/overview")]
    [InlineData("https://portal.azure.com/#view/Microsoft_Azure_Security_Insights/MainMenuBlade/~/0/id/%2Fsubscriptions%2F11111111-1111-1111-1111-111111111111%2FresourceGroups%2Frg-sentinel%2Fproviders%2FMicrosoft.OperationalInsights%2Fworkspaces%2Flaw-Sentinel")]
    public void Workspace_resource_id_accepts_portal_forms(string id)
    {
        var parsed = WorkspaceClient.ParseResourceId(id);

        Assert.Equal(("11111111-1111-1111-1111-111111111111", "rg-sentinel", "law-Sentinel"),
            (parsed.SubscriptionId, parsed.ResourceGroup, parsed.WorkspaceName));
    }

    [Theory]
    [InlineData("law-Sentinel")]
    [InlineData("22222222-2222-2222-2222-222222222222")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.SecurityInsights/workspaces/x")]
    public void Workspace_resource_id_rejects_other_input(string id)
    {
        Assert.Throws<RuleHealthException>(() => WorkspaceClient.ParseResourceId(id));
    }

    [Fact]
    public async Task Workspace_lookup_reads_the_customer_id()
    {
        using var api = new FakeApi().Route("/workspaces/law-sentinel?api-version=2026-03-01", "workspace.json");
        var id = WorkspaceClient.ParseResourceId(
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-sentinel/providers/Microsoft.OperationalInsights/workspaces/law-sentinel");

        var workspace = await new WorkspaceClient(api.Connection).ResolveAsync(id, CancellationToken.None);

        Assert.Equal("22222222-2222-2222-2222-222222222222", workspace.WorkspaceId);
        Assert.Equal("law-Sentinel", workspace.WorkspaceName); // canonical casing from ARM
    }

    [Fact]
    public async Task Raw_log_keeps_every_body_as_valid_json_with_an_index()
    {
        var folder = TestPaths.NewTempDirectory();
        try
        {
            var raw = new RawResponseLog(folder);
            using (var api = new FakeApi(raw)
                       .Route("skipToken=page2", "sentinel-alertrules-page2.json")
                       .Route("/alertRules?", "sentinel-alertrules-page1.json"))
            {
                await new SentinelRulesClient(api.Connection).ListAsync(Workspace, CancellationToken.None);
            }

            var files = Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).ToList();
            Assert.Equal(2, files.Count);
            Assert.EndsWith("001-list-sentinel-analytics-rules-law-sentinel-200.json", files[0]);
            foreach (var file in files)
                JsonDocument.Parse(File.ReadAllText(file)).Dispose();
            Assert.Equal(2, File.ReadAllLines(Path.Combine(folder, "index.txt")).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Missing_resource_in_app_registration_names_the_permission_to_add()
    {
        // Real message from a first sign-in without Azure Service Management (2026-10-16).
        const string message =
            "AADSTS650057: Invalid resource. The client has requested access to a resource which is not listed in the " +
            "requested permissions in the client's application registration. Client app ID: bbbbbbbb-2222-4bbb-8bbb-222222222222. " +
            "Resource value from request: https://management.azure.com. Resource app ID: 797f4846-ba00-4fd7-ba43-dac1f8f63013. " +
            "List of valid resources from app registration: 00000003-0000-0000-c000-000000000000, ca7f3f0b-7d91-482c-8e09-c5d840d0eac5.";

        Assert.Contains("Azure Service Management > user_impersonation", AuthHints.For(message));
        Assert.Null(AuthHints.For("AADSTS50126: invalid username or password"));
    }

    [Fact]
    public async Task Tenant_check_tells_existing_missing_and_unknown_apart()
    {
        HttpClient Answer(HttpStatusCode status, string body) => new FakeApi()
            .RouteBody("login.microsoftonline.com", status, body).Connection.Graph;

        Assert.True(await TenantDiscovery.EnsureExistsAsync("aaaaaaaa-1111-4aaa-8aaa-111111111111", CancellationToken.None,
            Answer(HttpStatusCode.OK, """{"issuer":"https://login.microsoftonline.com/x/v2.0"}""")));
        var missing = await Assert.ThrowsAsync<RuleHealthException>(() => TenantDiscovery.EnsureExistsAsync(
            "03dd93c4-dee8-44d6-ad99-baf2644d63f3", CancellationToken.None,
            Answer(HttpStatusCode.BadRequest, """{"error":"invalid_tenant","error_description":"AADSTS90002: Tenant '03dd93c4-dee8-44d6-ad99-baf2644d63f3' not found."}""")));
        Assert.Contains("not a user's Object ID", missing.Message);
        // Anything else (proxy page, outage) is no verdict: the sign-in will report it.
        Assert.False(await TenantDiscovery.EnsureExistsAsync("aaaaaaaa-1111-4aaa-8aaa-111111111111", CancellationToken.None,
            Answer(HttpStatusCode.BadGateway, "<html>proxy</html>")));
    }

    [Theory]
    [InlineData("ERROR: AADSTS90002: Tenant '03dd93c4-dee8-44d6-ad99-baf2644d63f3' not found.", "not a user's Object ID")]
    [InlineData("AADSTS50011: The redirect URI 'http://localhost:5180/auth/callback' does not match.", "Mobile and desktop applications")]
    [InlineData("AADSTS7000218: The request body must contain the following parameter: 'client_assertion' or 'client_secret'.", "Do not add a secret")]
    [InlineData("AADSTS65001: The user or administrator has not consented to use the application.", "admin consent")]
    public void Entra_failures_say_what_to_do(string message, string hint)
    {
        Assert.Contains(hint, AuthHints.For(message));
    }

    [Fact]
    public void Token_summary_names_the_app_permissions_and_active_roles()
    {
        // Live 2026-10-26: the same Defender 403 for an app without the scope and for an account without
        // an (activated) role; the claims tell them apart. Unsigned test tokens, claims only.
        var withRole = Jwt.EncodeUnsigned(new
        {
            appid = "bbbbbbbb-2222-4bbb-8bbb-222222222222",
            scp = "CustomDetection.Read.All SecurityAlert.Read.All",
            wids = new[] { "b79fbf4d-3ef9-4689-8143-76b194e85509", "5d6b6bb7-de71-4623-b4af-96380a352509" },
        });
        var noRole = Jwt.EncodeUnsigned(new { azp = "bbbbbbbb-2222-4bbb-8bbb-222222222222", scp = "AuditLog.Read.All", wids = new[] { "b79fbf4d-3ef9-4689-8143-76b194e85509" } });

        Assert.Equal("Token used: app bbbbbbbb-2222-4bbb-8bbb-222222222222; permissions: CustomDetection.Read.All SecurityAlert.Read.All; " +
                     "directory roles active in the token: Security Reader.", TokenSummary.Describe(withRole));
        Assert.Equal("Token used: app bbbbbbbb-2222-4bbb-8bbb-222222222222; permissions: AuditLog.Read.All; directory roles active in the token: none.",
            TokenSummary.Describe(noRole));
        Assert.Null(TokenSummary.Describe("not-a-jwt"));
    }

    [Fact]
    public void A_403_says_which_token_was_used_and_what_to_do()
    {
        var token = Jwt.EncodeUnsigned(new { appid = "bbbbbbbb-2222-4bbb-8bbb-222222222222", scp = "CustomDetection.Read.All", wids = Array.Empty<string>() });
        var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/beta/security/rules/detectionRules");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        const string body = """{"error":{"code":"Forbidden","message":"Missing user permissions. API required permissions: SecurityData.Read, user permissions: ."}}""";

        var text = ApiConnection.DescribeFailure(new ApiCall("List Defender custom detection rules", "CustomDetection.Read.All"),
            request, new HttpResponseMessage(HttpStatusCode.Forbidden), body);

        Assert.Contains("Token used: app bbbbbbbb-2222-4bbb-8bbb-222222222222; permissions: CustomDetection.Read.All; directory roles active in the token: none.", text);
        Assert.Contains("activate it, then choose Sign in again", text);
        Assert.DoesNotContain(token, text); // never the token itself
    }

    [Fact]
    public async Task A_401_gets_a_new_access_token_once_then_returns_the_401()
    {
        var asked = new List<bool>();
        using var handler = new BearerTokenHandler((force, _) => { asked.Add(force); return Task.FromResult(force ? "fresh" : "cached"); }, null)
        {
            InnerHandler = new StatusHandler(HttpStatusCode.Unauthorized),
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/a");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(new[] { false, true }, asked); // cached token, then one forced refresh, no third try
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    [Fact]
    public void Add_tenant_stores_a_pkce_sign_in_with_the_users_own_client_id()
    {
        var folder = TestPaths.NewTempDirectory();
        try
        {
            var store = new RuleHealth.Core.Storage.JsonStore(folder);

            var tenant = RuleHealth.Core.Operations.TenantOperation.Prepare(store, new RuleHealth.Core.Operations.TenantRequest(
                "contoso", "AAAAAAAA-1111-4AAA-8AAA-111111111111", "BBBBBBBB-2222-4BBB-8BBB-222222222222"));

            Assert.Equal(AuthModes.Pkce, tenant.Auth.Mode);
            Assert.Equal("bbbbbbbb-2222-4bbb-8bbb-222222222222", tenant.Auth.ClientId);
            Assert.Equal("aaaaaaaa-1111-4aaa-8aaa-111111111111", tenant.TenantId);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Offline_tenants_cannot_sign_in()
    {
        var tenant = new TenantDocument { Alias = "hb", TenantId = "x", Auth = new AuthSettings { Mode = AuthModes.Offline } };

        var ex = Assert.Throws<RuleHealthException>(() => TenantAuth.Require(tenant));

        Assert.Contains("offline", ex.Message);
    }

    [Theory]
    [InlineData(AuthModes.Browser, null)]        // pre-M8 browser sign-in without a client id
    [InlineData(AuthModes.Cli, null)]            // pre-M8 Azure CLI sign-in: gone
    [InlineData(AuthModes.CliGraph, "bbbbbbbb-2222-4bbb-8bbb-222222222222")]
    public void Old_sign_in_modes_ask_to_add_the_tenant_again(string mode, string? clientId)
    {
        var tenant = new TenantDocument { Alias = "contoso", TenantId = "x", Auth = new AuthSettings { Mode = mode, ClientId = clientId } };

        var ex = Assert.Throws<RuleHealthException>(() => TenantAuth.Require(tenant));

        Assert.Contains("Connect it again", ex.Message);
        Assert.Equal(409, ex.HttpStatus);
    }

    [Fact]
    public void A_pre_M8_browser_tenant_with_a_client_id_signs_in_with_pkce()
    {
        var tenant = new TenantDocument
        {
            Alias = "contoso", TenantId = "aaaaaaaa-1111-4aaa-8aaa-111111111111",
            Auth = new AuthSettings { Mode = AuthModes.Browser, ClientId = "bbbbbbbb-2222-4bbb-8bbb-222222222222" },
        };

        Assert.Equal(("aaaaaaaa-1111-4aaa-8aaa-111111111111", ClientProfile.OwnApp("bbbbbbbb-2222-4bbb-8bbb-222222222222")), TenantAuth.Require(tenant));
    }
}
