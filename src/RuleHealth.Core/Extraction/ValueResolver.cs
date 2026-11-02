using Kusto.Language.Syntax;

namespace RuleHealth.Core.Extraction;

/// <summary>
/// Values on the value side of a comparison. Kind is "Literal" when every value is known from the
/// query text, otherwise "Runtime:&lt;why&gt;" (toscalar, subquery, unknown name, ...).
/// </summary>
public sealed record ResolvedValues(IReadOnlyList<string> Values, string Kind)
{
    public const string LiteralKind = "Literal";

    public bool IsLiteral => Kind == LiteralKind;

    public static ResolvedValues Runtime(string why) => new(Array.Empty<string>(), "Runtime:" + why);
}

/// <summary>Resolves a value expression to literal strings, following let statements.</summary>
public static class ValueResolver
{
    private const int MaxDepth = 5;

    private static readonly HashSet<string> ListFunctions = new(StringComparer.OrdinalIgnoreCase) { "pack_array", "dynamic" };
    private static readonly HashSet<string> TextFunctions = new(StringComparer.OrdinalIgnoreCase) { "tolower", "toupper", "tostring", "strcat" };

    /// <summary>let name → bound expression. The last binding of a name wins.</summary>
    public static Dictionary<string, Expression> CollectLets(SyntaxNode root)
    {
        var lets = new Dictionary<string, Expression>(StringComparer.Ordinal);
        foreach (var let in root.GetDescendantsOrSelf<LetStatement>())
            lets[let.Name.SimpleName] = let.Expression;
        return lets;
    }

    public static ResolvedValues Resolve(SyntaxNode? expression, IReadOnlyDictionary<string, Expression> lets, int depth = 0)
    {
        if (expression is null || depth > MaxDepth)
            return ResolvedValues.Runtime("unknown");

        switch (expression)
        {
            case ParenthesizedExpression parenthesized:
                return Resolve(parenthesized.Expression, lets, depth + 1);

            case LiteralExpression or CompoundStringLiteralExpression:
                return Literal(LiteralText((Expression)expression));

            case DynamicExpression dynamic:
                // dynamic(["a", "b"]) or dynamic({"k": "v"}): every literal inside.
                return Literal(dynamic.GetDescendantsOrSelf<LiteralExpression>().Select(LiteralText).ToArray());

            case ExpressionList list:
                return Combine(list.Expressions.Select(e => Resolve(e.Element, lets, depth + 1)).ToList());

            case NameReference name:
                return lets.TryGetValue(name.SimpleName, out var bound)
                    ? Resolve(bound, lets, depth + 1)
                    : ResolvedValues.Runtime($"name '{name.SimpleName}'");

            case FunctionCallExpression call:
                var function = call.Name.SimpleName;
                var arguments = call.ArgumentList.Expressions.Select(e => Resolve(e.Element, lets, depth + 1)).ToList();
                if (ListFunctions.Contains(function))
                    // Like the spike: the list counts as literal even if an element is not (keeps the known ones).
                    return Literal(arguments.SelectMany(a => a.Values).ToArray());
                if (TextFunctions.Contains(function))
                {
                    var combined = Combine(arguments);
                    if (combined.IsLiteral && string.Equals(function, "strcat", StringComparison.OrdinalIgnoreCase))
                        return Literal(string.Concat(combined.Values));
                    return combined;
                }
                return ResolvedValues.Runtime(function + "()");
        }

        if (expression is Expression { IsConstant: true, ConstantValueInfo: { } constant })
            return Literal(constant.ValueText ?? "");

        return ResolvedValues.Runtime(expression.Kind.ToString());
    }

    private static ResolvedValues Literal(params string[] values) => new(values, ResolvedValues.LiteralKind);

    /// <summary>All values; the first non-literal kind wins so the comparison is reported as runtime.</summary>
    private static ResolvedValues Combine(IReadOnlyList<ResolvedValues> parts)
    {
        var values = parts.SelectMany(p => p.Values).ToArray();
        var runtime = parts.FirstOrDefault(p => !p.IsLiteral);
        return new ResolvedValues(values, runtime?.Kind ?? ResolvedValues.LiteralKind);
    }

    /// <summary>The literal's value (unescaped, verbatim strings resolved), else its source text.</summary>
    private static string LiteralText(Expression literal) =>
        literal.ConstantValueInfo is { } constant ? constant.ValueText ?? "" : literal.ToString().Trim();
}
