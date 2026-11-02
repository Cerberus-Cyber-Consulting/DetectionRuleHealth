using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>validation.json — result of a validate job for every check with state=validate.</summary>
public sealed record ValidationDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime RunAt { get; init; }
    public required int WindowDays { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    public List<ValidationResult> Results { get; init; } = new();
    public List<ValidationQuery> Queries { get; init; } = new();
}

public sealed record ValidationResult
{
    public required string CheckId { get; init; }
    public required string Table { get; init; }
    public required string Column { get; init; }
    public required string? Path { get; init; }
    public required string OperatorClass { get; init; }
    public required string Value { get; init; }

    /// <summary><see cref="Verdicts"/>.</summary>
    public required string Verdict { get; init; }

    public required long? MatchCount { get; init; }
    public required long? TableRowCount { get; init; }
    public required DateTime? LastSeenAt { get; init; }

    /// <summary><see cref="QueryEngines"/>.</summary>
    public required string Engine { get; init; }

    public required string? Error { get; init; }
}

public sealed record ValidationQuery
{
    public required string Table { get; init; }
    public required string Engine { get; init; }
    public required string Kql { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; init; }

    public string? Error { get; init; }
}

public static class Verdicts
{
    public const string Present = "present";
    public const string Absent = "absent";

    /// <summary>The table had no rows in the window: connector or retention problem, not a rule problem.</summary>
    public const string TableEmpty = "table-empty";

    public const string Error = "error";
    public const string Skipped = "skipped";
}

public static class QueryEngines
{
    public const string LogAnalytics = "loganalytics";
    public const string Hunting = "hunting";
}
