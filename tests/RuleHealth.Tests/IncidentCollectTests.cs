using System.Net;
using System.Text.Json;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Collection;
using RuleHealth.Core.Model;
using RuleHealth.Core.Stats;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>
/// Incident collection over the fixtures. securityincident-query.json and graph-incidents.json are
/// built to hit every rule of the linking: resource-id/upper-case rule ids, redirected (merged)
/// incidents on both sides, a Defender-only row, a merge via ProviderIncidentId, the title fallback,
/// an unmatched alert, an unknown classification and a deleted rule.
/// </summary>
public sealed class IncidentCollectTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
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

    private static FakeApi Api() => new FakeApi()
        .Route("skipToken=page2", "sentinel-alertrules-page2.json")
        .Route("/alertRules?", "sentinel-alertrules-page1.json")
        .Route("/security/rules/detectionRules", "detectionrules.json")
        .Route("/v1/workspaces/22222222-2222-2222-2222-222222222222/query", "securityincident-query.json")
        .Route("/v1.0/security/incidents?", "graph-incidents.json");

    private static async Task<(CollectedRules Rules, CollectedIncidents Incidents, List<string> Progress)> CollectAsync(FakeApi api)
    {
        var progress = new List<string>();
        var rules = await new TenantCollector(api.Connection, progress.Add)
            .CollectRulesAsync(Tenant, includeSentinel: true, includeDefender: true, CancellationToken.None);
        var incidents = await new IncidentCollector(api.Connection, progress.Add)
            .CollectAsync(Tenant, rules, 90, includeSentinel: true, includeDefender: true, Now, CancellationToken.None);
        return (rules, incidents, progress);
    }

    [Fact]
    public async Task Incidents_are_linked_merged_and_filtered()
    {
        using var api = Api();

        var (_, collected, progress) = await CollectAsync(api);
        var incidents = collected.Document.Incidents.ToDictionary(i => i.IncidentId);

        // -0001 and -0005 from Sentinel; 1001, 1002 (merged with -0002) and 1006 from Defender.
        Assert.Equal(new[] { "1001", "1002", "1006", "aaaaaaaa-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000005" },
            incidents.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc), collected.Document.Window.From);

        var sentinel = incidents["aaaaaaaa-0000-4000-8000-000000000001"];
        Assert.Equal(new[] { "73e01a99-5cd7-4139-a149-9f2736ff2ab5" }, sentinel.RuleIds); // resource id, upper case → rules.json id
        Assert.Equal((RuleSources.Sentinel, 11, "high"), (sentinel.Source, sentinel.Number, sentinel.Severity));
        Assert.Equal((IncidentStatuses.Closed, Classifications.TruePositive), (sentinel.Status, sentinel.Classification));
        Assert.Equal(new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc), sentinel.ClosedAt);

        var deletedRule = incidents["aaaaaaaa-0000-4000-8000-000000000005"];
        Assert.Equal(new[] { "deadbeef-0000-4000-8000-000000000009" }, deletedRule.RuleIds);
        Assert.Equal(Classifications.Undetermined, deletedRule.Classification); // closed without a verdict

        var byDetectorId = incidents["1001"];
        Assert.Equal(new[] { "office-encoded-powershell" }, byDetectorId.RuleIds);
        Assert.Equal((RuleSources.Defender, IncidentStatuses.Closed, Classifications.TruePositive),
            (byDetectorId.Source, byDetectorId.Status, byDetectorId.Classification));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), byDetectorId.ClosedAt);

        // The rule's detectorId (different case) for the custom detection, plus the Sentinel rule from the
        // merged SecurityIncident row.
        var merged = incidents["1002"];
        Assert.Equal(new[] { "6c1f2a9e-1d55-4a7e-9a0c-3a2b5c7d9e01", "a1b2c3d4-0000-4000-8000-000000000002" }, merged.RuleIds);
        Assert.Equal((IncidentStatuses.Open, (string?)null), (merged.Status, merged.Classification));

        Assert.Equal(new[] { "future/status rule" }, incidents["1006"].RuleIds); // title fallback

        Assert.Contains("incidents sentinel law-Sentinel: 6 in 90 days, 3 linked to analytics rules, 1 merged into others and 1 deleted (skipped)", progress);
        Assert.Contains("incidents defender: 6 in 90 days, 3 with custom detection alerts, 1 merged into others (skipped)", progress);
        Assert.Contains("custom detection alerts → rules: 1 by detectorId, 1 by rule id, 1 by title, 1 unmatched", progress);
        Assert.Contains("incidents in both sources (ProviderIncidentId = Graph id): 1 merged", progress);

        Assert.Equal(2, collected.Warnings.Count);
        Assert.Contains(collected.Warnings, w => w.Contains("unknown incident classification 'someNewVerdict'"));
        Assert.Contains(collected.Warnings, w => w.Contains("1 custom detection alerts matched no rule") && w.Contains("Encoded PowerShell on HOST-042"));

        Assert.Contains(api.Requests, url =>
            Uri.UnescapeDataString(url).EndsWith("/v1.0/security/incidents?$filter=createdDateTime ge 2026-07-18T12:00:00Z&$expand=alerts"));
    }

    [Fact]
    public async Task An_incident_listed_on_two_pages_is_kept_once_in_its_latest_state()
    {
        // Live 2026-10-19: Graph pages with $skip, new incidents shift the pages and incident 23865 came
        // back twice, which crashed the merge ("An item with the same key has already been added").
        const string alert = """{ "id": "a", "title": "x", "detectionSource": "customDetection", "detectorId": "office-encoded-powershell" }""";
        string Incident(string status, string classification, string updated) => $$"""
            { "id": "23865", "displayName": "Dup", "severity": "high", "status": "{{status}}", "classification": "{{classification}}",
              "createdDateTime": "2026-10-18T08:00:00Z", "lastUpdateDateTime": "{{updated}}", "alerts": [ {{alert}} ] }
            """;
        using var api = new FakeApi()
            .Route("skipToken=page2", "sentinel-alertrules-page2.json")
            .Route("/alertRules?", "sentinel-alertrules-page1.json")
            .Route("/security/rules/detectionRules", "detectionrules.json")
            .RouteBody("$skip=50", HttpStatusCode.OK, $$"""{ "value": [ {{Incident("resolved", "truePositive", "2026-10-19T03:00:00Z")}} ] }""")
            .RouteBody("/v1.0/security/incidents?", HttpStatusCode.OK, $$"""
                { "value": [ {{Incident("active", "unknown", "2026-10-18T08:00:00Z")}} ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/security/incidents?$expand=alerts&$skip=50" }
                """);
        var progress = new List<string>();
        var rules = await new TenantCollector(api.Connection, progress.Add)
            .CollectRulesAsync(Tenant, includeSentinel: true, includeDefender: true, CancellationToken.None);

        var collected = await new IncidentCollector(api.Connection, progress.Add)
            .CollectAsync(Tenant, rules, 90, includeSentinel: false, includeDefender: true, Now, CancellationToken.None);

        var incident = Assert.Single(collected.Document.Incidents);
        Assert.Equal((IncidentStatuses.Closed, Classifications.TruePositive), (incident.Status, incident.Classification));
        Assert.Contains(progress, line => line.EndsWith("1 listed twice by Graph (kept once)"));
        Assert.Contains(collected.Warnings, w => w.Contains("listed 1 incident(s) twice"));
    }

    [Fact]
    public void Merge_combines_repeated_ids_instead_of_failing()
    {
        Incident Defender(string ruleId) => new()
        {
            IncidentId = "7", Source = RuleSources.Defender, Title = "t", CreatedAt = Now,
            Status = IncidentStatuses.Open, Classification = null, RuleIds = { ruleId },
        };

        var (incidents, _) = IncidentCollector.Merge(Array.Empty<(Incident, string?)>(), new[] { Defender("r1"), Defender("r2") });

        Assert.Equal(new[] { "r1", "r2" }, Assert.Single(incidents).RuleIds);
    }

    [Fact]
    public async Task Written_incidents_and_stats_match_the_contract()
    {
        using var api = Api();
        var (rules, collected, _) = await CollectAsync(api);
        var stats = RuleStatsCalculator.Calculate("contoso", collected.Document, rules.Document.Rules, Now);

        var store = new JsonStore(_root);
        store.Write("contoso", Documents.Incidents, collected.Document);
        store.Write("contoso", Documents.RuleStats, stats);
        foreach (var document in new[] { Documents.Incidents, Documents.RuleStats })
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(store.DocumentPath("contoso", document)));
            var errors = new MiniJsonSchema(Path.Combine(TestPaths.Schemas, document + ".schema.json")).Validate(json.RootElement);
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }

        var byRule = stats.Rules.ToDictionary(r => r.RuleId);
        Assert.Equal(8, byRule.Count); // 7 collected rules + the deleted one
        Assert.Equal((1, 1, 1.0), (byRule["73e01a99-5cd7-4139-a149-9f2736ff2ab5"].Incidents, byRule["73e01a99-5cd7-4139-a149-9f2736ff2ab5"].Tp, byRule["73e01a99-5cd7-4139-a149-9f2736ff2ab5"].TpRate!.Value));
        Assert.Equal((1, 1), (byRule["a1b2c3d4-0000-4000-8000-000000000002"].Incidents, byRule["a1b2c3d4-0000-4000-8000-000000000002"].SharedIncidents));
        Assert.Equal(0, byRule["BuiltInFusion"].Incidents);
    }

    [Fact]
    public void A_partial_log_analytics_result_keeps_the_rows_and_reports_the_error()
    {
        using var json = JsonDocument.Parse("""
            {
              "tables": [ { "name": "PrimaryResult", "columns": [ { "name": "n", "type": "long" } ], "rows": [ [ 1 ], [ 2 ] ] } ],
              "error": { "code": "PartialError", "message": "There were some errors when processing your query.",
                         "details": [ { "message": "Query result set has exceeded the internal record count limit" } ] }
            }
            """);

        var result = LogAnalyticsClient.ReadPrimaryTable(json.RootElement);

        Assert.Equal(new long?[] { 1, 2 }, result.Rows.Select(r => r.Long("n")));
        Assert.Equal("PartialError: There were some errors when processing your query. (Query result set has exceeded the internal record count limit)",
            result.PartialError);
        Assert.Empty(LogAnalyticsClient.ReadPrimaryTable(JsonDocument.Parse("{}").RootElement).Rows); // 204 No Content
    }

    [Fact]
    public void Sentinel_rule_ids_resolve_from_guid_or_resource_id()
    {
        var resolver = new SentinelRuleIds(new[]
        {
            new Rule
            {
                RuleId = "73e01a99-5cd7-4139-a149-9f2736ff2ab5", Source = RuleSources.Sentinel, Kind = "Scheduled",
                DisplayName = "x", Enabled = true, QueryText = null, SchemaContext = SchemaContexts.Sentinel,
            },
        });

        Assert.Equal("73e01a99-5cd7-4139-a149-9f2736ff2ab5", resolver.Resolve("73E01A99-5CD7-4139-A149-9F2736FF2AB5"));
        Assert.Equal("73e01a99-5cd7-4139-a149-9f2736ff2ab5", resolver.Resolve("/subscriptions/s/resourceGroups/r/providers/Microsoft.OperationalInsights/workspaces/w/providers/Microsoft.SecurityInsights/alertRules/73e01a99-5cd7-4139-a149-9f2736ff2ab5/"));
        Assert.Equal("unknown-guid", resolver.Resolve("UNKNOWN-GUID"));
    }

    [Fact]
    public void The_sentinel_query_skips_nothing_itself_and_flags_redirected()
    {
        var query = IncidentCollector.SentinelQuery(90);

        Assert.Contains("where CreatedTime >= ago(90d)", query);
        Assert.Contains("arg_max(TimeGenerated, *) by IncidentName", query);
        Assert.Contains("Redirected = tostring(Labels) has \"redirected\"", query);
    }
}
