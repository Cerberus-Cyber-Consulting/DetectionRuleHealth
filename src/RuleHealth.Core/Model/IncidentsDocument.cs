using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>incidents.json — see schemas/incidents.schema.json.</summary>
public sealed record IncidentsDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime GeneratedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    public required CollectionWindow Window { get; init; }
    public List<Incident> Incidents { get; init; } = new();
}

/// <summary>The time window a collection covered; shared by incidents.json and rule-stats.json.</summary>
public sealed record CollectionWindow
{
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required int Days { get; init; }
}

public sealed record Incident
{
    public required string IncidentId { get; init; }

    /// <summary>sentinel | defender</summary>
    public required string Source { get; init; }

    public int? Number { get; init; }
    public required string Title { get; init; }
    public string? Severity { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }

    /// <summary>open | closed</summary>
    public required string Status { get; init; }

    /// <summary><see cref="Classifications"/>; null while open.</summary>
    public required string? Classification { get; init; }

    /// <summary>Every rule that contributed an alert to this incident.</summary>
    public List<string> RuleIds { get; init; } = new();
}

public static class IncidentStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

public static class Classifications
{
    public const string TruePositive = "TP";
    public const string BenignPositive = "BP";
    public const string FalsePositive = "FP";
    public const string Undetermined = "Undetermined";
}
