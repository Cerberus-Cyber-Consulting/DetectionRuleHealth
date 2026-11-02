using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Operations;

/// <summary>
/// `schema export`: turns a tenant's collected schema.json into a test fixture for offline extraction
/// (the golden test, `extract --schema-file`). Reads files only, no sign-in. Keeps Microsoft table
/// names, column names and types; drops everything that says something about the tenant: the table
/// info (workspace names, plans), custom log tables (`_CL`), search-job results (`_SRCH`) and restored
/// tables (`_RST`), unless <c>includeCustom</c>. The tenant id is never in schema.json to begin with.
/// </summary>
public static class SchemaExportOperation
{
    /// <summary>Suffixes of tables a tenant creates itself (custom logs, search jobs, restores).</summary>
    public static readonly IReadOnlyList<string> TenantTableSuffixes = new[] { "_CL", "_SRCH", "_RST" };

    /// <returns>The exported document (written to <paramref name="outFile"/>) and how many tables were dropped.</returns>
    public static (SchemaDocument Export, int Dropped) Run(JsonStore store, string alias, string outFile, bool includeCustom, Action<string> log)
    {
        var schema = store.TryRead<SchemaDocument>(alias, Documents.Schema)
            ?? throw new RuleHealthException($"No schema.json for '{alias}'. Sign in and collect this tenant in the UI first.");

        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var dropped = 0;
        foreach (var (name, columns) in schema.Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!includeCustom && IsTenantTable(name))
            {
                dropped++;
                continue;
            }
            var exported = new Dictionary<string, string>(columns, StringComparer.Ordinal);
            // A workspace table collected before the standard columns were added at collect: add them
            // here, so an older collect exports the same fixture a new one would.
            if (schema.TableInfo?.GetValueOrDefault(name)?.Workspaces.Count > 0)
                Clients.SchemaClient.AddStandardColumns(exported);
            tables[name] = new Dictionary<string, string>(exported.OrderBy(c => c.Key, StringComparer.Ordinal), StringComparer.Ordinal);
        }

        var export = new SchemaDocument
        {
            Tenant = alias,
            GeneratedAt = schema.GeneratedAt,
            // What the fixture is: an export of a tenant, with the date the schema was collected.
            Source = $"tenant-export:{alias} ({schema.GeneratedAt:yyyy-MM-dd}, {schema.Source})",
            Tables = tables,
            Functions = schema.Functions,
        };
        JsonStore.WriteFile(Path.GetFullPath(outFile), export);
        // WriteFile keeps a .bak of an older export; a fixture folder does not need it.
        var backup = Path.GetFullPath(outFile) + ".bak";
        if (File.Exists(backup))
            File.Delete(backup);

        log($"wrote {Path.GetFullPath(outFile)}: {tables.Count} tables, {tables.Sum(t => t.Value.Count)} columns" +
            (dropped > 0 ? $"; left out {dropped} tenant-specific tables ({string.Join(", ", TenantTableSuffixes.Select(s => "*" + s))})" : ""));
        return (export, dropped);
    }

    public static bool IsTenantTable(string name) =>
        TenantTableSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
}
