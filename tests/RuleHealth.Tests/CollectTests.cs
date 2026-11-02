using System.Net;
using System.Text.Json;
using RuleHealth.Core;
using RuleHealth.Core.Collection;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>
/// collect → rules.json + schema.json → extract, end to end over the fixtures: the documents a live
/// tenant produces must match the contract and feed the extraction like the markdown rules do.
/// </summary>
public sealed class CollectTests : IDisposable
{
    private readonly string _root = TestPaths.NewTempDirectory();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly TenantDocument Tenant = new()
    {
        Alias = "contoso",
        TenantId = "00000000-0000-0000-0000-000000000000",
        Auth = new AuthSettings { Mode = AuthModes.Browser, ClientId = "33333333-3333-3333-3333-333333333333" },
        Workspaces = new List<WorkspaceRef>
        {
            new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroup = "rg-sentinel",
                WorkspaceName = "law-Sentinel",
                WorkspaceId = "22222222-2222-2222-2222-222222222222",
            },
        },
    };

    private static FakeApi FullTenant() => new FakeApi()
        .Route("skipToken=page2", "sentinel-alertrules-page2.json")
        .Route("/alertRules?", "sentinel-alertrules-page1.json")
        .Route("/security/rules/detectionRules", "detectionrules.json")
        .Route("/tables?api-version=", "workspace-tables.json")
        .Route("getHuntingSchemaTables", "hunting-schema-tables.json");

    [Fact]
    public async Task Collected_documents_match_the_contract_and_extract()
    {
        using var api = FullTenant();
        var collector = new TenantCollector(api.Connection, _ => { });

        var rules = await collector.CollectRulesAsync(Tenant, includeSentinel: true, includeDefender: true, CancellationToken.None);
        var schema = await collector.CollectSchemaAsync(Tenant, CancellationToken.None);

        Assert.Empty(rules.Warnings);
        Assert.Equal(7, rules.Document.Rules.Count);
        Assert.Equal("arm-tables + graph-hunting", schema.Document.Source);
        Assert.Equal(new[] { "FileProfile", "DeviceFromIP" }, schema.Document.Functions);
        // ARM columns kept, advanced hunting adds what the workspace does not know.
        Assert.Equal("guid", schema.Document.Tables["SigninLogs"]["CorrelationId"]);
        Assert.Equal("string", schema.Document.Tables["SigninLogs"]["Location"]);
        Assert.Equal("datetime", schema.Document.Tables["DeviceProcessEvents"]["TimeGenerated"]);

        var store = new JsonStore(_root);
        store.Write("contoso", Documents.Rules, rules.Document);
        store.Write("contoso", Documents.Schema, schema.Document);
        AssertValid(store, Documents.Rules);
        AssertValid(store, Documents.Schema);

        var extracted = new RuleExtractor(store.Read<SchemaDocument>("contoso", Documents.Schema))
            .Extract("contoso", store.Read<RulesDocument>("contoso", Documents.Rules).Rules);

        // Only the four rules with KQL are analysed (MSIC/Fusion and the empty Defender query have none).
        Assert.Equal(4, extracted.Stats.QueriesAnalysed);
        Assert.Equal(0, extracted.Stats.QueriesWithBindErrors);
        var check = Assert.Single(extracted.Candidates.Checks, c => c.Value == "ProcessCreated");
        Assert.Equal(("DeviceProcessEvents", "ActionType", "eq", true), (check.Table, check.Column, check.OperatorClass, check.Validatable));
        Assert.Contains(extracted.Candidates.Checks, c => c is { Table: "SigninLogs", Column: "UserPrincipalName", Value: "bg1@contoso.com", OperatorClass: "eq_ci" });
        Assert.Contains(extracted.Candidates.Checks, c => c is { Table: "DeviceEvents", Value: "OpenProcessApiCall" });
    }

    [Fact]
    public async Task Missing_hunting_permission_still_writes_the_workspace_schema()
    {
        using var api = new FakeApi()
            .RouteBody("getHuntingSchemaTables", HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"no"}}""")
            .Route("/tables?api-version=", "workspace-tables.json");

        var schema = await new TenantCollector(api.Connection, _ => { }).CollectSchemaAsync(Tenant, CancellationToken.None);

        Assert.Equal("arm-tables", schema.Document.Source);
        Assert.Empty(schema.Document.Functions!); // no advanced hunting, so no FileProfile/DeviceFromIP
        var warning = Assert.Single(schema.Warnings);
        Assert.Contains("ThreatHunting.Read.All", warning);
    }

    [Fact]
    public async Task No_schema_source_at_all_is_an_error()
    {
        using var api = new FakeApi()
            .RouteBody("getHuntingSchemaTables", HttpStatusCode.Forbidden, "{}")
            .RouteBody("/tables?api-version=", HttpStatusCode.Forbidden, "{}");

        await Assert.ThrowsAsync<RuleHealthException>(
            () => new TenantCollector(api.Connection, _ => { }).CollectSchemaAsync(Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task A_failing_rule_source_fails_the_collection()
    {
        using var api = new FakeApi()
            .Route("skipToken=page2", "sentinel-alertrules-page2.json")
            .Route("/alertRules?", "sentinel-alertrules-page1.json")
            .RouteBody("/detectionRules", HttpStatusCode.Forbidden, "{}");
        var collector = new TenantCollector(api.Connection, _ => { });

        await Assert.ThrowsAsync<RuleHealthException>(
            () => collector.CollectRulesAsync(Tenant, includeSentinel: true, includeDefender: true, CancellationToken.None));
        var sentinelOnly = await collector.CollectRulesAsync(Tenant, includeSentinel: true, includeDefender: false, CancellationToken.None);
        Assert.Equal(4, sentinelOnly.Document.Rules.Count);
    }

    [Fact]
    public async Task The_same_rule_in_two_workspaces_is_kept_once_with_a_warning()
    {
        using var api = FullTenant();
        var second = Tenant.Workspaces![0] with { WorkspaceName = "law-Second" };
        var tenant = Tenant with { Workspaces = new List<WorkspaceRef> { Tenant.Workspaces[0], second } };

        var rules = await new TenantCollector(api.Connection, _ => { })
            .CollectRulesAsync(tenant, includeSentinel: true, includeDefender: false, CancellationToken.None);

        Assert.Equal(4, rules.Document.Rules.Count);
        Assert.Equal(4, rules.Warnings.Count);
        Assert.All(rules.Warnings, w => Assert.Contains("kept the one from workspace law-Sentinel", w));
    }

    private static void AssertValid(JsonStore store, string document)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(store.DocumentPath("contoso", document)));
        var errors = new MiniJsonSchema(Path.Combine(TestPaths.Schemas, document + ".schema.json")).Validate(json.RootElement);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
