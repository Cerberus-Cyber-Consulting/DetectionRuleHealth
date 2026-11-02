using System.Text.RegularExpressions;

namespace RuleHealth.Core.Markdown;

/// <summary>
/// Builds a browsable link base (https://github.com/owner/repo/blob/branch/) from a clone's .git
/// folder, so rules from a markdown repo link back to their source. Reads the files directly instead
/// of running git, which may not be on PATH.
/// </summary>
public static partial class GitOrigin
{
    public static string? TryGetBlobBaseUrl(string repoRoot)
    {
        var gitDir = Path.Combine(repoRoot, ".git");
        var config = Path.Combine(gitDir, "config");
        var head = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(config) || !File.Exists(head))
            return null;

        var remote = OriginUrl().Match(File.ReadAllText(config));
        var branch = HeadBranch().Match(File.ReadAllText(head));
        if (!remote.Success || !branch.Success)
            return null;

        var url = remote.Groups[1].Value.Trim();
        // git@github.com:owner/repo.git → https://github.com/owner/repo
        var ssh = SshUrl().Match(url);
        if (ssh.Success)
            url = $"https://{ssh.Groups[1].Value}/{ssh.Groups[2].Value}";
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        return $"{url.TrimEnd('/')}/blob/{branch.Groups[1].Value.Trim()}/";
    }

    [GeneratedRegex(@"\[remote ""origin""\][^\[]*?url\s*=\s*(\S+)", RegexOptions.Singleline)]
    private static partial Regex OriginUrl();

    [GeneratedRegex(@"^ref:\s*refs/heads/(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadBranch();

    [GeneratedRegex(@"^git@([^:]+):(.+)$")]
    private static partial Regex SshUrl();
}
