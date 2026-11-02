using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>selections.json — the user's decisions per checkId. Written by the UI, read by `validate`.</summary>
public sealed record SelectionsDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime UpdatedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    /// <summary>checkId → decision. Ordinal keys; checkIds are lowercase hex.</summary>
    public Dictionary<string, Decision> Decisions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>ruleId → "manual OK": the rule is fine although the listed problem checks were absent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RuleStatusDecision>? RuleStatuses { get; init; }

    /// <summary>ruleId → issue categories for reporting.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RuleCategoryAssignment>? RuleCategories { get; init; }

    /// <summary>ruleId → investigation notes (track record), oldest first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<RuleNote>>? RuleNotes { get; init; }
}

public sealed record RuleCategoryAssignment
{
    /// <summary>Category names as the user sees them ("Missing events"); custom names are allowed.</summary>
    public List<string> Categories { get; init; } = new();
    public required DateTime UpdatedAt { get; init; }

    public const int MaxCategories = 20;
    public const int MaxNameLength = 60;
}

public sealed record RuleNote
{
    /// <summary>Short id made by the UI, so one note can be deleted.</summary>
    public required string Id { get; init; }
    public required string Text { get; init; }
    public string? Author { get; init; }
    public required DateTime CreatedAt { get; init; }

    public const int MaxTextLength = 4000;
    public const int MaxAuthorLength = 100;
}

public sealed record RuleStatusDecision
{
    /// <summary>Always "ok" (<see cref="RuleStatusStates.Ok"/>).</summary>
    public required string State { get; init; }

    public string? Note { get; init; }
    public required DateTime DecidedAt { get; init; }

    /// <summary>The rule's checks that were absent / table-empty / error / dead exclusions when marked OK.</summary>
    public List<string> CoveredCheckIds { get; init; } = new();
}

public static class RuleStatusStates
{
    public const string Ok = "ok";
}

public sealed record Decision
{
    /// <summary><see cref="DecisionStates"/>.</summary>
    public required string State { get; init; }

    public string? Note { get; init; }
    public required DateTime DecidedAt { get; init; }

    /// <summary>Copy of the check so the decision stays readable if it disappears from candidates.json.</summary>
    public required CheckKey Check { get; init; }
}

/// <summary>The five fields that make up a checkId.</summary>
public sealed record CheckKey
{
    public required string Table { get; init; }
    public required string Column { get; init; }
    public required string? Path { get; init; }
    public required string OperatorClass { get; init; }
    public required string Value { get; init; }
}

public static class DecisionStates
{
    /// <summary>Must always be present in normal telemetry.</summary>
    public const string Validate = "validate";

    /// <summary>Only expected during attacks, or not useful to check.</summary>
    public const string Ignore = "ignore";
}
