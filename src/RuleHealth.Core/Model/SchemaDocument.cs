using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>schema.json — see schemas/schema.schema.json. Table and column names are case-sensitive.</summary>
public sealed record SchemaDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime GeneratedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    /// <summary>graph-hunting | arm-tables | file:&lt;name&gt;</summary>
    public required string Source { get; init; }

    /// <summary>tableName → (columnName → kusto scalar type). Dictionary uses ordinal (case-sensitive) keys.</summary>
    public Dictionary<string, Dictionary<string, string>> Tables { get; init; } = new(StringComparer.Ordinal);

    /// <summary>tableName → where it can be queried. Written by collect, used by validate for routing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TableInfo>? TableInfo { get; init; }

    /// <summary>Magic functions to stub: FileProfile, DeviceFromIP, _GetWatchlist.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Functions { get; init; }
}

public sealed record TableInfo
{
    /// <summary>tenant.json workspace names that have this table.</summary>
    public List<string> Workspaces { get; init; } = new();

    /// <summary>Analytics | Basic | Auxiliary (first workspace listed); null when not a workspace table.</summary>
    public required string? Plan { get; init; }

    /// <summary>Returned by the advanced hunting schema.</summary>
    public required bool Hunting { get; init; }
}

public static class TablePlans
{
    public const string Analytics = "Analytics";
    public const string Basic = "Basic";
    public const string Auxiliary = "Auxiliary";
}
