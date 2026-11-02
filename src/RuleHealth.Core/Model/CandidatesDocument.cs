using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>candidates.json — see schemas/candidates.schema.json.</summary>
public sealed record CandidatesDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime GeneratedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    public required string SchemaSource { get; init; }
    public List<ValueCheck> Checks { get; init; } = new();
    public List<UnresolvedComparison> Unresolved { get; init; } = new();
    public List<RuleExtractionSummary> RuleSummaries { get; init; } = new();
}

/// <summary>One (table, column, path, operatorClass, value) combination, merged across rules.</summary>
public sealed record ValueCheck
{
    public required string CheckId { get; init; }
    public required string Table { get; init; }
    public required string Column { get; init; }

    /// <summary>Property path inside a dynamic column, e.g. AdditionalFields.Foo.</summary>
    public required string? Path { get; init; }

    /// <summary><see cref="OperatorClasses"/>.</summary>
    public required string OperatorClass { get; init; }

    public required string Value { get; init; }

    /// <summary><see cref="Lineages"/>.</summary>
    public required string Lineage { get; init; }

    /// <summary>v1: true for direct/renamed lineage only.</summary>
    public required bool Validatable { get; init; }

    public List<CheckOccurrence> Occurrences { get; init; } = new();
}

public sealed record CheckOccurrence
{
    public required string RuleId { get; init; }

    /// <summary>Operator as written: ==, in~, !has, has_any, ...</summary>
    public required string Operator { get; init; }

    /// <summary>Exclusion (!=, !in, !has...). A never-seen excluded value is a dead exclusion, not a broken rule.</summary>
    public required bool Negated { get; init; }

    public string? DerivedColumn { get; init; }
    public string? DerivedPath { get; init; }
    public string? ColumnDefinition { get; init; }

    /// <summary>The comparison as it appears in the rule.</summary>
    public required string Expression { get; init; }
}

public sealed record UnresolvedComparison
{
    public required string RuleId { get; init; }

    /// <summary><see cref="UnresolvedStatuses"/>.</summary>
    public required string Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operator { get; init; }

    public required string Expression { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
}

public sealed record RuleExtractionSummary
{
    public required string RuleId { get; init; }
    public int Comparisons { get; init; }
    public int Checks { get; init; }
    public int NullChecks { get; init; }
    public int Unresolved { get; init; }
    public List<string> BindErrors { get; init; } = new();

    /// <summary>Schema tables the query reads, sorted (ordinal). Null in files written before this field existed.</summary>
    public List<string>? Tables { get; init; }
}

public static class Lineages
{
    public const string Direct = "direct";
    public const string Renamed = "renamed";
    public const string Expression = "expression";
}

public static class UnresolvedStatuses
{
    public const string RuntimeValue = "RuntimeValue";
    public const string UnboundExpression = "UnboundExpression";
    public const string ComputedColumn = "ComputedColumn";
}

/// <summary>The operatorClass values from the schema. Negation is stored separately on the occurrence.</summary>
public static class OperatorClasses
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "eq", "eq_ci", "has", "has_cs",
        "contains", "contains_cs", "startswith", "startswith_cs", "endswith", "endswith_cs",
        "hasprefix", "hasprefix_cs", "hassuffix", "hassuffix_cs",
        "regex", "like", "like_cs",
    };
}
