using System.Text.Json;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Extraction;

/// <summary>
/// Loads a table schema for offline extraction (--schema-file): our own schema.json, as a collect
/// writes it or `rulehealth schema export` exports it from your own tenant. Third-party schema files
/// are not read (M8 step 5): the schema must come from a tenant you control.
/// </summary>
public static class SchemaFileReader
{
    /// <summary>Magic functions Defender and the unified (Defender portal) Sentinel experience provide.</summary>
    public static readonly IReadOnlyList<string> UnifiedFunctions = new[] { "FileProfile", "DeviceFromIP" };

    public static SchemaDocument Read(string path, string tenant)
    {
        if (!File.Exists(path))
            throw new RuleHealthException($"Schema file {path} does not exist.");

        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;

        if (root.TryGetProperty("schemaVersion", out _))
            return JsonStore.ReadFile<SchemaDocument>(path);

        throw new RuleHealthException(
            $"{path} is not a Detection Rule Health schema.json. Export one from your own tenant with " +
            "`rulehealth schema export --tenant <alias> --out <file>` after collecting it.");
    }

    /// <summary>
    /// Unified context = Defender portal with Sentinel onboarded: all Sentinel tables plus all XDR
    /// tables. A table present in both keeps the Sentinel columns and gains the XDR ones. XDR tables
    /// also get TimeGenerated. Column names are case-sensitive (IdentityInfo has both AccountUPN and
    /// AccountUpn), so every dictionary is ordinal.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> MergeUnified(
        Dictionary<string, Dictionary<string, string>> sentinel,
        Dictionary<string, Dictionary<string, string>> xdr)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(sentinel, StringComparer.Ordinal);
        foreach (var (name, xdrColumns) in xdr)
        {
            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tables.TryGetValue(name, out var existing))
                foreach (var (column, type) in existing)
                    columns[column] = type;
            foreach (var (column, type) in xdrColumns)
                columns.TryAdd(column, type);
            columns.TryAdd("TimeGenerated", "datetime");
            tables[name] = columns;
        }
        return tables;
    }
}
