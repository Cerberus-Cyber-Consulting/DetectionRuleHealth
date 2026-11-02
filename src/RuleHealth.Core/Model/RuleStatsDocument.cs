using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>rule-stats.json — see schemas/rule-stats.schema.json.</summary>
public sealed record RuleStatsDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime GeneratedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    public required CollectionWindow Window { get; init; }
    public List<RuleStats> Rules { get; init; } = new();
}

public sealed record RuleStats
{
    public required string RuleId { get; init; }
    public int Incidents { get; init; }
    public int Open { get; init; }
    public int Closed { get; init; }
    public int Tp { get; init; }
    public int Bp { get; init; }
    public int Fp { get; init; }
    public int Undetermined { get; init; }

    /// <summary>tp / (tp+bp+fp); null when the denominator is 0. Same denominator for bp and fp.</summary>
    public double? TpRate { get; init; }
    public double? BpRate { get; init; }
    public double? FpRate { get; init; }

    /// <summary>undetermined / closed.</summary>
    public double? UndeterminedRate { get; init; }

    /// <summary>Incidents this rule shares with other rules (counted for each rule).</summary>
    public int SharedIncidents { get; init; }

    public DateTime? LastIncidentAt { get; init; }
}
