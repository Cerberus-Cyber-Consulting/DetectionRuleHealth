using RuleHealth.Core.Model;

namespace RuleHealth.Core.Auth;

/// <summary>
/// Which public client signs in for which API, with which scopes and redirect path (skill
/// rulehealth-auth). Two options per tenant:
/// - own app registration (auth.mode "pkce", recommended): one client for everything, one sign-in;
/// - Microsoft's apps (auth.mode "microsoft-apps", quick start): Microsoft Graph Command Line Tools
///   for Graph and Microsoft Azure PowerShell for ARM and Log Analytics, two sign-ins in a row.
/// Two apps because Entra refuses a Microsoft app tokens for a Microsoft API it is not
/// preauthorized for (AADSTS65002): Graph Command Line Tools gets no ARM token (expected from the
/// documented rule; not documented for these two apps: VERIFY live). Microsoft's own wording for
/// 65002 is that tools reusing a Microsoft app id "must move to another app ID they register": the
/// quick start is a convenience Microsoft does not endorse, the own app registration is the supported path.
/// </summary>
public sealed record ClientProfile(string Mode, string GraphClientId, string AzureClientId, string RedirectPath)
{
    /// <summary>Microsoft Graph Command Line Tools, the public client behind Connect-MgGraph.</summary>
    public const string GraphCommandLineTools = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    /// <summary>Microsoft Azure PowerShell, the public client behind Connect-AzAccount.</summary>
    public const string AzurePowerShell = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>
    /// The Graph permissions the quick start asks for by name: with `.default`, Graph Command Line
    /// Tools would only get what was consented to it before.
    /// </summary>
    private const string GraphSecurityScopes =
        "https://graph.microsoft.com/CustomDetection.Read.All https://graph.microsoft.com/ThreatHunting.Read.All " +
        "https://graph.microsoft.com/SecurityIncident.Read.All https://graph.microsoft.com/SecurityAlert.Read.All";

    public static ClientProfile OwnApp(string clientId) => new(AuthModes.Pkce, clientId, clientId, "/auth/callback");

    /// <summary>Microsoft's apps register http://localhost without a path: the callback comes to the site root.</summary>
    public static readonly ClientProfile MicrosoftApps = new(AuthModes.MicrosoftApps, GraphCommandLineTools, AzurePowerShell, "/");

    /// <summary>One client for every API: one sign-in, one refresh token.</summary>
    public bool SingleClient => string.Equals(GraphClientId, AzureClientId, StringComparison.OrdinalIgnoreCase);

    public string ClientFor(ApiResource resource) => resource == ApiResource.Graph ? GraphClientId : AzureClientId;

    /// <summary>The redirect URI for this profile on <paramref name="publicUrl"/> (http://localhost:5180).</summary>
    public string RedirectUri(string publicUrl) =>
        // Entra ignores the port of a localhost redirect URI but matches the path; the Microsoft apps
        // registered "http://localhost", so no path at all.
        RedirectPath == "/" ? publicUrl.TrimEnd('/') : publicUrl.TrimEnd('/') + RedirectPath;

    /// <summary>The scope of a token request for <paramref name="resource"/> (code redemption or refresh).</summary>
    public string ScopeFor(ApiResource resource) =>
        resource == ApiResource.Graph && Mode == AuthModes.MicrosoftApps
            ? GraphSecurityScopes + " offline_access"
            : Scopes.For(resource);

    /// <summary>
    /// The authorize request of a sign-in step: step 1 is Graph (with openid profile for the id token);
    /// step 2, only for Microsoft's apps, is ARM through Azure PowerShell.
    /// </summary>
    public string SignInScope(int step) => step == 1
        ? (Mode == AuthModes.MicrosoftApps ? GraphSecurityScopes + " offline_access openid profile" : Scopes.SignIn)
        : Scopes.Arm + " offline_access openid profile";

    /// <summary>The API whose token a sign-in step redeems.</summary>
    public static ApiResource ResourceOfStep(int step) => step == 1 ? ApiResource.Graph : ApiResource.Arm;

    /// <summary>"Microsoft Graph Command Line Tools" etc. for a known client id; null for the user's own app.</summary>
    public static string? KnownAppName(string? clientId) => clientId?.ToLowerInvariant() switch
    {
        GraphCommandLineTools => "Microsoft Graph Command Line Tools",
        AzurePowerShell => "Microsoft Azure PowerShell",
        _ => null,
    };
}
