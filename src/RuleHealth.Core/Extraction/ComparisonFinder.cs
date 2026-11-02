using System.Text.RegularExpressions;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;

namespace RuleHealth.Core.Extraction;

/// <summary>The column side of a comparison: the bound column and, for dynamic columns, the full path text.</summary>
/// <param name="Transform">
/// Set when the column is wrapped in a function that derives a new value from it (split(X, ",")[0],
/// extract(..., X), ...): the outermost such call. The check is derived, not validatable directly.
/// </param>
/// <param name="Computed">
/// Set when the column is wrapped in a function whose result carries no column value at all
/// (array_length(X), strlen(X), isempty(X), ...): the compared value is computed, so no check.
/// </param>
public sealed record ColumnRef(ColumnSymbol Column, string? Path, string? Transform = null, string? Computed = null);

/// <summary>One column-vs-value comparison found in a query.</summary>
/// <param name="Operator">Operator token as written.</param>
/// <param name="Column">Null when the compared side does not bind to a column (UnboundExpression).</param>
/// <param name="ValueExpression">The other side, to be resolved to literal values.</param>
/// <param name="Text">The comparison text with whitespace collapsed.</param>
/// <param name="InsideNot">True under an odd number of not(...) wrappers: the meaning of the operator is inverted.</param>
public sealed record Comparison(string Operator, ColumnRef? Column, SyntaxNode ValueExpression, string Text, bool InsideNot = false);

/// <summary>
/// Finds every string-style comparison in a parsed query: binary operators other than logic,
/// arithmetic and ranges, plus in / has_any / has_all. Join keys and column-vs-column comparisons
/// are skipped (nothing to validate).
/// </summary>
public static partial class ComparisonFinder
{
    private static readonly HashSet<string> IgnoredBinaryOperators = new(StringComparer.Ordinal)
    {
        "and", "or", "+", "-", "*", "/", "%", "<", ">", "<=", ">=", "between", "!between",
    };

    public static List<Comparison> Find(SyntaxNode root)
    {
        var comparisons = new List<Comparison>();
        foreach (var node in root.GetDescendantsOrSelf<SyntaxNode>())
        {
            string op;
            SyntaxNode left, right;
            switch (node)
            {
                case BinaryExpression binary when !IgnoredBinaryOperators.Contains(binary.Operator.Text):
                    (op, left, right) = (binary.Operator.Text, binary.Left, binary.Right);
                    break;
                case InExpression inExpression:
                    (op, left, right) = (inExpression.Operator.Text, inExpression.Left, inExpression.Right);
                    break;
                case HasAnyExpression hasAny:
                    (op, left, right) = (hasAny.Operator.Text, hasAny.Left, hasAny.Right);
                    break;
                case HasAllExpression hasAll:
                    (op, left, right) = (hasAll.Operator.Text, hasAll.Left, hasAll.Right);
                    break;
                default:
                    continue;
            }

            // Join keys ($left.X == $right.Y) compare columns to columns.
            var text = Text(node);
            if (JoinKeyReference().IsMatch(text))
                continue;

            var column = GetColumnRef(left);
            var valueSide = right;
            if (column is null)
            {
                // Reversed form: "value" == Column
                var reversed = GetColumnRef(right);
                if (reversed is not null)
                {
                    column = reversed;
                    valueSide = left;
                }
            }
            // Column vs column (self joins, computed comparisons): nothing to validate.
            if (column is not null && GetColumnRef(valueSide) is not null)
                continue;

            comparisons.Add(new Comparison(op, column, valueSide, text, IsInsideNot(node)));
        }
        return comparisons;
    }

    /// <summary>
    /// Unwraps tolower(X), tostring(X), trim(" ", X), (X), X.a.b and X["a"] down to the column.
    /// Keeps the outermost path text (a.b.c, not a.b), because that is what the rule compares.
    /// A path on a wrapped column (parse_json(X).a.b) is rebased onto the column (X.a.b).
    /// </summary>
    public static ColumnRef? GetColumnRef(SyntaxNode? expression)
    {
        SyntaxNode? current = expression;
        string? fullPath = null;  // outermost path text as written: parse_json(X).a.b
        string? pathBase = null;  // the non-column base that path hangs off: parse_json(X)
        string? transform = null;
        string? computed = null;
        string? graphPrefix = null; // "SourceNode." when the path starts at a graph-match node/edge variable
        for (var step = 0; step < 12 && current is not null; step++)
        {
            switch (current)
            {
                case ParenthesizedExpression parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case FunctionCallExpression call:
                    // Follow the arguments whose value reaches the result (ValueFlow). When none does
                    // (array_length, strlen, isempty, ...) still find the column, but mark the value as computed.
                    var flowing = ValueFlow.FlowingArguments(call);
                    if (flowing.Count == 0)
                        computed ??= Text(call);
                    else if (!ValueFlow.IsPreserving(call))
                        transform ??= Text(call);
                    var candidates = flowing.Count > 0 ? flowing : call.ArgumentList.Expressions.Select(e => e.Element).ToList();
                    // First argument that can lead to a column; constants like the " " in trim(" ", X) are skipped.
                    current = candidates.FirstOrDefault(a => a.ReferencedSymbol is ColumnSymbol
                        || a is PathExpression or ElementExpression or FunctionCallExpression or ParenthesizedExpression);
                    if (current is null)
                        return null;
                    continue;

                case PathExpression or ElementExpression:
                    fullPath ??= Text(current);
                    var baseExpression = current is PathExpression path ? path.Expression : ((ElementExpression)current).Expression;
                    // graph-match: SourceNode.NodeLabel / anyEdge.EdgeLabel read a column of the node or edge
                    // table (ExposureGraphNodes / ExposureGraphEdges). The variable is a VariableSymbol, the
                    // selector the table's own ColumnSymbol; a let-bound dynamic never has a column selector.
                    if (baseExpression is NameReference { ReferencedSymbol: VariableSymbol }
                        && current is PathExpression { Selector.ReferencedSymbol: ColumnSymbol graphColumn })
                    {
                        graphPrefix = Text(baseExpression) + ".";
                        return Result(graphColumn, Text(current));
                    }
                    if (baseExpression.ReferencedSymbol is ColumnSymbol baseColumn)
                    {
                        // TargetNode.NodeProperties.rawData: the column is reached through a graph variable.
                        if (baseExpression is PathExpression { Expression: NameReference { ReferencedSymbol: VariableSymbol } variable })
                            graphPrefix = Text(variable) + ".";
                        return Result(baseColumn, Text(current)); // X.a (or X.a inside parse_json(X.a).b)
                    }
                    if (pathBase is null && baseExpression is not (PathExpression or ElementExpression))
                        pathBase = Text(baseExpression);
                    current = baseExpression;
                    continue;
            }

            return current.ReferencedSymbol is ColumnSymbol column ? Result(column, column.Name) : null;
        }
        return null;

        // innerText is how the column (plus any path directly on it) is written at the bottom of the chain.
        // A graph variable prefix is dropped: TargetNode.NodeProperties.rawData.x is the path
        // NodeProperties.rawData.x on the node table, and SourceNode.NodeLabel is the column itself.
        ColumnRef Result(ColumnSymbol column, string innerText)
        {
            var result = ResultOnColumn(column, innerText);
            if (graphPrefix is null || result.Path is not { } p || !p.StartsWith(graphPrefix, StringComparison.Ordinal))
                return result;
            var onColumn = p[graphPrefix.Length..];
            return result with { Path = onColumn == column.Name ? null : onColumn };
        }

        ColumnRef ResultOnColumn(ColumnSymbol column, string innerText)
        {
            if (fullPath is null)
                return new ColumnRef(column, null, transform, computed);
            if (pathBase is null)
                return new ColumnRef(column, fullPath, transform, computed); // a path straight on the column
            if (transform is not null || computed is not null)
                return new ColumnRef(column, fullPath, transform, computed); // derived anyway; keep the text
            // parse_json(X).a.b → X.a.b: the wrappers between the path and the column only change the
            // type, so the path applies to the column itself. If the text cannot be rebased, do not
            // pretend it is a plain column comparison: treat it as derived.
            if (fullPath.StartsWith(pathBase, StringComparison.Ordinal))
                return new ColumnRef(column, innerText + fullPath[pathBase.Length..]);
            return new ColumnRef(column, fullPath, Transform: fullPath);
        }
    }

    /// <summary>Node text without leading comments and with whitespace collapsed.</summary>
    public static string Text(SyntaxNode node) => CollapseWhitespace(node.ToString(IncludeTrivia.Interior));

    /// <summary>
    /// `where not(X in ("a", "b"))` excludes a and b just like `X !in (...)`. By De Morgan this also
    /// holds for not(A and B): each comparison inside is an exclusion. Two nots cancel out.
    /// </summary>
    private static bool IsInsideNot(SyntaxNode node)
    {
        var inside = false;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is FunctionCallExpression { Name.SimpleName: "not" })
                inside = !inside;
        }
        return inside;
    }

    public static string CollapseWhitespace(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\$(left|right)\.", RegexOptions.IgnoreCase)]
    private static partial Regex JoinKeyReference();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
