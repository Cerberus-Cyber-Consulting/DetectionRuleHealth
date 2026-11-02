using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Operations;

/// <summary>
/// The authenticated ApiConnection for a tenant, from its sign-in in the <see cref="TokenStore"/>.
/// Nothing signs in here: sign-in is a browser redirect (/auth/login), so a job or an operation can
/// only use a sign-in that already happened.
/// </summary>
public static class TenantConnection
{
    /// <param name="httpLog">Receives one line per HTTP call (verbose mode); null to stay quiet.</param>
    /// <param name="freshSignIn">Newly issued access tokens instead of cached ones, e.g. right after activating a PIM role.</param>
    public static ApiConnection Connect(TenantDocument tenant, TokenStore tokens, Action<string> log, Action<string>? httpLog,
        RawResponseLog? raw, bool freshSignIn = false)
    {
        var (tenantId, profile) = TenantAuth.Require(tenant);
        var session = tokens.Get(tenant.Alias)
            ?? throw new RuleHealthException(
                $"Not signed in to '{tenant.Alias}'. Sign in first; a sign-in lasts until Detection Rule Health restarts.") { HttpStatus = 401 };

        // tenant.json changed after the sign-in (added again with another tenant or app): the tokens
        // belong to the old one.
        if (!string.Equals(session.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) || session.Profile != profile)
        {
            tokens.Remove(tenant.Alias);
            throw new RuleHealthException(
                $"The tenant or sign-in option of '{tenant.Alias}' changed since you signed in. Sign in again.") { HttpStatus = 401 };
        }

        if (freshSignIn)
        {
            session.ForgetAccessTokens();
            log("Getting newly issued tokens (they carry roles activated since the last ones were issued)...");
        }
        log($"Using the sign-in of {session.Upn ?? "the signed-in account"}.");
        return ApiConnection.Create(session, httpLog, raw);
    }
}
