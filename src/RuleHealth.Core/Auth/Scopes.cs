namespace RuleHealth.Core.Auth;

/// <summary>The three APIs the engine calls; each gets its own access token from the same sign-in.</summary>
public enum ApiResource
{
    Graph,
    Arm,
    LogAnalytics,
}

/// <summary>Token scopes per API. One delegated sign-in covers all three (see the ms-security-apis skill).</summary>
public static class Scopes
{
    public const string Graph = "https://graph.microsoft.com/.default";
    public const string Arm = "https://management.azure.com/.default";

    /// <summary>Also valid for api.loganalytics.azure.com.</summary>
    public const string LogAnalytics = "https://api.loganalytics.io/.default";

    /// <summary>
    /// The authorize request: Graph's permissions, a refresh token (offline_access) to get the ARM and
    /// Log Analytics tokens without another prompt, and an id token (openid profile) that says who signed in.
    /// </summary>
    public const string SignIn = Graph + " offline_access openid profile";

    /// <summary>What a token request for <paramref name="resource"/> asks for: one resource per request.</summary>
    public static string For(ApiResource resource) => resource switch
    {
        ApiResource.Graph => Graph,
        ApiResource.Arm => Arm,
        ApiResource.LogAnalytics => LogAnalytics,
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    } + " offline_access";
}
