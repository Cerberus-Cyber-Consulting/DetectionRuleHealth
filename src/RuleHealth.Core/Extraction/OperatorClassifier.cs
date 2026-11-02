namespace RuleHealth.Core.Extraction;

/// <summary>
/// Maps an operator as written (==, !in~, has_any, matches regex, ...) to the operatorClass stored
/// in a check, plus whether it is negated. Negation lives on the occurrence, so `X == "a"` and
/// `X != "a"` share one check (one decision covers both).
/// </summary>
public static class OperatorClassifier
{
    private static readonly Dictionary<string, string> Positive = new(StringComparer.Ordinal)
    {
        ["=="] = "eq",
        ["in"] = "eq",
        ["=~"] = "eq_ci",
        ["in~"] = "eq_ci",
        ["has"] = "has",
        ["has_any"] = "has",
        ["has_all"] = "has",
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
        ["matches regex"] = "regex",
        ["like"] = "like",
        ["like_cs"] = "like_cs",
    };

    /// <summary>Returns null for operators that have no operatorClass (the caller reports them).</summary>
    public static (string OperatorClass, bool Negated)? Classify(string op)
    {
        // Normalise "matches   regex" to a single space.
        var text = string.Join(' ', op.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (text == "!=")
            return ("eq", true);
        if (text == "!~")
            return ("eq_ci", true);
        if (Positive.TryGetValue(text, out var positive))
            return (positive, false);
        if (text.StartsWith('!') && Positive.TryGetValue(text[1..], out var negated))
            return (negated, true);
        return null;
    }
}
