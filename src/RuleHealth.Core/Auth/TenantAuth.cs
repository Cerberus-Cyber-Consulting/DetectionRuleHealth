using RuleHealth.Core.Model;

namespace RuleHealth.Core.Auth;

/// <summary>How a tenant signs in, from tenant.json.</summary>
public static class TenantAuth
{
    /// <summary>
    /// The tenant id and the client profile for the PKCE sign-in, or a <see cref="RuleHealthException"/>
    /// saying why this tenant cannot sign in. Pre-M8 files with mode "browser" and a client id sign in
    /// with that app registration; the Azure CLI modes are gone and the tenant has to be connected again.
    /// </summary>
    public static (string TenantId, ClientProfile Profile) Require(TenantDocument tenant)
    {
        if (tenant.Auth.Mode == AuthModes.Offline)
            throw new RuleHealthException(
                $"'{tenant.Alias}' is an offline tenant (markdown rules): there is no tenant to sign in to.") { HttpStatus = 409 };
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            throw new RuleHealthException($"tenant.json for '{tenant.Alias}' has no tenant ID. Add the tenant again.") { HttpStatus = 409 };
        if (tenant.Auth.Mode == AuthModes.MicrosoftApps)
            return (tenant.TenantId, ClientProfile.MicrosoftApps);
        if (tenant.Auth.Mode is AuthModes.Pkce or AuthModes.Browser && !string.IsNullOrWhiteSpace(tenant.Auth.ClientId))
            return (tenant.TenantId, ClientProfile.OwnApp(tenant.Auth.ClientId));
        throw new RuleHealthException(
            $"'{tenant.Alias}' was added with a sign-in this version no longer supports ({tenant.Auth.Mode}). " +
            "Connect it again: with your own app registration (recommended) or with Microsoft's apps (quick start); " +
            "the collected data stays.") { HttpStatus = 409 };
    }
}
