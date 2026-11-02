using System.Net;
using System.Text;
using System.Text.Json;
using RuleHealth.Cli.Commands;
using RuleHealth.Cli.Serve;
using RuleHealth.Core;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;

namespace RuleHealth.Tests;

/// <summary>
/// Drives the real HttpListener server over loopback. Uses a temp copy of samples/ so the PUT test
/// never touches the committed sample selections.
/// </summary>
public sealed class ServeTests : IDisposable
{
    private readonly string _data = TestPaths.NewTempDirectory();
    private readonly StaticServer _server;
    private readonly HttpClient _http;

    public ServeTests()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.Samples, "hybridbrothers")))
        {
            var target = Path.Combine(_data, "hybridbrothers", Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        _server = TestServer.Start(_data);
        // Session cookie, CSRF token and Origin, like the UI; AuthTests checks writes without them are refused.
        _http = TestServer.Browser(_server);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public async Task Root_serves_index_html()
    {
        var response = await _http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
    }

    [Theory]
    [InlineData("/ui/brand/fonts/Montserrat-VariableFont_wght.ttf", "font/ttf")]
    [InlineData("/ui/brand/fonts/jetbrains-mono-latin-400-normal.woff2", "font/woff2")]
    [InlineData("/ui/brand/img/favicon.svg", "image/svg+xml")]
    [InlineData("/ui/brand/preview.html", "text/html; charset=utf-8")]
    public async Task Brand_files_are_served_with_their_content_type(string path, string contentType)
    {
        var response = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Tenants_lists_the_sample_tenant_with_its_files()
    {
        var response = await _http.GetAsync("/api/tenants");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var tenant = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("hybridbrothers", tenant.GetProperty("alias").GetString());
        var files = tenant.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("rules", files);
        Assert.Contains("candidates", files);
    }

    [Fact]
    public async Task Document_route_returns_the_file()
    {
        var text = await _http.GetStringAsync("/api/tenants/hybridbrothers/rules");
        using var json = JsonDocument.Parse(text);

        Assert.Equal(73, json.RootElement.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task Healthz_reports_ok_and_the_version_to_loopback_callers()
    {
        var response = await _http.GetAsync("/healthz");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(AppInfo.Version, json.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Health_command_sees_a_running_server()
    {
        Assert.True(await HealthCommand.CheckAsync(new Uri(_server.BaseUrl).Port, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Health_command_fails_when_nothing_listens()
    {
        Assert.False(await HealthCommand.CheckAsync(TestServer.FreePort(), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task App_route_returns_version_and_update_check_state()
    {
        var response = await _http.GetAsync("/api/app");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(AppInfo.Version, json.RootElement.GetProperty("version").GetString());
        Assert.Equal("enabled", json.RootElement.GetProperty("updateCheck").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("update").ValueKind);
    }

    [Fact]
    public async Task App_route_reports_a_disabled_update_check()
    {
        var data = TestPaths.NewTempDirectory();
        try
        {
            using var server = TestServer.Start(data, updateCheck: false);
            using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
            using var json = JsonDocument.Parse(await http.GetStringAsync("/api/app"));

            Assert.Equal("disabled", json.RootElement.GetProperty("updateCheck").GetString());
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Theory]
    [InlineData("/api/tenants/hybridbrothers/schema")]        // not a served document
    [InlineData("/api/tenants/nobody/rules")]                  // unknown tenant
    [InlineData("/api/tenants/..%2Fsamples/rules")]            // traversal via encoded slash
    [InlineData("/ui/..%2F..%2FCLAUDE.md")]                    // traversal out of ui/
    [InlineData("/ui/../CLAUDE.md")]
    [InlineData("/nothing-here")]
    public async Task Unknown_or_unsafe_paths_are_404(string path)
    {
        var response = await _http.GetAsync(path);

        // HTTP.sys (under HttpListener) rejects some encoded traversals itself with 403 before our
        // code runs; anything that reaches us gets 404. Either way nothing outside ui/ is served.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task Only_selections_can_be_written()
    {
        var response = await _http.PutAsync("/api/tenants/hybridbrothers/rules", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Put_selections_writes_atomically_and_keeps_a_backup()
    {
        var check = new CheckKey { Table = "DeviceEvents", Column = "ActionType", Path = null, OperatorClass = "eq", Value = "AntivirusDetection" };
        var document = new SelectionsDocument
        {
            Tenant = "hybridbrothers",
            UpdatedAt = DateTime.UtcNow,
            Decisions = { [CheckIdentity.Compute(check)] = new Decision { State = DecisionStates.Validate, DecidedAt = DateTime.UtcNow, Check = check } },
        };

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", Body(document));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var saved = await _http.GetStringAsync("/api/tenants/hybridbrothers/selections");
        Assert.Contains("AntivirusDetection", saved);
        Assert.True(File.Exists(Path.Combine(_data, "hybridbrothers", "selections.json.bak")));
    }

    [Fact]
    public async Task Put_selections_rejects_a_checkId_that_does_not_match_its_check()
    {
        var check = new CheckKey { Table = "T", Column = "C", Path = null, OperatorClass = "eq", Value = "v" };
        var document = new SelectionsDocument
        {
            Tenant = "hybridbrothers",
            UpdatedAt = DateTime.UtcNow,
            Decisions = { ["0000000000000000"] = new Decision { State = DecisionStates.Ignore, DecidedAt = DateTime.UtcNow, Check = check } },
        };

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", Body(document));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_selections_keeps_manual_ok_rule_statuses()
    {
        var document = new SelectionsDocument
        {
            Tenant = "hybridbrothers",
            UpdatedAt = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc),
            RuleStatuses = new Dictionary<string, RuleStatusDecision>
            {
                ["md:Azure/DetectExecutableDropViaAzure.md#1"] = new()
                {
                    State = RuleStatusStates.Ok, Note = "only seen during attacks",
                    DecidedAt = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc),
                    CoveredCheckIds = { "0123456789abcdef" },
                },
            },
        };

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", Body(document));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var saved = Core.Storage.JsonStore.ReadFile<SelectionsDocument>(Path.Combine(_data, "hybridbrothers", "selections.json"));
        var status = Assert.Single(saved.RuleStatuses!);
        Assert.Equal(("only seen during attacks", "0123456789abcdef"), (status.Value.Note, status.Value.CoveredCheckIds.Single()));
    }

    [Theory]
    [InlineData("""{ "state": "maybe", "decidedAt": "2026-10-26T06:00:00Z", "coveredCheckIds": [] }""", "state must be 'ok'")]
    [InlineData("""{ "state": "ok", "decidedAt": "2026-10-26T06:00:00Z", "coveredCheckIds": [ "NOT-A-CHECK" ] }""", "coveredCheckIds")]
    public async Task Put_selections_rejects_bad_rule_statuses(string status, string mentions)
    {
        var body = $$"""{ "schemaVersion": 1, "tenant": "hybridbrothers", "updatedAt": "2026-10-26T06:00:00Z", "decisions": {}, "ruleStatuses": { "r1": {{status}} } }""";

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(mentions, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_selections_keeps_rule_notes()
    {
        var document = new SelectionsDocument
        {
            Tenant = "hybridbrothers",
            UpdatedAt = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc),
            RuleNotes = new Dictionary<string, List<RuleNote>>
            {
                ["md:Azure/DetectExecutableDropViaAzure.md#1"] = new()
                {
                    new() { Id = "mh1x2-ab12cd", Text = "Checked with the SOC:\nno RMM tools in use.", Author = "Robbe",
                            CreatedAt = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc) },
                    new() { Id = "mh1x3-ef34gh", Text = "Re-checked after the agent rollout.",
                            CreatedAt = new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc) },
                },
            },
        };

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", Body(document));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var saved = Core.Storage.JsonStore.ReadFile<SelectionsDocument>(Path.Combine(_data, "hybridbrothers", "selections.json"));
        var notes = Assert.Single(saved.RuleNotes!).Value;
        Assert.Equal(new[] { "mh1x2-ab12cd", "mh1x3-ef34gh" }, notes.Select(n => n.Id));
        Assert.Equal(("Checked with the SOC:\nno RMM tools in use.", "Robbe"), (notes[0].Text, notes[0].Author));
        Assert.Null(notes[1].Author);
    }

    [Fact]
    public async Task Put_selections_keeps_rule_categories()
    {
        var document = new SelectionsDocument
        {
            Tenant = "hybridbrothers",
            UpdatedAt = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc),
            RuleCategories = new Dictionary<string, RuleCategoryAssignment>
            {
                ["md:Azure/DetectExecutableDropViaAzure.md#1"] = new()
                {
                    Categories = { "Missing events", "Needs tuning by customer" },
                    UpdatedAt = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc),
                },
            },
        };

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", Body(document));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var saved = Core.Storage.JsonStore.ReadFile<SelectionsDocument>(Path.Combine(_data, "hybridbrothers", "selections.json"));
        Assert.Equal(new[] { "Missing events", "Needs tuning by customer" }, Assert.Single(saved.RuleCategories!).Value.Categories);
    }

    [Theory]
    [InlineData("""{ "categories": [], "updatedAt": "2026-10-26T06:00:00Z" }""", "needs 1-20 categories")]
    [InlineData("""{ "categories": [ " Missing events" ], "updatedAt": "2026-10-26T06:00:00Z" }""", "category name")]
    [InlineData("""{ "categories": [ "Missing events", "missing events" ], "updatedAt": "2026-10-26T06:00:00Z" }""", "listed twice")]
    public async Task Put_selections_rejects_bad_rule_categories(string assignment, string mentions)
    {
        var body = $$"""{ "schemaVersion": 1, "tenant": "hybridbrothers", "updatedAt": "2026-10-26T06:00:00Z", "decisions": {}, "ruleCategories": { "r1": {{assignment}} } }""";

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(mentions, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""[ { "id": "a/b", "text": "x", "createdAt": "2026-10-26T06:00:00Z" } ]""", "note id")]
    [InlineData("""[ { "id": "a1", "text": "  ", "createdAt": "2026-10-26T06:00:00Z" } ]""", "note text")]
    [InlineData("""[ { "id": "a1", "text": "x", "createdAt": "2026-10-26T06:00:00Z" }, { "id": "a1", "text": "y", "createdAt": "2026-10-26T06:00:00Z" } ]""", "used twice")]
    public async Task Put_selections_rejects_bad_rule_notes(string notes, string mentions)
    {
        var body = $$"""{ "schemaVersion": 1, "tenant": "hybridbrothers", "updatedAt": "2026-10-26T06:00:00Z", "decisions": {}, "ruleNotes": { "r1": {{notes}} } }""";

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(mentions, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_selections_rejects_unknown_properties()
    {
        var body = """{ "schemaVersion": 1, "tenant": "hybridbrothers", "updatedAt": "2026-10-16T04:00:00Z", "decisions": {}, "extra": true }""";

        var response = await _http.PutAsync("/api/tenants/hybridbrothers/selections", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ write guard and jobs

    [Theory]
    [InlineData("PUT", "/api/tenants/hybridbrothers/selections")]
    [InlineData("POST", "/api/tenants/hybridbrothers/jobs")]
    [InlineData("POST", "/api/jobs/abc/cancel")]
    public async Task Writes_without_session_token_and_origin_are_refused(string method, string path)
    {
        // A page on another site can send a simple POST/PUT, but neither our Origin nor the CSRF token.
        using var plain = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("""{"kind":"collect"}""", Encoding.UTF8, "text/plain") };

        var response = await plain.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Refused", await response.Content.ReadAsStringAsync());
        Assert.Empty(_server.Jobs.ForTenant("hybridbrothers"));
    }

    [Fact]
    public async Task A_job_runs_in_the_background_and_reports_its_failure()
    {
        var started = await PostJob("""{ "kind": "collect" }""");

        Assert.Equal(HttpStatusCode.Accepted, started.Status);
        var job = await _server.Jobs.WaitAsync(started.Id, TimeSpan.FromSeconds(10));
        Assert.Equal("failed", job.State);
        Assert.Contains("offline tenant", job.Error);

        using var fetched = JsonDocument.Parse(await _http.GetStringAsync($"/api/jobs/{started.Id}"));
        Assert.Equal("failed", fetched.RootElement.GetProperty("state").GetString());
        using var list = JsonDocument.Parse(await _http.GetStringAsync("/api/tenants/hybridbrothers/jobs"));
        Assert.Equal(started.Id, list.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Extract_job_writes_candidates_from_the_collected_rules()
    {
        WriteSchemaFromCandidates();
        var before = File.GetLastWriteTimeUtc(CandidatesPath);

        var started = await PostJob("""{ "kind": "extract" }""");
        var job = await _server.Jobs.WaitAsync(started.Id, TimeSpan.FromSeconds(60));

        Assert.True(job.State == "succeeded", job.Error);
        Assert.StartsWith("Extracted ", job.Summary);
        Assert.Contains(job.Log, line => line.Contains("candidates.json"));
        Assert.True(File.GetLastWriteTimeUtc(CandidatesPath) > before);
    }

    [Theory]
    [InlineData("""{ "kind": "dance" }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "kind": "collect", "unexpected": 1 }""", HttpStatusCode.BadRequest)]
    [InlineData("not json", HttpStatusCode.BadRequest)]
    public async Task Bad_job_requests_are_rejected(string body, HttpStatusCode expected)
    {
        var response = await _http.PostAsync("/api/tenants/hybridbrothers/jobs", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Validation_plan_explains_what_is_missing()
    {
        // The sample has no schema.json: a state the UI explains, not a server error.
        var response = await _http.GetAsync("/api/tenants/hybridbrothers/validation-plan");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("schema.json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Validation_plan_returns_the_queries_without_sending_them()
    {
        WriteSchemaFromCandidates();

        using var json = JsonDocument.Parse(await _http.GetStringAsync("/api/tenants/hybridbrothers/validation-plan?days=7"));
        var root = json.RootElement;

        Assert.Equal(7, root.GetProperty("days").GetInt32());
        Assert.True(root.GetProperty("offline").GetBoolean());
        var batch = root.GetProperty("batches")[0];
        Assert.Contains("| summarize TableRows = count()", batch.GetProperty("kql").GetString());
        Assert.Contains("ago(7d)", batch.GetProperty("kql").GetString());
        Assert.Equal("loganalytics", batch.GetProperty("engine").GetString());
        Assert.StartsWith(root.GetProperty("selected").GetInt32() + " checks marked validate", root.GetProperty("summary").GetString());
        Assert.Empty(_server.Jobs.ForTenant("hybridbrothers"));
    }

    // ------------------------------------------------------------------ add tenant

    private const string ClientId = "bbbbbbbb-2222-4bbb-8bbb-222222222222";

    [Theory]
    [InlineData("""{ "alias": "Bad Alias", "tenantId": "00000000-0000-0000-0000-000000000000", "clientId": "bbbbbbbb-2222-4bbb-8bbb-222222222222" }""", "alias")]
    [InlineData("""{ "alias": "contoso", "tenantId": "not-a-guid", "clientId": "bbbbbbbb-2222-4bbb-8bbb-222222222222" }""", "tenant id")]
    [InlineData("""{ "alias": "contoso", "tenantId": "00000000-0000-0000-0000-000000000000" }""", "client id")]
    [InlineData("""{ "alias": "contoso", "tenantId": "00000000-0000-0000-0000-000000000000", "clientId": "not-a-guid" }""", "client id")]
    [InlineData("""{ "alias": "contoso", "tenantId": "00000000-0000-0000-0000-000000000000", "clientId": "bbbbbbbb-2222-4bbb-8bbb-222222222222", "auth": "cli" }""", "Invalid tenant request")]
    public async Task Add_tenant_refuses_bad_input_before_any_sign_in(string body, string mentions)
    {
        var response = await _http.PostAsync("/api/tenants", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(mentions, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_data, "contoso")));
    }

    [Fact]
    public async Task Add_tenant_refuses_an_alias_of_another_tenant()
    {
        var response = await _http.PostAsync("/api/tenants", new StringContent(
            $$"""{ "alias": "hybridbrothers", "tenantId": "00000000-0000-0000-0000-000000000000", "clientId": "{{ClientId}}" }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Add_tenant_writes_nothing_when_the_tenant_does_not_exist()
    {
        // Live 2026-10-26: a user's Object ID entered as tenant ID. The anonymous tenant check (stubbed here:
        // no live calls in tests) must refuse in one sentence before any sign-in, writing nothing.
        _server.TenantDiscoveryHttp = new HttpClient(new StubHandler(HttpStatusCode.BadRequest,
            """{"error":"invalid_tenant","error_description":"AADSTS90002: Tenant '00000000-0000-0000-0000-000000000000' not found."}"""));
        var response = await _http.PostAsync("/api/tenants", new StringContent(
            $$"""{ "alias": "contoso", "tenantId": "00000000-0000-0000-0000-000000000000", "clientId": "{{ClientId}}", "displayName": "Contoso" }""",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not exist in Entra ID", error);
        Assert.Contains("Object ID", error);
        Assert.False(File.Exists(Path.Combine(_data, "contoso", "tenant.json")));
    }

    [Fact]
    public async Task Add_tenant_writes_tenant_json_with_the_pkce_sign_in()
    {
        _server.TenantDiscoveryHttp = new HttpClient(new StubHandler(HttpStatusCode.OK, """{"issuer":"https://login.microsoftonline.com/x/v2.0"}"""));

        var response = await _http.PostAsync("/api/tenants", new StringContent(
            $$"""{ "alias": "contoso", "tenantId": "AAAAAAAA-1111-4AAA-8AAA-111111111111", "clientId": "{{ClientId}}" }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = Core.Storage.JsonStore.ReadFile<TenantDocument>(Path.Combine(_data, "contoso", "tenant.json"));
        Assert.Equal((AuthModes.Pkce, ClientId, "aaaaaaaa-1111-4aaa-8aaa-111111111111"), (tenant.Auth.Mode, tenant.Auth.ClientId, tenant.TenantId));
    }

    /// <summary>Answers every request with one canned response.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private string CandidatesPath => Path.Combine(_data, "hybridbrothers", "candidates.json");

    private async Task<(HttpStatusCode Status, string Id)> PostJob(string body)
    {
        var response = await _http.PostAsync("/api/tenants/hybridbrothers/jobs", new StringContent(body, Encoding.UTF8, "application/json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.GetProperty("id").GetString()!);
    }

    /// <summary>A minimal schema.json with tableInfo: every table/column the sample checks use, as strings, in one workspace.</summary>
    private void WriteSchemaFromCandidates()
    {
        var candidates = Core.Storage.JsonStore.ReadFile<CandidatesDocument>(CandidatesPath);
        var schema = new SchemaDocument { Tenant = "hybridbrothers", GeneratedAt = DateTime.UtcNow, Source = "test" };
        var info = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
        foreach (var check in candidates.Checks)
        {
            if (!schema.Tables.TryGetValue(check.Table, out var columns))
                schema.Tables[check.Table] = columns = new Dictionary<string, string>(StringComparer.Ordinal) { ["TimeGenerated"] = "datetime" };
            columns.TryAdd(check.Column, "string");
            info[check.Table] = new TableInfo { Workspaces = { "law" }, Plan = TablePlans.Analytics, Hunting = false };
        }
        new Core.Storage.JsonStore(_data).Write("hybridbrothers", Documents.Schema, schema with { TableInfo = info });
    }

    private static StringContent Body(SelectionsDocument document) =>
        new(JsonSerializer.Serialize(document, Core.Storage.Json.Options), Encoding.UTF8, "application/json");
}
