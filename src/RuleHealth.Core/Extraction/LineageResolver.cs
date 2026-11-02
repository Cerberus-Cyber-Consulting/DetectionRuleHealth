using System.Text.RegularExpressions;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Extraction;

/// <summary>
/// A (table, column) a compared column traces back to. Path is the full path on the table column
/// (AdditionalFields.FileType) when the compared value is a field inside it, else null.
/// </summary>
public sealed record SourceColumn(string Table, string Column, string? Path = null);

/// <summary>
/// Where a compared column comes from. Kind is "direct", "renamed", "expression" (see
/// <see cref="Lineages"/>) or null when no table column was reached (FileProfile output, watchlist,
/// datatable, print).
/// </summary>
public sealed record ColumnLineage(string? Kind, IReadOnlyList<SourceColumn> Sources);

/// <summary>Traces a column symbol back to database table columns.</summary>
public static partial class LineageResolver
{
    private const int MaxDepth = 8;

    /// <summary>Wrappers that only change the type, so a JSON path through them still addresses the same data.</summary>
    private static readonly HashSet<string> TypeWrappers = new(StringComparer.Ordinal) { "tostring", "todynamic", "parse_json" };

    /// <param name="pathSuffix">
    /// What the comparison selects inside the column, as written after the column name
    /// (`.SPF`, `["rawData"].x`), or "" for the column itself. It is carried back through renames
    /// and path-only definitions so the check gets the full path on the table column. Its first key
    /// also lets a column built with pack(...) resolve to the value under that key only.
    /// </param>
    /// <param name="expandedColumns">
    /// Names of columns the rule expands with mv-expand/mv-apply. Once lineage passes such a column
    /// the value is one array element, not the table column's value: the result becomes expression
    /// lineage without a path (a path check on the raw array would never match).
    /// </param>
    public static ColumnLineage Resolve(GlobalState globals, ColumnSymbol column, string pathSuffix = "",
        IReadOnlySet<string>? expandedColumns = null)
    {
        var sources = new List<SourceColumn>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        // The same column can be reached with different paths/keys, so they are part of "seen".
        var seen = new HashSet<(Symbol, string?, string?)>();

        Walk(column, 0, Lineages.Direct, pathSuffix, FirstKey(pathSuffix));

        var kind = sources.Count == 0 ? null
            : kinds.Contains(Lineages.Expression) ? Lineages.Expression
            : kinds.Contains(Lineages.Renamed) ? Lineages.Renamed
            : Lineages.Direct;
        return new ColumnLineage(kind, sources.Distinct().ToList());

        // suffix: path still to apply on the column ("" = none); null once a value transformation
        // made the path meaningless. key: first object key still to select (for pack()).
        void Walk(ColumnSymbol current, int depth, string via, string? suffix, string? key)
        {
            if (depth > MaxDepth || !seen.Add((current, suffix, key)))
                return;

            // Name-based and rule-wide, like the compared-column check in RuleExtractor: it can
            // over-mark, which only makes a check non-validatable (safe), never wrongly validatable.
            if (expandedColumns is not null && expandedColumns.Contains(current.Name))
            {
                via = Lineages.Expression;
                suffix = null;
            }

            // A column of a database table: done.
            if (globals.GetTable(current) is { } table)
            {
                sources.Add(new SourceColumn(table.Name, current.Name, string.IsNullOrEmpty(suffix) ? null : current.Name + suffix));
                kinds.Add(via);
                return;
            }

            var renamed = via == Lineages.Direct ? Lineages.Renamed : via;

            // Columns produced by join/union/project-rename point at the columns they came from.
            if (current.OriginalColumns.Count > 0)
            {
                foreach (var original in current.OriginalColumns)
                    Walk(original, depth + 1, renamed, suffix, key);
                return;
            }

            // `X = Col`, `X = parse_json(Col)`, `X = tostring(Col.a.b)`: the same data under another
            // name, possibly one level down a JSON path. A rename that carries the path along, so
            // `X.c == "v"` becomes a check on Col.a.b.c. Only the assignment form counts:
            // `parse Col with ... X:int ...` also has a bare Col as Source but extracts part of the value.
            if (IsPathOverColumn(current, out var underlying, out var definitionPath) && !ReferenceEquals(underlying, current))
            {
                var combined = suffix is null ? null : definitionPath + suffix;
                Walk(underlying, depth + 1, renamed, combined, combined is null ? (definitionPath.Length == 0 ? key : null) : FirstKey(combined));
                return;
            }

            // Computed columns (extend X = f(A, B), summarize ...) point at the syntax that defined
            // them. Only columns whose value can flow into X are sources: iff(A == "x", B, C) lists
            // B and C, not A (see ValueFlow). No flowing column → no lineage → ComputedColumn.
            foreach (var source in ValueFlow.SourceColumns(current.Source, key))
            {
                if (!ReferenceEquals(source.Column, current))
                    Walk(source.Column, depth + 1, Lineages.Expression, null, source.Key);
            }
        }
    }

    /// <summary>
    /// True when the column is defined as `Name = expr` and expr is a column, optionally wrapped in
    /// tostring/todynamic/parse_json and parentheses, with any JSON path on top. Also true for an
    /// unnamed type wrapper that keeps the column's name (`distinct tostring(X)` → column X).
    /// definitionPath is the path as written after the column (".a.b", "[\"x\"]"), "" for none.
    /// </summary>
    private static bool IsPathOverColumn(ColumnSymbol current, out ColumnSymbol column, out string definitionPath)
    {
        column = null!;
        definitionPath = "";
        var source = current.Source;
        var assigned = source?.Parent is SimpleNamedExpression named && ReferenceEquals(named.Expression, source);
        // Not `parse Col with ...` (a bare NameReference Source): that extracts part of the value.
        var autoNamed = !assigned && source is FunctionCallExpression;
        if (!assigned && !autoNamed)
            return false;

        var segments = new List<string>(); // outermost first
        var node = source;
        for (var step = 0; step < 16 && node is not null; step++)
        {
            switch (node)
            {
                case ParenthesizedExpression parenthesized:
                    node = parenthesized.Expression;
                    continue;
                case FunctionCallExpression call when TypeWrappers.Contains(call.Name.SimpleName)
                                                     && call.ArgumentList.Expressions.Count == 1:
                    node = call.ArgumentList.Expressions[0].Element;
                    continue;
                case PathExpression path:
                    segments.Add("." + ComparisonFinder.Text(path.Selector));
                    node = path.Expression;
                    continue;
                case ElementExpression element:
                    segments.Add(ComparisonFinder.Text(element.Selector));
                    node = element.Expression;
                    continue;
                case NameReference { ReferencedSymbol: ColumnSymbol referenced }:
                    // An unnamed wrapper only counts when the result kept the column's own name.
                    if (autoNamed && (segments.Count > 0 || referenced.Name != current.Name))
                        return false;
                    column = referenced;
                    segments.Reverse();
                    definitionPath = string.Concat(segments);
                    return true;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>First object key in a path suffix: ".DeviceType.x" → "DeviceType", "[\"x\"]" → "x".</summary>
    public static string? FirstKey(string? suffix)
    {
        if (string.IsNullOrEmpty(suffix))
            return null;
        var match = FirstKeyPattern().Match(suffix);
        return !match.Success ? null : match.Groups["dot"].Success ? match.Groups["dot"].Value : match.Groups["bracket"].Value;
    }

    [GeneratedRegex("""^\s*(?:\.\s*(?<dot>[A-Za-z_][A-Za-z0-9_]*)|\[\s*["'](?<bracket>[^"']+)["']\s*\])""")]
    private static partial Regex FirstKeyPattern();
}
