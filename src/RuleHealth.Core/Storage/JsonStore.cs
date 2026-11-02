using System.Text.Json;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Storage;

/// <summary>
/// Reads and writes the per-tenant JSON documents under &lt;dataRoot&gt;/&lt;alias&gt;/&lt;doc&gt;.json.
/// This is the whole "backend" in v1; a v2 host can swap it for a database behind the same methods.
/// </summary>
public sealed class JsonStore
{
    public JsonStore(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string TenantDirectory(string alias)
    {
        if (!Documents.IsValidAlias(alias))
            throw new RuleHealthException($"Invalid tenant alias '{alias}'. Use lowercase letters, digits and dashes.");
        return Path.Combine(DataRoot, alias);
    }

    public string DocumentPath(string alias, string document)
    {
        if (!Documents.All.Contains(document))
            throw new ArgumentException($"Unknown document '{document}'.", nameof(document));
        return Path.Combine(TenantDirectory(alias), document + ".json");
    }

    public bool Exists(string alias, string document) => File.Exists(DocumentPath(alias, document));

    /// <summary>Tenant folders (valid alias names only), sorted.</summary>
    public IReadOnlyList<string> ListTenants()
    {
        if (!Directory.Exists(DataRoot))
            return Array.Empty<string>();
        return Directory.GetDirectories(DataRoot)
            .Select(Path.GetFileName)
            .Where(Documents.IsValidAlias)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Documents present for a tenant, in pipeline order.</summary>
    public IReadOnlyList<string> ListDocuments(string alias) =>
        Documents.All.Where(document => Exists(alias, document)).ToList();

    /// <summary>Reads a document; returns null when the file does not exist.</summary>
    public T? TryRead<T>(string alias, string document) where T : class
    {
        var path = DocumentPath(alias, document);
        return File.Exists(path) ? ReadFile<T>(path) : null;
    }

    public T Read<T>(string alias, string document) where T : class
    {
        var path = DocumentPath(alias, document);
        if (!File.Exists(path))
            throw new RuleHealthException($"{path} does not exist.");
        return ReadFile<T>(path);
    }

    /// <summary>
    /// Writes atomically: serialize to &lt;file&gt;.tmp, then replace the target, keeping the previous
    /// version as &lt;file&gt;.bak. A crash mid-write leaves the old file intact.
    /// </summary>
    public void Write<T>(string alias, string document, T value) where T : class => WriteFile(DocumentPath(alias, document), value);

    /// <summary>The same atomic write for a file outside the tenant folders (app state in &lt;data&gt;/.app/).</summary>
    public static void WriteFile<T>(string path, T value) where T : class
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var bak = path + ".bak";

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json.Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    public static T ReadFile<T>(string path) where T : class
    {
        try
        {
            // Check schemaVersion before mapping, so an old/new file gives a clear message instead of
            // a confusing "missing property" error.
            var bytes = File.ReadAllBytes(path);
            using (var probe = JsonDocument.Parse(bytes))
            {
                var root = probe.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("schemaVersion", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || version.GetInt32() != Documents.SchemaVersion)
                {
                    throw new RuleHealthException(
                        $"{path} does not have \"schemaVersion\": {Documents.SchemaVersion}. " +
                        "It was written by a different RuleHealth version; re-run the command that produces it.");
                }
            }
            return JsonSerializer.Deserialize<T>(bytes, Json.Options)
                ?? throw new RuleHealthException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new RuleHealthException($"{path} is not valid: {ex.Message}", ex);
        }
    }
}
