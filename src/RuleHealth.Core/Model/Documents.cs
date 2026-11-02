using System.Text.RegularExpressions;

namespace RuleHealth.Core.Model;

/// <summary>Names of the JSON documents in a tenant folder, and the shared constants of the data contract.</summary>
public static partial class Documents
{
    /// <summary>Every file carries this schemaVersion; JsonStore rejects anything else.</summary>
    public const int SchemaVersion = 1;

    public const string Tenant = "tenant";
    public const string Rules = "rules";
    public const string Schema = "schema";
    public const string Incidents = "incidents";
    public const string RuleStats = "rule-stats";
    public const string Candidates = "candidates";
    public const string Selections = "selections";
    public const string Validation = "validation";

    /// <summary>All documents, in pipeline order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Tenant, Rules, Schema, Incidents, RuleStats, Candidates, Selections, Validation,
    };

    /// <summary>Documents the serve API exposes. schema.json is engine-internal (large, not needed by the UI).</summary>
    public static readonly IReadOnlyList<string> Served = new[]
    {
        Tenant, Rules, Incidents, RuleStats, Candidates, Selections, Validation,
    };

    /// <summary>Tenant aliases are folder names: lowercase letters, digits and dashes only (no path traversal).</summary>
    public static bool IsValidAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias);

    public static bool IsValidCheckId(string? checkId) => checkId is not null && CheckIdPattern().IsMatch(checkId);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex CheckIdPattern();
}
