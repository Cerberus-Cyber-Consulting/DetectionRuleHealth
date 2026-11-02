using System.Reflection;

namespace RuleHealth.Core;

/// <summary>The running version, shown by --version, /healthz, /api/app and the UI.</summary>
public static class AppInfo
{
    public const string ProductName = "Detection Rule Health";

    /// <summary>
    /// SemVer from the build (-p:Version=0.1.0-beta.1 in the image, 0.0.0-dev locally). The SDK appends
    /// "+&lt;commit&gt;" to the informational version; that part is dropped so update checks compare clean versions.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "0.0.0-dev";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
