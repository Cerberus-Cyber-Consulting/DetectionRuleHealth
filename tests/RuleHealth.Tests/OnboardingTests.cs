using System.Net;
using RuleHealth.Core;
using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Jobs;
using RuleHealth.Core.Model;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>The engine side of the setup wizard (skill rulehealth-onboarding, "Tests"). No live calls.</summary>
public sealed class OnboardingTests : IDisposable
{
    private const string TenantId = "aaaaaaaa-1111-4aaa-8aaa-111111111111";
    private readonly string _root = TestPaths.NewTempDirectory();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ------------------------------------------------------------------ wizard step

    private static TenantDocument Pkce(List<WorkspaceRef>? workspaces = null) => new()
    {
        Alias = "contoso", TenantId = TenantId, Workspaces = workspaces,
        Auth = new AuthSettings { Mode = AuthModes.Pkce, ClientId = "bbbbbbbb-2222-4bbb-8bbb-222222222222" },
    };

    public static IEnumerable<object?[]> StepCases()
    {
        var none = new List<WorkspaceRef>();
        yield return new object?[] { null, false, false, SetupProgress.App };              // nothing saved yet
        yield return new object?[] { Pkce(), false, false, SetupProgress.SignIn };          // tenant.json written
        yield return new object?[] { Pkce(), true, false, SetupProgress.Workspaces };       // signed in, workspace step not answered
        yield return new object?[] { Pkce(none), true, false, SetupProgress.Collect };      // "No Sentinel workspace" counts as answered
        yield return new object?[] { Pkce(none), false, false, SetupProgress.SignIn };      // collect needs a sign-in again after a restart
        yield return new object?[] { Pkce(none), false, true, SetupProgress.Done };         // collected data is readable signed out
        yield return new object?[] { Pkce() with { Auth = new AuthSettings { Mode = AuthModes.Offline } }, false, false, SetupProgress.Done };
        yield return new object?[] { Pkce() with { Demo = true }, false, false, SetupProgress.Done };
    }

    [Theory]
    [MemberData(nameof(StepCases))]
    public void Wizard_step_follows_from_the_files_and_the_sign_in(TenantDocument? tenant, bool signedIn, bool hasRules, string expected)
    {
        Assert.Equal(expected, SetupProgress.StepFor(tenant, signedIn, hasRules));
    }

    // ------------------------------------------------------------------ job stages

    private readonly JobManager _jobs = new(_ => { });

    [Fact]
    public async Task Stages_keep_their_first_order_and_take_the_latest_report()
    {
        var job = _jobs.Start("t", "collect", (_, stages, _) =>
        {
            stages.Report(new JobStage("A", StageStates.Pending));
            stages.Report(new JobStage("B", StageStates.Pending));
            stages.Report(new JobStage("B", StageStates.Running));
            stages.Report(new JobStage("A", StageStates.Succeeded, "3 rules", 3));
            stages.Report(new JobStage("B", StageStates.Succeeded, "1 table", 1));
            return Task.FromResult("done");
        });

        var finished = await _jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { ("A", StageStates.Succeeded, (int?)3), ("B", StageStates.Succeeded, (int?)1) },
            finished.Stages.Select(s => (s.Name, s.State, s.Count)));
        Assert.Null(finished.Hint);
    }

    [Fact]
    public async Task Cancelling_mid_stage_marks_the_running_stage_cancelled_and_the_rest_skipped()
    {
        var running = new TaskCompletionSource();
        var job = _jobs.Start("t", "collect", async (_, stages, token) =>
        {
            stages.Report(new JobStage("Rules", StageStates.Succeeded, null, 5));
            stages.Report(new JobStage("Incidents", StageStates.Running));
            stages.Report(new JobStage("Rule statistics", StageStates.Pending));
            running.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        });
        await running.Task;

        Assert.True(_jobs.Cancel(job.Id));
        var finished = await _jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(5));

        Assert.Equal(JobStates.Cancelled, finished.State);
        Assert.Equal(new[] { StageStates.Succeeded, StageStates.Cancelled, StageStates.Skipped }, finished.Stages.Select(s => s.State));
    }

    [Fact]
    public async Task A_failing_job_hands_its_hint_to_the_stage_it_stopped_in()
    {
        var job = _jobs.Start("t", "collect", (_, stages, _) =>
        {
            stages.Report(new JobStage("Rules", StageStates.Running));
            throw new RuleHealthException("Signed out of 't'.") { HttpStatus = 401, Hint = "Sign in again." };
        });

        var finished = await _jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(5));

        Assert.Equal((JobStates.Failed, "Sign in again."), (finished.State, finished.Hint));
        Assert.Equal((StageStates.Failed, "Sign in again."), (finished.Stages[0].State, finished.Stages[0].Hint));
    }

    [Fact]
    public async Task A_failed_stage_in_a_job_that_went_on_gives_the_job_its_hint()
    {
        var job = _jobs.Start("t", "collect", (_, stages, _) =>
        {
            stages.Report(new JobStage("Sentinel", StageStates.Failed, "HTTP 403", null, "Needs Microsoft Sentinel Reader."));
            stages.Report(new JobStage("Defender", StageStates.Succeeded, null, 2));
            return Task.FromResult("Collected 2 rules. Not collected: Sentinel");
        });

        var finished = await _jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(5));

        Assert.Equal((JobStates.Succeeded, "Needs Microsoft Sentinel Reader."), (finished.State, finished.Hint));
    }

    // ------------------------------------------------------------------ collect: partial failure

    private static readonly TenantDocument WithWorkspace = Pkce(new List<WorkspaceRef>
    {
        new() { SubscriptionId = "11111111-1111-1111-1111-111111111111", ResourceGroup = "rg-sentinel", WorkspaceName = "law-Sentinel", WorkspaceId = "22222222-2222-2222-2222-222222222222" },
    });

    private sealed class Recorder : IProgress<JobStage>
    {
        public Dictionary<string, JobStage> Stages { get; } = new();
        public void Report(JobStage value) => Stages[value.Name] = value;
    }

    [Fact]
    public async Task A_refused_rule_source_fails_only_its_stage_and_the_rest_is_written()
    {
        using var api = new FakeApi()
            .RouteBody("/alertRules?", HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"The client does not have authorization."}}""")
            .Route("/security/rules/detectionRules", "detectionrules.json")
            .Route("/v1.0/security/incidents?", "graph-incidents.json")
            .Route("/tables?api-version=", "workspace-tables.json")
            .Route("getHuntingSchemaTables", "hunting-schema-tables.json");
        var store = new JsonStore(_root);
        var stages = new Recorder();

        var summary = await CollectOperation.RunAsync(store, WithWorkspace, api.Connection, new CollectOptions(Days: 30), _ => { }, stages, CancellationToken.None);

        var sentinel = stages.Stages[CollectStages.Sentinel];
        Assert.Equal(StageStates.Failed, sentinel.State);
        Assert.Contains("Microsoft Sentinel Reader", sentinel.Hint);
        Assert.Equal(StageStates.Succeeded, stages.Stages[CollectStages.Defender].State);
        Assert.Equal(StageStates.Succeeded, stages.Stages[CollectStages.Schema].State);
        Assert.Equal(StageStates.Succeeded, stages.Stages[CollectStages.Incidents].State);
        Assert.Equal(StageStates.Succeeded, stages.Stages[CollectStages.RuleStats].State);
        Assert.EndsWith("Not collected: Microsoft Sentinel analytics rules", summary);
        var rules = store.Read<RulesDocument>("contoso", Documents.Rules).Rules;
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Equal(RuleSources.Defender, r.Source));
        Assert.True(store.Exists("contoso", Documents.Incidents));
        // No Sentinel rules, so no SecurityIncident query was sent.
        Assert.DoesNotContain(api.Requests, url => url.Contains("/query", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_rule_sources_refused_fails_the_run_and_writes_nothing()
    {
        using var api = new FakeApi()
            .RouteBody("/alertRules?", HttpStatusCode.Forbidden, "{}")
            .RouteBody("/security/rules/detectionRules", HttpStatusCode.Forbidden, "{}");
        var store = new JsonStore(_root);

        var ex = await Assert.ThrowsAsync<RuleHealthException>(() =>
            CollectOperation.RunAsync(store, WithWorkspace, api.Connection, new CollectOptions(), _ => { }, null, CancellationToken.None));

        Assert.Contains("Nothing was written", ex.Message);
        Assert.NotNull(ex.Hint);
        Assert.False(Directory.Exists(Path.Combine(_root, "contoso")));
    }

    [Fact]
    public async Task Without_a_workspace_the_sentinel_stage_is_skipped_with_the_reason()
    {
        using var api = new FakeApi()
            .Route("/security/rules/detectionRules", "detectionrules.json")
            .Route("/v1.0/security/incidents?", "graph-incidents.json")
            .Route("getHuntingSchemaTables", "hunting-schema-tables.json");
        var stages = new Recorder();

        await CollectOperation.RunAsync(new JsonStore(_root), Pkce(new List<WorkspaceRef>()), api.Connection, new CollectOptions(), _ => { }, stages, CancellationToken.None);

        Assert.Equal((StageStates.Skipped, "No Sentinel workspace selected"),
            (stages.Stages[CollectStages.Sentinel].State, stages.Stages[CollectStages.Sentinel].Detail));
        Assert.DoesNotContain(api.Requests, url => url.Contains("management.azure.com", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ workspace discovery

    private const string Sub1 = "11111111-1111-1111-1111-111111111111";
    private const string Sub4 = "44444444-4444-4444-4444-444444444444";

    private static string Workspace(string id, string name, string customerId, string state = "Succeeded") =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "location": "westeurope", "properties": { "customerId": "{{customerId}}", "provisioningState": "{{state}}" } }""";

    [Fact]
    public async Task Discovery_lists_this_tenants_workspaces_with_their_sentinel_state()
    {
        using var api = new FakeApi()
            .RouteBody("/workspaces/LAW-A/providers/Microsoft.SecurityInsights/onboardingStates", HttpStatusCode.OK, """{ "value": [ { "name": "default", "properties": {} } ] }""")
            .RouteBody("/workspaces/law-b/providers/Microsoft.SecurityInsights/onboardingStates", HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"x"}}""")
            .RouteBody("/workspaces/law-c/providers/Microsoft.SecurityInsights/onboardingStates", HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"x"}}""")
            .RouteBody($"/subscriptions/{Sub1}/providers/Microsoft.OperationalInsights/workspaces?", HttpStatusCode.OK, $$"""
                { "value": [
                  {{Workspace($"/subscriptions/{Sub1}/resourcegroups/rg-sec/providers/microsoft.operationalinsights/workspaces/law-a", "LAW-A", "c1c1c1c1-0000-0000-0000-000000000001")}},
                  {{Workspace($"/subscriptions/{Sub1}/resourcegroups/rg-sec/providers/microsoft.operationalinsights/workspaces/law-b", "law-b", "c1c1c1c1-0000-0000-0000-000000000002")}},
                  {{Workspace($"/subscriptions/{Sub1}/resourcegroups/rg-other/providers/microsoft.operationalinsights/workspaces/law-c", "law-c", "c1c1c1c1-0000-0000-0000-000000000003")}},
                  {{Workspace($"/subscriptions/{Sub1}/resourcegroups/rg-sec/providers/microsoft.operationalinsights/workspaces/law-gone", "law-gone", "c1c1c1c1-0000-0000-0000-000000000004", "Deleting")}}
                ] }
                """)
            .RouteBody($"/subscriptions/{Sub4}/providers/Microsoft.OperationalInsights/workspaces?", HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"x"}}""")
            .RouteBody("/subscriptions?api-version=2022-12-01", HttpStatusCode.OK, $$"""
                { "value": [
                  { "subscriptionId": "{{Sub1}}", "displayName": "Production", "state": "Enabled", "tenantId": "{{TenantId}}" },
                  { "subscriptionId": "22222222-2222-2222-2222-222222222222", "displayName": "Lighthouse customer", "state": "Enabled", "tenantId": "03dd93c4-dee8-44d6-ad99-baf2644d63f3" },
                  { "subscriptionId": "33333333-3333-3333-3333-333333333333", "displayName": "Old", "state": "Deleted", "tenantId": "{{TenantId}}" },
                  { "subscriptionId": "{{Sub4}}", "displayName": "No access", "state": "Warned", "tenantId": "{{TenantId}}" }
                ] }
                """);

        var result = await new DiscoveryClient(api.Connection).DiscoverAsync(TenantId, CancellationToken.None);

        Assert.Equal(2, result.Subscriptions); // Lighthouse and deleted subscriptions left out
        Assert.Equal(new[] { ("LAW-A", "rg-sec", SentinelStates.Enabled), ("law-c", "rg-other", SentinelStates.Unknown), ("law-b", "rg-sec", SentinelStates.NotEnabled) },
            result.Workspaces.Select(w => (w.WorkspaceName, w.ResourceGroup, w.Sentinel)));
        Assert.Equal(("Production", "c1c1c1c1-0000-0000-0000-000000000001"), (result.Workspaces[0].SubscriptionName, result.Workspaces[0].WorkspaceId));
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.StartsWith("No access:", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("law-c:", StringComparison.Ordinal) && w.Contains("Microsoft Sentinel Reader"));
        Assert.DoesNotContain(api.Requests, url => url.Contains("22222222-2222-2222-2222-222222222222/providers", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ tenant id lookup

    [Fact]
    public async Task A_domain_resolves_to_the_tenant_id_from_the_issuer()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, $$"""{"issuer":"https://login.microsoftonline.com/{{TenantId.ToUpperInvariant()}}/v2.0"}"""));

        Assert.Equal(TenantId, await TenantDiscovery.ResolveDomainAsync(" Contoso.onmicrosoft.com ", CancellationToken.None, http));
    }

    [Theory]
    [InlineData("contoso", 400)]                   // not a domain
    [InlineData("../contoso.com", 400)]
    [InlineData("contoso.com/v2.0?x=", 400)]
    [InlineData("unknown.example", 404)]           // Entra: AADSTS90002
    public async Task Bad_or_unknown_domains_are_refused(string domain, int status)
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.BadRequest,
            """{"error":"invalid_tenant","error_description":"AADSTS90002: Tenant 'unknown.example' not found."}"""));

        var ex = await Assert.ThrowsAsync<RuleHealthException>(() => TenantDiscovery.ResolveDomainAsync(domain, CancellationToken.None, http));

        Assert.Equal(status, ex.HttpStatus);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
