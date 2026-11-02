namespace RuleHealth.Tests;

/// <summary>Locates the repository root (the folder with RuleHealth.sln) from the test binaries.</summary>
public static class TestPaths
{
    public static readonly string RepoRoot = FindRepoRoot();
    public static readonly string Schemas = Path.Combine(RepoRoot, "schemas");
    public static readonly string Samples = Path.Combine(RepoRoot, "samples");
    public static readonly string Ui = Path.Combine(RepoRoot, "ui");

    /// <summary>Every sample document as (tenant folder, document name, full path).</summary>
    public static IEnumerable<(string Tenant, string Document, string Path)> SampleFiles() =>
        Directory.GetDirectories(Samples)
            .SelectMany(dir => Directory.GetFiles(dir, "*.json").Select(file =>
                (System.IO.Path.GetFileName(dir), System.IO.Path.GetFileNameWithoutExtension(file), file)))
            .OrderBy(x => x.Item3, StringComparer.Ordinal);

    /// <summary>A fresh empty folder under the temp directory; deleted by the caller.</summary>
    public static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rulehealth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RuleHealth.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("RuleHealth.sln not found above " + AppContext.BaseDirectory);
    }
}
