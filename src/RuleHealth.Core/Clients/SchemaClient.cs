using System.Text.Json;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Clients;

/// <summary>Table name → (column name → Kusto scalar type), ordinal (case-sensitive) keys.</summary>
public sealed class TableSchemas : Dictionary<string, Dictionary<string, string>>
{
    public TableSchemas() : base(StringComparer.Ordinal) { }

    /// <summary>Workspace tables only: table → plan (Analytics, Basic, Auxiliary).</summary>
    public Dictionary<string, string?> Plans { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Table schemas for binding rule queries:
/// - Graph beta getHuntingSchemaTables: what advanced hunting sees (XDR tables, plus the workspace
///   when a workspaceId is given — VERIFY whether that includes Sentinel and _CL tables).
/// - ARM workspace tables: every Log Analytics table with its standard and custom columns.
/// </summary>
public sealed class SchemaClient
{
    public const string TablesApiVersion = "2026-03-01";

    private static readonly ApiCall HuntingCall = new(
        "Read advanced hunting schema",
        "Graph delegated permission ThreatHunting.Read.All (admin consent) and Security Reader " +
        "(or a Defender Unified RBAC role that allows advanced hunting)");

    private static readonly ApiCall TablesCall = new(
        "List workspace tables",
        "Log Analytics Reader (or Microsoft Sentinel Reader) on the workspace (ARM user_impersonation)");

    private readonly ApiConnection _api;

    public SchemaClient(ApiConnection api) => _api = api;

    public async Task<TableSchemas> GetHuntingTablesAsync(string? workspaceId, List<string> warnings, CancellationToken cancellationToken)
    {
        var url = ApiConnection.GraphBase + "/beta/security/getHuntingSchemaTables" +
                  (workspaceId is null ? "" : $"(workspaceId={Uri.EscapeDataString(workspaceId)})");
        var items = await _api.GetAllPagesAsync(_api.Graph, url, "@odata.nextLink", HuntingCall, cancellationToken);
        return ReadHuntingTables(items, warnings);
    }

    public async Task<TableSchemas> GetWorkspaceTablesAsync(WorkspaceRef workspace, List<string> warnings, CancellationToken cancellationToken)
    {
        var url = $"{ApiConnection.ArmBase}{WorkspaceResourceId.From(workspace).Path}/tables?api-version={TablesApiVersion}";
        var call = TablesCall with { Label = $"List workspace tables ({workspace.WorkspaceName})" };
        var items = await _api.GetAllPagesAsync(_api.Arm, url, "nextLink", call, cancellationToken);
        return ReadWorkspaceTables(items, warnings);
    }

    /// <summary>`value[]` of { name, columns[] { name, dataType } }.</summary>
    public static TableSchemas ReadHuntingTables(IEnumerable<JsonElement> items, List<string> warnings)
    {
        var tables = new TableSchemas();
        foreach (var item in items)
        {
            if (ApiConnection.Text(item, "name") is not { } name)
                continue;
            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ApiConnection.Path(item, "columns") is { ValueKind: JsonValueKind.Array } list)
            {
                foreach (var column in list.EnumerateArray())
                {
                    if (ApiConnection.Text(column, "name") is { } columnName)
                        columns[columnName] = MapType(ApiConnection.Text(column, "dataType"), $"{name}.{columnName}", warnings);
                }
            }
            tables[name] = columns;
        }
        return tables;
    }

    /// <summary>
    /// `value[]` of { name, properties.schema { standardColumns[], columns[] } }. `columns` holds the
    /// custom columns and is null for Microsoft tables; a table's full schema is both lists.
    /// </summary>
    public static TableSchemas ReadWorkspaceTables(IEnumerable<JsonElement> items, List<string> warnings)
    {
        var tables = new TableSchemas();
        foreach (var item in items)
        {
            if (ApiConnection.Text(item, "name") is not { } name)
                continue;
            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var listName in new[] { "standardColumns", "columns" })
            {
                if (ApiConnection.Path(item, "properties", "schema", listName) is not { ValueKind: JsonValueKind.Array } list)
                    continue;
                foreach (var column in list.EnumerateArray())
                {
                    if (ApiConnection.Text(column, "name") is { } columnName)
                        columns[columnName] = MapType(ApiConnection.Text(column, "type"), $"{name}.{columnName}", warnings);
                }
            }
            AddStandardColumns(columns);
            tables[name] = columns;
            tables.Plans[name] = ApiConnection.PathText(item, "properties", "plan");
        }
        return tables;
    }

    /// <summary>
    /// The standard columns every Log Analytics workspace table has (Learn: "Standard columns in Azure
    /// Monitor Logs"), with the types the table reference lists. The tables API leaves several out
    /// ("some of the standard columns don't show in the schema view"), most notably `Type`, which a
    /// rule's join or union then misses (`Type1`). Columns the API did list keep their type.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> StandardColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TenantId"] = "string",
        ["TimeGenerated"] = "datetime",
        ["_TimeReceived"] = "datetime",
        ["Type"] = "string",
        ["_ItemId"] = "string",
        ["_ResourceId"] = "string",
        ["_SubscriptionId"] = "string",
        ["_IsBillable"] = "string",
        ["_BilledSize"] = "real",
    };

    public static void AddStandardColumns(Dictionary<string, string> columns)
    {
        foreach (var (column, type) in StandardColumns)
            columns.TryAdd(column, type);
    }

    /// <summary>Union of several schemas (several workspaces): a table present twice gets every column.</summary>
    public static TableSchemas Union(IEnumerable<TableSchemas> schemas)
    {
        var result = new TableSchemas();
        foreach (var schema in schemas)
        {
            foreach (var (table, columns) in schema)
            {
                if (!result.TryGetValue(table, out var merged))
                    result[table] = merged = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (column, type) in columns)
                    merged.TryAdd(column, type);
            }
        }
        return result;
    }

    /// <summary>
    /// API type names → the Kusto scalar types schema.json allows. ARM uses string, int, long, real,
    /// boolean, dateTime, guid, dynamic. Graph's dataType list is not documented (examples: String,
    /// DateTime), so .NET-style names are mapped as well. Anything else becomes dynamic, with a warning.
    /// </summary>
    public static string MapType(string? apiType, string where, List<string> warnings)
    {
        switch (apiType?.ToLowerInvariant())
        {
            case "string": return "string";
            case "int": case "int32": return "int";
            case "long": case "int64": return "long";
            case "real": case "double": return "real";
            case "decimal": return "decimal";
            case "bool": case "boolean": return "bool";
            case "datetime": return "datetime";
            case "timespan": return "timespan";
            case "guid": return "guid";
            case "dynamic": case "object": return "dynamic";
            default:
                // One warning per unknown type name, not one per column.
                var prefix = $"unknown column type '{apiType}'";
                if (!warnings.Any(w => w.StartsWith(prefix, StringComparison.Ordinal)))
                    warnings.Add($"{prefix} (first seen on {where}); those columns are bound as dynamic");
                return "dynamic";
        }
    }
}
