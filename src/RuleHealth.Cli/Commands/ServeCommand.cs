using RuleHealth.Cli.Serve;
using RuleHealth.Core;

namespace RuleHealth.Cli.Commands;

/// <summary>`rulehealth serve` — serves ui/ and the JSON API on RULEHEALTH_LISTEN until Ctrl+C (or docker stop).</summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        cli.AllowOnly("ui", "open", "samples");
        var settings = cli.Settings;
        var dataDir = Path.GetFullPath(cli.DataDirectory);
        // Read-only demo data for "Explore with demo data"; missing = the wizard does not offer it.
        var samplesDir = Path.GetFullPath(cli.Get("samples") ?? settings.SamplesDir);
        var uiDir = (cli.Get("ui") ?? settings.UiDir) is { } ui ? Path.GetFullPath(ui) : FindUiDirectory();

        if (!Directory.Exists(dataDir))
            throw new RuleHealthException($"Data folder {dataDir} does not exist. Use --data samples to see the sample tenant.");
        if (!File.Exists(Path.Combine(uiDir, "index.html")))
            throw new RuleHealthException($"No index.html in {uiDir}. Pass --ui <dir> or set RULEHEALTH_UI.");

        // RULEHEALTH_UPDATE_CHECK=false: no checker at all, so no call to GitHub. Failures only show with
        // RULEHEALTH_LOG_LEVEL=debug: an offline machine is normal, not an error.
        var updates = settings.UpdateCheck
            ? new Core.Updates.UpdateChecker(dataDir, AppInfo.Version, debugLog: settings.Debug ? Console.WriteLine : null)
            : null;
        using var server = new StaticServer(dataDir, uiDir, settings.Listen, settings.PublicUrl, Console.WriteLine)
        {
            UpdateCheckEnabled = settings.UpdateCheck,
            SamplesRoot = samplesDir,
            Updates = updates,
        };
        server.Start();
        using var stopChecks = new CancellationTokenSource();
        var checks = updates?.RunAsync(stopChecks.Token) ?? Task.CompletedTask;
        Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
        Console.WriteLine($"Serving {uiDir}");
        Console.WriteLine($"Data    {dataDir}");
        if (server.SamplesRoot is { } samples)
            Console.WriteLine($"Demo    {samples} (read-only)");
        Console.WriteLine($"Listen  {settings.Listen}");
        Console.WriteLine($"Open    {settings.PublicUrl}   (Ctrl+C to stop)");
        if (cli.Has("open"))
        {
            // The default browser; the shell resolves the URL, nothing is executed from it.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(settings.PublicUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Console.WriteLine($"Could not open the browser ({ex.Message}); open the URL above yourself."); }
        }

        var stopped = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let us shut down cleanly instead of killing the process
            stopped.TrySetResult();
        };
        await stopped.Task;
        Console.WriteLine("Stopping.");
        stopChecks.Cancel();
        await checks;
        return 0;
    }

    /// <summary>
    /// Finds ui/ next to the binary (published image: /app/ui) or by walking up from the binary's
    /// directory, then from the current directory. Covers the image, `dotnet run` from the repo root
    /// and running the built dll from bin/.
    /// </summary>
    private static string FindUiDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "ui");
                if (File.Exists(Path.Combine(candidate, "index.html")))
                    return candidate;
            }
        }
        throw new RuleHealthException("Could not find the ui/ folder. Run from the repository, pass --ui <dir> or set RULEHEALTH_UI.");
    }
}
