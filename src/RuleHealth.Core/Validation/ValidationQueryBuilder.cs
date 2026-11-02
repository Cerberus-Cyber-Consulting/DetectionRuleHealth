using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Validation;

/// <summary>
/// Turns value checks into KQL: one predicate per check, one summarize query per batch (same table,
/// same engine, at most <see cref="MaxChecksPerQuery"/> checks). See references/queries.md.
///
/// Predicate rules (from the Microsoft Learn check, 2026-10-19):
/// - Paths are always written with brackets (`Col["a"]["b"]`): the dotted form breaks on keys with
///   spaces, dots, dashes or keyword names. A string-typed column holding JSON gets parse_json() first.
/// - Values are verbatim strings (@"..." with "" for a quote), except values with a line break, which
///   a verbatim literal cannot hold.
/// - Numbers are compared as numbers only on a numeric column; a path or another type is compared as
///   text (tostring(x) == "4624" matches a dynamic 4624 too), so a type mismatch never breaks a batch.
/// - `==` becomes `=~` when the rule lowercases/uppercases the column (tolower(X) == "abc"): the value
///   was written for a case-insensitive comparison.
/// </summary>
public static partial class ValidationQueryBuilder
{
    public const int MaxChecksPerQuery = 50;

    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["eq"] = "==",
        ["eq_ci"] = "=~",
        ["has"] = "has",
        ["has_cs"] = "has_cs",
        ["contains"] = "contains",
        ["contains_cs"] = "contains_cs",
        ["startswith"] = "startswith",
        ["startswith_cs"] = "startswith_cs",
        ["endswith"] = "endswith",
        ["endswith_cs"] = "endswith_cs",
        ["hasprefix"] = "hasprefix",
        ["hasprefix_cs"] = "hasprefix_cs",
        ["hassuffix"] = "hassuffix",
        ["hassuffix_cs"] = "hassuffix_cs",
        ["regex"] = "matches regex",
    };

    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal) { "int", "long", "real", "decimal" };

    /// <summary>Whether the operator class can be expressed in KQL at all (`like` cannot: it is not a KQL operator).</summary>
    public static bool IsSupported(string operatorClass) => Operators.ContainsKey(operatorClass);

    /// <summary>
    /// The predicate for one check, or null with a reason when it cannot be written.
    /// <paramref name="columnType"/> is the schema.json type of the column (null when unknown).
    /// </summary>
    public static (string? Predicate, string? Problem) Predicate(ValueCheck check, string? columnType)
    {
        if (!Operators.TryGetValue(check.OperatorClass, out var op))
            return (null, $"operator class '{check.OperatorClass}' has no KQL operator");

        var column = Identifier(check.Column);
        if (check.Path is not null)
        {
            var accessors = PathAccessors(check.Column, check.Path);
            if (accessors is null)
                return (null, $"path '{check.Path}' could not be turned into an accessor");
            // A string column that holds JSON needs parse_json before an accessor works.
            var source = columnType == "string" ? $"parse_json({column})" : column;
            return ($"tostring({source}{accessors}) {CaseAware(op, check)} {StringLiteral(check.Value)}", null);
        }

        if (columnType is null or "string")
            return ($"{column} {CaseAware(op, check)} {StringLiteral(check.Value)}", null);

        if (op is "==" or "=~" && NumericTypes.Contains(columnType) && IsNumber(check.Value))
            return ($"{column} == {check.Value.Trim()}", null);

        if (op is "==" or "=~" && columnType == "bool" && bool.TryParse(check.Value, out var flag))
            return ($"{column} == {(flag ? "true" : "false")}", null);

        // dynamic, datetime, guid, timespan, or a number compared with text: compare the text form.
        return ($"tostring({column}) {CaseAware(op, check)} {StringLiteral(check.Value)}", null);
    }

    /// <summary>
    /// The batch query. Log Analytics: filter on TimeGenerated in the query (plus the request timespan).
    /// Advanced hunting: no time filter in the query, only the request Timespan, because a filter in
    /// the query can make the service read the (possibly filtered) Sentinel copy of an XDR table.
    /// </summary>
    public static string BatchQuery(string table, string timeColumn, bool timeFilterInQuery, int days,
        IReadOnlyList<(string CheckId, string Predicate)> checks)
    {
        if (checks.Count == 0 || checks.Count > MaxChecksPerQuery)
            throw new ArgumentException($"A batch holds 1 to {MaxChecksPerQuery} checks, got {checks.Count}.", nameof(checks));

        var time = Identifier(timeColumn);
        var text = new StringBuilder();
        text.Append(Identifier(table)).Append('\n');
        if (timeFilterInQuery)
            text.Append($"| where {time} > ago({days}d)\n");
        text.Append("| summarize TableRows = count()");
        foreach (var (checkId, predicate) in checks)
        {
            text.Append(",\n    ");
            text.Append($"c_{checkId} = countif({predicate}), l_{checkId} = maxif({time}, {predicate})");
        }
        return text.ToString();
    }

    /// <summary>A table or column name; bracket-quoted when it is not a plain identifier.</summary>
    public static string Identifier(string name) =>
        PlainIdentifier().IsMatch(name) ? name : $"['{name.Replace("\\", "\\\\").Replace("'", "\\'")}']";

    /// <summary>
    /// The value as a KQL string literal: verbatim (@"..." with "" for a quote) unless it holds a line
    /// break, which verbatim literals cannot contain; then a regular escaped literal.
    /// </summary>
    public static string StringLiteral(string value)
    {
        if (value.IndexOfAny(new[] { '\n', '\r' }) < 0)
            return "@\"" + value.Replace("\"", "\"\"") + "\"";
        var escaped = new StringBuilder("\"");
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }
        return escaped.Append('"').ToString();
    }

    /// <summary>
    /// `Col.a.b`, `Col["a"]["b"]`, `Col.['a']`, `Col[0].x` → `["a"]["b"]...` (always brackets).
    /// Null when the path does not start with the column or contains anything else.
    /// </summary>
    public static string? PathAccessors(string column, string path)
    {
        if (!path.StartsWith(column, StringComparison.Ordinal))
            return null;
        var rest = path[column.Length..];
        var accessors = new StringBuilder();
        var position = 0;
        while (position < rest.Length)
        {
            var match = PathSegment().Match(rest, position);
            if (!match.Success || match.Index != position)
                return null;
            if (match.Groups["index"].Success)
                accessors.Append('[').Append(match.Groups["index"].Value).Append(']');
            else
            {
                var key = match.Groups["dotted"].Success ? match.Groups["dotted"].Value
                    : match.Groups["dq"].Success ? match.Groups["dq"].Value.Replace("\\\"", "\"")
                    : match.Groups["sq"].Value.Replace("\\'", "'");
                accessors.Append("[\"").Append(key.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"]");
            }
            position += match.Length;
        }
        return accessors.Length > 0 ? accessors.ToString() : null;
    }

    // == on a column the rule lowercases means the value was written for a case-insensitive match.
    private static string CaseAware(string op, ValueCheck check) =>
        op == "==" && check.Occurrences.Any(o => CaseFolded().IsMatch(o.Expression)) ? "=~" : op;

    private static bool IsNumber(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
        && NumberLiteral().IsMatch(value.Trim());

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainIdentifier();

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberLiteral();

    [GeneratedRegex(@"\b(tolower|toupper)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CaseFolded();

    // One accessor: .name | .['key'] | ["key"] | ['key'] | [0]
    [GeneratedRegex("""\.?\[\s*(?<index>-?\d+)\s*\]|\.?\[\s*"(?<dq>(?:[^"\\]|\\.)*)"\s*\]|\.?\[\s*'(?<sq>(?:[^'\\]|\\.)*)'\s*\]|\.(?<dotted>[A-Za-z_][A-Za-z0-9_$@-]*)""")]
    private static partial Regex PathSegment();
}
