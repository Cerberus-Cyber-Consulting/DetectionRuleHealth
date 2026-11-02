using System.Text.RegularExpressions;

namespace RuleHealth.Core;

/// <summary>
/// Runtime settings from the RULEHEALTH_* environment variables. The defaults are for running from
/// the repository; the Docker image sets its own values (see Dockerfile). Command-line options such as
/// --data override them. Invalid values fail at startup with a message naming the variable, so a typo
/// in a compose file is not silently ignored.
/// </summary>
public sealed record AppSettings
{
    public const int DefaultPort = 5180;

    /// <summary>Data folder with one subfolder per tenant (RULEHEALTH_DATA).</summary>
    public string DataDir { get; init; } = "data";

    /// <summary>HttpListener prefix (RULEHEALTH_LISTEN). Inside a container it must be http://+:5180/.</summary>
    public string Listen { get; init; } = $"http://localhost:{DefaultPort}/";

    /// <summary>The address the browser uses (RULEHEALTH_PUBLIC_URL), without a trailing slash.
    /// Origin of the sign-in redirect URI and of the Host/Origin checks.</summary>
    public string PublicUrl { get; init; } = $"http://localhost:{DefaultPort}";

    /// <summary>Daily "new version available" call to the GitHub Releases API (RULEHEALTH_UPDATE_CHECK).</summary>
    public bool UpdateCheck { get; init; } = true;

    /// <summary>Read-only demo data offered by the setup wizard (RULEHEALTH_SAMPLES).</summary>
    public string SamplesDir { get; init; } = "samples";

    /// <summary>Static UI folder (RULEHEALTH_UI); null = search next to the binary and up from it.</summary>
    public string? UiDir { get; init; }

    /// <summary>info or debug (RULEHEALTH_LOG_LEVEL). debug logs one line per outgoing HTTP call.</summary>
    public string LogLevel { get; init; } = "info";

    public bool Debug => LogLevel == "debug";

    /// <summary>Port of <see cref="Listen"/>; the health check calls it on loopback.</summary>
    public int ListenPort => PortOf(Listen);

    public static AppSettings FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>Reads the variables through <paramref name="get"/> (tests pass a dictionary).</summary>
    public static AppSettings FromEnvironment(Func<string, string?> get)
    {
        var defaults = new AppSettings();
        return new AppSettings
        {
            DataDir = Value(get, "RULEHEALTH_DATA") ?? defaults.DataDir,
            Listen = Value(get, "RULEHEALTH_LISTEN") is { } listen ? ParseListen(listen) : defaults.Listen,
            PublicUrl = Value(get, "RULEHEALTH_PUBLIC_URL") is { } url ? ParsePublicUrl(url) : defaults.PublicUrl,
            UpdateCheck = Value(get, "RULEHEALTH_UPDATE_CHECK") is { } check ? ParseBool("RULEHEALTH_UPDATE_CHECK", check) : defaults.UpdateCheck,
            SamplesDir = Value(get, "RULEHEALTH_SAMPLES") ?? defaults.SamplesDir,
            UiDir = Value(get, "RULEHEALTH_UI"),
            LogLevel = Value(get, "RULEHEALTH_LOG_LEVEL") is { } level ? ParseLogLevel(level) : defaults.LogLevel,
        };
    }

    // Empty counts as unset: `RULEHEALTH_DATA=` in a compose file should mean "default", not "current folder".
    private static string? Value(Func<string, string?> get, string name) =>
        get(name) is { } value && value.Trim().Length > 0 ? value.Trim() : null;

    private static string ParseListen(string value)
    {
        // HttpListener prefixes: http://host:port/ with host = localhost, an address, + or *.
        if (!Regex.IsMatch(value, @"^http://[^/:]+:\d{1,5}/?$"))
            throw new RuleHealthException($"RULEHEALTH_LISTEN must look like http://localhost:{DefaultPort}/ or http://+:{DefaultPort}/, got '{value}'.");
        return value.EndsWith('/') ? value : value + "/";
    }

    private static string ParsePublicUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0)
            throw new RuleHealthException($"RULEHEALTH_PUBLIC_URL must be an origin such as http://localhost:{DefaultPort}, got '{value}'.");
        return value.TrimEnd('/');
    }

    private static bool ParseBool(string name, string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new RuleHealthException($"{name} must be true or false, got '{value}'."),
    };

    private static string ParseLogLevel(string value) => value.ToLowerInvariant() switch
    {
        "info" or "debug" => value.ToLowerInvariant(),
        _ => throw new RuleHealthException($"RULEHEALTH_LOG_LEVEL must be info or debug, got '{value}'."),
    };

    private static int PortOf(string prefix) =>
        int.Parse(Regex.Match(prefix, @":(\d+)/$").Groups[1].Value);
}
