using Kusto.Language.Symbols;
using Kusto.Language.Syntax;

namespace RuleHealth.Core.Extraction;

/// <summary>A column whose value flows into an expression; Key is the object key still to be selected from it, if known.</summary>
public sealed record FlowSource(ColumnSymbol Column, string? Key);

/// <summary>
/// Which columns' values can end up in the value of an expression. A column referenced only in a
/// condition, comparison, arithmetic, count or length does not carry its value into the result:
/// in `iff(TimeGenerated between (a .. b), true, false)` the value is true/false, never a
/// TimeGenerated value, so TimeGenerated is not a source. Unknown functions carry nothing (a missing
/// check is better than a false one).
///
/// Object keys are followed where they are known: for `Obj.DeviceType` with
/// `Obj = pack("DeviceName", DeviceName, "DeviceType", DeviceType)`, only DeviceType is a source.
/// </summary>
public static class ValueFlow
{
    /// <summary>Same value, different type, case or whitespace. A comparison through these is still on the column.</summary>
    private static readonly HashSet<string> Preserving = new(StringComparer.Ordinal)
    {
        "tostring", "tolower", "toupper", "trim", "trim_start", "trim_end", "todynamic", "parse_json", "toguid",
        "tolong", "toint", "todouble", "toreal", "todecimal", "tobool", "todatetime", "totimespan",
    };

    /// <summary>The result is built from (part of) the value of every argument.</summary>
    private static readonly HashSet<string> AllArguments = new(StringComparer.Ordinal)
    {
        "coalesce", "strcat", "strcat_array", "pack_array", "array_concat", "set_union", "set_intersect", "bag_merge", "zip",
        "make_set", "make_list", "make_bag", "take_any", "any", "max", "min", "arg_max", "arg_min",
        "url_decode", "base64_decode_tostring", "reverse",
    };

    /// <summary>The result is built from the value of specific arguments only.</summary>
    private static readonly Dictionary<string, Func<int, int, bool>> SomeArguments = new(StringComparer.Ordinal)
    {
        // iff(condition, then, else): the condition never reaches the result.
        ["iff"] = (i, _) => i is 1 or 2,
        ["iif"] = (i, _) => i is 1 or 2,
        // case(c1, v1, c2, v2, ..., else)
        ["case"] = (i, n) => i % 2 == 1 || i == n - 1,
        // pack("key", value, ...): keys are literals, values flow.
        ["pack"] = (i, _) => i % 2 == 1,
        ["bag_pack"] = (i, _) => i % 2 == 1,
        // extract(regex, group, source[, type]) / extract_all(regex, [groups,] source)
        ["extract"] = (i, _) => i == 2,
        ["extract_all"] = (i, n) => i == n - 1,
        ["substring"] = (i, _) => i == 0,
        ["split"] = (i, _) => i == 0,
        ["replace_string"] = (i, _) => i is 0 or 2,
        ["replace_regex"] = (i, _) => i is 0 or 2,
        ["parse_url"] = (i, _) => i == 0,
        ["parse_path"] = (i, _) => i == 0,
        ["array_slice"] = (i, _) => i == 0,
        ["strcat_delim"] = (i, _) => i >= 1,
        // aggregates with a predicate: the predicate does not flow
        ["make_set_if"] = (i, _) => i == 0,
        ["make_list_if"] = (i, _) => i == 0,
        ["make_bag_if"] = (i, _) => i == 0,
        ["maxif"] = (i, _) => i == 0,
        ["minif"] = (i, _) => i == 0,
        ["take_anyif"] = (i, _) => i == 0,
        ["anyif"] = (i, _) => i == 0,
    };

    /// <summary>Functions whose result still has the argument's object keys (so a pending key passes through).</summary>
    private static readonly HashSet<string> KeepKeys = new(StringComparer.Ordinal)
    {
        "tostring", "todynamic", "parse_json", "coalesce", "iff", "iif", "case",
        "make_set", "make_list", "make_set_if", "make_list_if", "make_bag", "make_bag_if", "bag_merge",
        "take_any", "take_anyif", "any", "anyif", "arg_max", "arg_min", "pack_array", "array_concat",
        "set_union", "set_intersect", "array_slice",
    };

    public static bool IsPreserving(FunctionCallExpression call) => Preserving.Contains(call.Name.SimpleName);

    /// <summary>Arguments whose value flows into the call's result; empty when none does.</summary>
    public static List<Expression> FlowingArguments(FunctionCallExpression call)
    {
        var arguments = call.ArgumentList.Expressions.Select(e => e.Element).ToList();
        var name = call.Name.SimpleName;
        if (Preserving.Contains(name) || AllArguments.Contains(name))
            return arguments;
        if (SomeArguments.TryGetValue(name, out var flows))
            return arguments.Where((_, i) => flows(i, arguments.Count)).ToList();
        return new List<Expression>();
    }

    /// <summary>
    /// Columns whose values flow into the value of the expression. <paramref name="key"/> is the object
    /// key the caller selects from that value (`Obj.DeviceType` → "DeviceType"), or null.
    /// </summary>
    public static List<FlowSource> SourceColumns(SyntaxNode? expression, string? key = null)
    {
        var sources = new List<FlowSource>();
        Collect(expression, key, sources, 0);
        return sources;
    }

    private static void Collect(SyntaxNode? node, string? key, List<FlowSource> sources, int depth)
    {
        if (node is null || depth > 20)
            return;
        switch (node)
        {
            case ParenthesizedExpression parenthesized:
                Collect(parenthesized.Expression, key, sources, depth + 1);
                return;

            case SimpleNamedExpression named:
                Collect(named.Expression, key, sources, depth + 1);
                return;

            case PathExpression or ElementExpression:
            {
                // Walk to the innermost base; its first selector is the key taken from the base's value.
                // Deeper selectors and the caller's key apply below that key and are not tracked.
                var innermost = node;
                var baseNode = BaseOf(node);
                while (baseNode is PathExpression or ElementExpression)
                {
                    innermost = baseNode;
                    baseNode = BaseOf(baseNode);
                }
                var before = sources.Count;
                Collect(baseNode, SelectorKey(innermost), sources, depth + 1);
                // graph-match: the base is a node/edge variable and the selector is the table column
                // (SourceNode.NodeName, anyEdge.EdgeLabel).
                if (sources.Count == before && innermost is PathExpression { Selector.ReferencedSymbol: ColumnSymbol selected })
                    sources.Add(new FlowSource(selected, null));
                return;
            }

            case FunctionCallExpression call:
            {
                var name = call.Name.SimpleName;
                var arguments = call.ArgumentList.Expressions.Select(e => e.Element).ToList();
                if (key is not null && (name is "pack" or "bag_pack"))
                {
                    // pack("k1", v1, "k2", v2): follow only the value stored under the selected key.
                    for (var i = 0; i + 1 < arguments.Count; i += 2)
                    {
                        if (arguments[i] is LiteralExpression { ConstantValueInfo.ValueText: var k } && k == key)
                        {
                            Collect(arguments[i + 1], null, sources, depth + 1);
                            return;
                        }
                    }
                    // Key not found as a literal: fall back to every value.
                }
                var passKey = KeepKeys.Contains(name) ? key : null;
                foreach (var argument in FlowingArguments(call))
                    Collect(argument, passKey, sources, depth + 1);
                return;
            }

            case NameReference { ReferencedSymbol: ColumnSymbol column }:
                // Also covers `parse Col with ... X ...`, where X's Source is the bare Col.
                sources.Add(new FlowSource(column, key));
                return;

            default:
                // Literals, comparisons, logic, arithmetic, in/between, subqueries: no column value.
                return;
        }
    }

    private static Expression BaseOf(SyntaxNode node) =>
        node is PathExpression path ? path.Expression : ((ElementExpression)node).Expression;

    /// <summary>Key selected by `.Name` or `["Name"]`; null when it is computed.</summary>
    public static string? SelectorKey(SyntaxNode pathOrElement) => pathOrElement switch
    {
        PathExpression { Selector: NameReference name } => name.SimpleName,
        ElementExpression { Selector: BracketedExpression { Expression: LiteralExpression { ConstantValueInfo.ValueText: var text } } } => text,
        _ => null,
    };
}
