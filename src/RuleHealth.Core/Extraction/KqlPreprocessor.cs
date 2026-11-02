using System.Text.RegularExpressions;

namespace RuleHealth.Core.Extraction;

/// <summary>Text fixes needed before a rule query can be parsed.</summary>
public static partial class KqlPreprocessor
{
    /// <summary>Empty and "N/A" blocks are placeholders in rule repos, not queries.</summary>
    public static bool IsPlaceholder(string? queryText)
    {
        var trimmed = queryText?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Defender accepts a bare column name as the first argument of invoke FileProfile(...) and
    /// invoke DeviceFromIP(...); the parser does not, so quote it: FileProfile(SHA1) → FileProfile('SHA1').
    /// </summary>
    public static string Prepare(string queryText) => MagicFunctionArgument().Replace(queryText, "$1'$2'$3");

    [GeneratedRegex(@"(invoke\s+(?:FileProfile|DeviceFromIP)\(\s*)([^',)\s]+)(\s*[,)])")]
    private static partial Regex MagicFunctionArgument();
}
