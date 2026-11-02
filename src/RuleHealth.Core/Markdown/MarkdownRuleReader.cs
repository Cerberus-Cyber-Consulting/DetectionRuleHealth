using System.Text.RegularExpressions;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Markdown;

/// <summary>
/// Offline rule source: every ```KQL block in a rules repo laid out like HybridBrothers
/// (a "# Title" line, then "## Defender XDR" / "## Sentinel" sections each holding one block).
/// Lets you review a detection repo before it is deployed, and powers the golden test.
/// </summary>
public static partial class MarkdownRuleReader
{
    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase) { "README.md", "DetectionTemplate.md" };

    public static List<Rule> Read(string repoPath)
    {
        var root = Path.GetFullPath(repoPath);
        if (!Directory.Exists(root))
            throw new RuleHealthException($"Rules folder {root} does not exist.");

        var originBase = GitOrigin.TryGetBlobBaseUrl(root);
        var rules = new List<Rule>();

        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !SkippedFiles.Contains(Path.GetFileName(rel)))
            .Where(rel => !rel.Split('/').Any(part => part.StartsWith('.'))) // .git, .github
            .OrderBy(rel => rel, StringComparer.Ordinal);

        foreach (var relativePath in files)
        {
            var lines = File.ReadAllLines(Path.Combine(root, relativePath));
            var title = ReadTitle(lines) ?? Path.GetFileNameWithoutExtension(relativePath);
            foreach (var block in ReadBlocks(lines))
            {
                if (KqlPreprocessor.IsPlaceholder(block.Kql))
                    continue;
                rules.Add(new Rule
                {
                    RuleId = $"md:{relativePath}#{block.Index}",
                    Source = RuleSources.Markdown,
                    Kind = block.Heading,
                    DisplayName = block.Heading.Length > 0 ? $"{title} ({block.Heading})" : title,
                    Description = null,
                    Enabled = true,
                    Severity = null,
                    QueryText = block.Kql,
                    Frequency = null,
                    LastModifiedAt = null,
                    // The Defender portal binds both Sentinel and XDR blocks against the unified schema.
                    SchemaContext = SchemaContexts.Unified,
                    Origin = originBase is null ? Path.Combine(root, relativePath) : originBase + relativePath,
                });
            }
        }
        return rules;
    }

    private sealed record KqlBlock(string Heading, int Index, string Kql);

    /// <summary>Blocks are numbered per file from 1, counting N/A placeholders, so ruleIds stay stable.</summary>
    private static IEnumerable<KqlBlock> ReadBlocks(string[] lines)
    {
        var heading = "";
        var index = 0;
        System.Text.StringBuilder? buffer = null;

        foreach (var line in lines)
        {
            if (buffer is null)
            {
                var section = SectionHeading().Match(line);
                if (section.Success)
                    heading = section.Groups[1].Value.Trim();
                if (KqlFenceStart().IsMatch(line))
                    buffer = new System.Text.StringBuilder();
                continue;
            }

            if (FenceEnd().IsMatch(line))
            {
                index++;
                yield return new KqlBlock(heading, index, buffer.ToString());
                buffer = null;
                continue;
            }
            buffer.Append(line).Append('\n');
        }
    }

    /// <summary>First "# Title" line, without markdown emphasis.</summary>
    private static string? ReadTitle(string[] lines)
    {
        foreach (var line in lines)
        {
            var match = Title().Match(line);
            if (match.Success)
                return match.Groups[1].Value.Trim().Trim('*', '_').Trim();
        }
        return null;
    }

    [GeneratedRegex(@"^#\s+(.+)$")]
    private static partial Regex Title();

    [GeneratedRegex(@"^##\s+(.+)$")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^```\s*kql\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KqlFenceStart();

    [GeneratedRegex(@"^```\s*$")]
    private static partial Regex FenceEnd();
}
