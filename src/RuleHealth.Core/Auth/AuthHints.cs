using System.Text.RegularExpressions;

namespace RuleHealth.Core.Auth;

/// <summary>
/// Turns the Entra errors a misconfigured app registration (or account) produces into the fix. The
/// raw AADSTS text names resources by app id and rarely says what to change. Table: skill rulehealth-auth.
/// </summary>
public static partial class AuthHints
{
    // Resource app ids → what to add under API permissions (all delegated).
    private static readonly Dictionary<string, string> Permissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["797f4846-ba00-4fd7-ba43-dac1f8f63013"] = "Azure Service Management > user_impersonation",
        ["00000003-0000-0000-c000-000000000000"] =
            "Microsoft Graph > CustomDetection.Read.All, ThreatHunting.Read.All, SecurityIncident.Read.All, SecurityAlert.Read.All",
        ["ca7f3f0b-7d91-482c-8e09-c5d840d0eac5"] = "Log Analytics API > Data.Read",
    };

    private const string Consent =
        "Consent is missing: an administrator of this tenant must grant admin consent. Own app registration: " +
        "API permissions > Grant admin consent, or the admin consent link in the setup guide. Microsoft's apps: the " +
        "administrator signs in through the wizard with \"Sign in and consent\" and consents on behalf of the organization.";

    private const string Mfa = "Multi-factor authentication is required: complete it in the sign-in window, then try again.";

    private static readonly Dictionary<string, string> ByCode = new(StringComparer.Ordinal)
    {
        ["AADSTS50011"] = "Redirect URI mismatch: the app registration must have http://localhost:5180/auth/callback " +
                          "under Authentication > Mobile and desktop applications. (With Microsoft's apps the request uses " +
                          "http://localhost:5180; if Microsoft changed their redirect URIs, use your own app registration.)",
        ["AADSTS700016"] = "The client ID was not found in this tenant: check the Application (client) ID and the tenant ID.",
        ["AADSTS65001"] = Consent,
        ["AADSTS90094"] = Consent,
        ["AADSTS90095"] = Consent,
        ["AADSTS9002327"] = "The redirect URI is registered as a Single-page application: register it under " +
                            "Mobile and desktop applications instead. (With Microsoft's apps: their registration cannot be " +
                            "used from here; use your own app registration.)",
        ["AADSTS7000218"] = "The redirect URI is registered under the Web platform, which wants a client secret: move it to " +
                            "Mobile and desktop applications. Do not add a secret and leave \"Allow public client flows\" off.",
        ["AADSTS50105"] = "Your account is not assigned to this app (the enterprise app requires assignment): " +
                          "assign your user or a group to it. With Microsoft's apps, your organisation restricts Microsoft Graph " +
                          "Command Line Tools or Azure PowerShell to assigned users: ask for access or use your own app registration.",
        ["AADSTS7000112"] = "Your organisation disabled this Microsoft app (Microsoft Graph Command Line Tools or Azure PowerShell). " +
                            "Use your own app registration: Settings > Change sign-in.",
        ["AADSTS53003"] = "Blocked by Conditional Access: ask your identity administrator which policy applies to this app. With " +
                          "Microsoft's apps, policies on \"Windows Azure Service Management API\" also cover the Azure PowerShell " +
                          "sign-in and Log Analytics.",
        ["AADSTS50076"] = Mfa,
        ["AADSTS50079"] = Mfa,
        ["AADSTS65002"] = "Microsoft does not allow this Microsoft app to call that API (no preauthorization). Use your own " +
                          "app registration for this tenant: Settings > Change sign-in.",
        ["AADSTS90002"] = "The tenant ID is wrong. Use the Tenant ID (Entra ID → Overview → Tenant ID), not a user's Object ID, " +
                          "an app's client ID or a subscription ID.",
    };

    /// <summary>The plain explanation for an AADSTS code, or null when it is not one we know.</summary>
    public static string? ForCode(string? code)
    {
        if (code is null)
            return null;
        return ByCode.TryGetValue(code, out var hint) ? hint : null;
    }

    /// <summary>A hint to append to an error message, or null when the error is not one we recognise.</summary>
    public static string? For(string errorMessage)
    {
        // Defender: the token is fine but the account has no (active) Defender permission.
        if (errorMessage.Contains("Missing user permissions", StringComparison.Ordinal))
            return "Defender sees no permissions for your account. If your Security Reader / Defender role is a PIM role, " +
                   "activate it, then choose Sign in again and retry (a token issued before the activation does not carry the role).";
        // AADSTS650057: the app registration does not list the requested resource at all.
        if (errorMessage.Contains("AADSTS650057", StringComparison.Ordinal))
        {
            var resource = ResourceAppId().Match(errorMessage);
            var permission = resource.Success && Permissions.TryGetValue(resource.Groups[1].Value, out var p)
                ? p
                : "the permissions listed in the setup guide";
            return $"Fix: in the app registration, API permissions > Add a permission > {permission} (Delegated), " +
                   "then Grant admin consent.";
        }
        return ForCode(EntraException.CodeIn(errorMessage));
    }

    [GeneratedRegex(@"Resource app ID:\s*([0-9a-fA-F-]{36})")]
    private static partial Regex ResourceAppId();
}
