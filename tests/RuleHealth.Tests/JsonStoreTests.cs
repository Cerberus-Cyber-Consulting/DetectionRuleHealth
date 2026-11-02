using RuleHealth.Core;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

public sealed class JsonStoreTests : IDisposable
{
    private readonly string _root = TestPaths.NewTempDirectory();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static TenantDocument Tenant(string displayName) => new()
    {
        Alias = "contoso",
        DisplayName = displayName,
        Auth = new AuthSettings { Mode = AuthModes.Cli },
    };

    [Fact]
    public void Write_then_read_returns_the_same_document()
    {
        var store = new JsonStore(_root);

        store.Write("contoso", Documents.Tenant, Tenant("Contoso"));

        Assert.Equal(Tenant("Contoso"), store.Read<TenantDocument>("contoso", Documents.Tenant));
        Assert.Equal(new[] { "contoso" }, store.ListTenants());
        Assert.Equal(new[] { Documents.Tenant }, store.ListDocuments("contoso"));
    }

    [Fact]
    public void Second_write_keeps_one_backup_and_leaves_no_tmp()
    {
        var store = new JsonStore(_root);
        var path = store.DocumentPath("contoso", Documents.Tenant);

        store.Write("contoso", Documents.Tenant, Tenant("First"));
        store.Write("contoso", Documents.Tenant, Tenant("Second"));

        Assert.Equal("Second", store.Read<TenantDocument>("contoso", Documents.Tenant).DisplayName);
        Assert.Equal("First", JsonStore.ReadFile<TenantDocument>(path + ".bak").DisplayName);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Written_json_is_camel_case_utc_and_omits_absent_optionals()
    {
        var store = new JsonStore(_root);
        store.Write("contoso", Documents.Selections, new SelectionsDocument
        {
            Tenant = "contoso",
            UpdatedAt = new DateTime(2026, 10, 16, 6, 0, 0, DateTimeKind.Local),
        });

        var text = File.ReadAllText(store.DocumentPath("contoso", Documents.Selections));

        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Matches("\"updatedAt\": \"2026-10-1\\dT\\d\\d:00:00Z\"", text);
        Assert.DoesNotContain("demo", text);
    }

    [Fact]
    public void Wrong_schema_version_is_rejected_with_a_clear_message()
    {
        var store = new JsonStore(_root);
        var path = store.DocumentPath("contoso", Documents.Tenant);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "schemaVersion": 2, "alias": "contoso", "auth": { "mode": "cli" } }""");

        var ex = Assert.Throws<RuleHealthException>(() => store.Read<TenantDocument>("contoso", Documents.Tenant));

        Assert.Contains("schemaVersion", ex.Message);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Contoso")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Invalid_alias_is_rejected(string alias)
    {
        var store = new JsonStore(_root);

        Assert.Throws<RuleHealthException>(() => store.DocumentPath(alias, Documents.Tenant));
    }
}
