using System.Text.Json;
using RuleHealth.Core;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>`rulehealth schema export`: a tenant's schema.json as a fixture, without what identifies the tenant.</summary>
public sealed class SchemaExportTests : IDisposable
{
    private readonly string _root = TestPaths.NewTempDirectory();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JsonStore StoreWithSchema()
    {
        var store = new JsonStore(Path.Combine(_root, "data"));
        store.Write("lab", Documents.Schema, new SchemaDocument
        {
            Tenant = "lab",
            GeneratedAt = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc),
            Source = "arm-tables + graph-hunting",
            Tables =
            {
                ["SigninLogs"] = new(StringComparer.Ordinal) { ["UserPrincipalName"] = "string", ["TimeGenerated"] = "datetime" },
                ["DeviceEvents"] = new(StringComparer.Ordinal) { ["ActionType"] = "string" },
                ["ContosoFirewall_CL"] = new(StringComparer.Ordinal) { ["Action_s"] = "string" },
                ["SigninLogs_SRCH"] = new(StringComparer.Ordinal) { ["UserPrincipalName"] = "string" },
                ["AuditLogs_RST"] = new(StringComparer.Ordinal) { ["OperationName"] = "string" },
            },
            TableInfo = new()
            {
                ["SigninLogs"] = new TableInfo { Workspaces = { "law-contoso-prod" }, Plan = TablePlans.Analytics, Hunting = true },
            },
            Functions = SchemaFileReader.UnifiedFunctions.ToList(),
        });
        return store;
    }

    [Fact]
    public void The_export_keeps_microsoft_tables_and_drops_what_identifies_the_tenant()
    {
        var file = Path.Combine(_root, "fixture", "unified-schema.json");

        var (export, dropped) = SchemaExportOperation.Run(StoreWithSchema(), "lab", file, includeCustom: false, _ => { });

        Assert.Equal(3, dropped);
        Assert.Equal(new[] { "DeviceEvents", "SigninLogs" }, export.Tables.Keys.Order(StringComparer.Ordinal));
        Assert.Null(export.TableInfo); // workspace names and plans stay in the tenant folder
        Assert.Equal("tenant-export:lab (2026-10-30, arm-tables + graph-hunting)", export.Source);
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("law-contoso-prod", text);
        Assert.DoesNotContain("_CL", text);
        Assert.False(File.Exists(file + ".bak"));

        // The fixture is a valid schema.json, and extraction reads it back.
        using var json = JsonDocument.Parse(text);
        Assert.Empty(new MiniJsonSchema(Path.Combine(TestPaths.Schemas, "schema.schema.json")).Validate(json.RootElement));
        Assert.Equal(2, SchemaFileReader.Read(file, "hybridbrothers").Tables.Count);
    }

    [Fact]
    public void Collected_workspace_tables_get_the_log_analytics_standard_columns()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FakeApi.Fixtures, "workspace-tables.json")));
        var items = json.RootElement.GetProperty("value").EnumerateArray().ToList();

        var tables = Core.Clients.SchemaClient.ReadWorkspaceTables(items, new List<string>());

        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.True(t.Value.ContainsKey("Type"), $"{t.Key} has no Type column"));
        Assert.All(tables, t => Assert.Equal("real", t.Value["_BilledSize"]));
        Assert.All(tables, t => Assert.Equal("datetime", t.Value["TimeGenerated"])); // an API-listed column keeps its type
    }

    [Fact]
    public void An_older_collect_exports_the_standard_columns_for_workspace_tables_only()
    {
        var (export, _) = SchemaExportOperation.Run(StoreWithSchema(), "lab", Path.Combine(_root, "std.json"), includeCustom: false, _ => { });

        Assert.Equal("string", export.Tables["SigninLogs"]["Type"]);      // a workspace table (tableInfo)
        Assert.Equal("string", export.Tables["SigninLogs"]["_ResourceId"]);
        Assert.False(export.Tables["DeviceEvents"].ContainsKey("Type"));  // not known as a workspace table
    }

    [Fact]
    public void Custom_tables_stay_on_request()
    {
        var (export, dropped) = SchemaExportOperation.Run(StoreWithSchema(), "lab", Path.Combine(_root, "all.json"), includeCustom: true, _ => { });

        Assert.Equal(0, dropped);
        Assert.Contains("ContosoFirewall_CL", export.Tables.Keys);
    }

    [Fact]
    public void A_tenant_without_a_collected_schema_says_what_to_do()
    {
        var ex = Assert.Throws<RuleHealthException>(() =>
            SchemaExportOperation.Run(new JsonStore(_root), "lab", Path.Combine(_root, "x.json"), false, _ => { }));

        Assert.Contains("collect this tenant in the UI first", ex.Message);
    }

    [Fact]
    public void Only_our_own_schema_files_are_read()
    {
        // The shape of a third-party schema file (top-level environments, no schemaVersion) is refused.
        var file = Path.Combine(_root, "other.json");
        File.WriteAllText(file, """{ "m365": { "tables": {} }, "sentinel": { "tables": {} } }""");

        var ex = Assert.Throws<RuleHealthException>(() => SchemaFileReader.Read(file, "x"));

        Assert.Contains("schema export", ex.Message);
    }
}
