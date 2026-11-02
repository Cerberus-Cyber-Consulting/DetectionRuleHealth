using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>rules.json — see schemas/rules.schema.json.</summary>
public sealed record RulesDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Tenant { get; init; }
    public required DateTime GeneratedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }

    public List<Rule> Rules { get; init; } = new();
}

public sealed record Rule
{
    /// <summary>Sentinel: alert rule resource name. Defender: detectionRule id. Markdown: md:&lt;path&gt;#&lt;block&gt;.</summary>
    public required string RuleId { get; init; }

    /// <summary><see cref="RuleSources"/>.</summary>
    public required string Source { get; init; }

    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required bool Enabled { get; init; }

    /// <summary>Defender only: enabled, disabled or autoDisabled (<see cref="RuleStatuses"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    public string? Severity { get; init; }

    /// <summary>null for rule kinds without KQL.</summary>
    public required string? QueryText { get; init; }

    /// <summary>ISO-8601 duration.</summary>
    public string? Frequency { get; init; }

    public DateTime? LastModifiedAt { get; init; }

    /// <summary><see cref="SchemaContexts"/>.</summary>
    public required string SchemaContext { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Origin { get; init; }
}

public static class RuleSources
{
    public const string Sentinel = "sentinel";
    public const string Defender = "defender";
    public const string Markdown = "markdown";
}

public static class RuleStatuses
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string AutoDisabled = "autoDisabled";
}

public static class SchemaContexts
{
    public const string Unified = "unified";
    public const string Sentinel = "sentinel";
    public const string Xdr = "xdr";
}
