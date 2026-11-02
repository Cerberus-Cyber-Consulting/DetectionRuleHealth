using System.Net;
using RuleHealth.Core;
using RuleHealth.Core.Auth;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Cli.Serve;

/// <summary>
/// The sign-in routes (skill rulehealth-auth):
///   GET  /auth/login?tenant={alias}   → 302 to Entra's authorize endpoint (top-level navigation)
///   GET  /auth/callback               ← Entra's redirect: code + state, an error, or the admin consent return
///   POST /auth/logout?tenant={alias}  → drop that tenant's tokens
///   GET  /api/session                 → { csrf, signedIn[], signIn } — never a token
/// The callback ends on the UI (see Landing); what happened is kept in the browser session and handed to
/// the UI once through /api/session, so no error text or code ends up in a URL or in the history.
/// Never logged: codes, tokens, the callback URL.
/// </summary>
internal sealed class AuthRoutes(JsonStore store, TokenStore tokens, PkceAuthorizationService signIn, RequestGuard guard, Action<string> log)
{
    /// <summary>
    /// Where the browser lands after a sign-in or admin consent, from the `next` key the UI passed to
    /// /auth/login (or the `state` of an admin consent link). Only these fixed routes: never a URL from
    /// the request, so the callback cannot be used to redirect anywhere else.
    /// </summary>
    internal static string Landing(string? next, bool ok) => next switch
    {
        // The wizard: a sign-in moves on to the workspace step; a failure stays on the sign-in step to explain it.
        "setup" => ok ? "/#/setup/workspaces" : "/#/setup/signin",
        "settings" => "/#/settings",
        _ => "/#/data",
    };

    public async Task<bool> TryRouteAsync(HttpListenerRequest request, HttpListenerResponse response, string[] segments)
    {
        switch (segments)
        {
            case ["api", "session"]:
                if (request.HttpMethod != "GET") { StaticServer.SendError(response, 405, "Use GET."); return true; }
                await SendSessionAsync(request, response);
                return true;
            case ["auth", "login"]:
                if (request.HttpMethod != "GET") { StaticServer.SendError(response, 405, "Use GET."); return true; }
                Login(request, response);
                return true;
            case ["auth", "callback"]:
                if (request.HttpMethod != "GET") { StaticServer.SendError(response, 405, "Use GET."); return true; }
                await CallbackAsync(request, response);
                return true;
            case ["auth", "logout"]:
                if (request.HttpMethod != "POST") { StaticServer.SendError(response, 405, "Use POST."); return true; }
                Logout(request, response);
                return true;
            case ["auth", ..]:
                StaticServer.SendError(response, 404, "Not found.");
                return true;
            default:
                return false;
        }
    }

    private async Task SendSessionAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var session = guard.Ensure(request, response);
        await StaticServer.SendJsonAsync(response, new
        {
            csrf = session.Csrf,
            signedIn = tokens.All.Select(s => new { alias = s.Alias, upn = s.Upn, tenantId = s.TenantId, signedInAt = s.SignedInAt.UtcDateTime }),
            signIn = session.TakeNotice(),
        });
    }

    private void Login(HttpListenerRequest request, HttpListenerResponse response)
    {
        var alias = request.QueryString["tenant"];
        if (!Documents.IsValidAlias(alias) || store.TryRead<TenantDocument>(alias!, Documents.Tenant) is not { } tenant)
        {
            StaticServer.SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }

        var next = request.QueryString["next"] is "setup" or "settings" ? request.QueryString["next"]! : "data";
        var session = guard.Ensure(request, response);
        try
        {
            // consent=1: an administrator wants Entra's consent prompt (Microsoft's apps: "on behalf of your organization").
            Redirect(response, signIn.Begin(tenant, session.Id, next, consent: request.QueryString["consent"] == "1"));
            log($"  sign-in started for '{tenant.Alias}'");
        }
        catch (RuleHealthException ex)
        {
            session.SetNotice(new SignInNotice(tenant.Alias, false, ex.Message));
            Redirect(response, Landing(next, ok: false));
        }
    }

    /// <summary>
    /// Microsoft's apps register http://localhost without a path, so their sign-in comes back to the
    /// site root. Only a request carrying a state plus a code or an error is a callback; anything else
    /// on "/" is the UI.
    /// </summary>
    public static bool IsRootCallback(System.Collections.Specialized.NameValueCollection query) =>
        query["state"] is { Length: > 0 } && (query["code"] is not null || query["error"] is not null);

    public Task RootCallbackAsync(HttpListenerRequest request, HttpListenerResponse response) => CallbackAsync(request, response);

    private async Task CallbackAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var query = request.QueryString;
        var session = guard.Find(request);

        // Admin consent return (/adminconsent?…&redirect_uri=…/auth/callback): no code, nothing to redeem.
        if (query["admin_consent"] is { } consent && query["code"] is null)
        {
            (session ?? guard.Ensure(request, response)).SetNotice(string.Equals(consent, "True", StringComparison.OrdinalIgnoreCase)
                ? new SignInNotice(null, true, "Admin consent granted. You can sign in now.")
                : new SignInNotice(null, false, "Admin consent was not granted."));
            // The consent link's state is the UI's `next` key; a consent is not a sign-in, so it lands on the sign-in step.
            Redirect(response, Landing(query["state"], ok: false));
            return;
        }

        // Single use: the state is taken out whatever happens next.
        var pending = signIn.Take(query["state"], session?.Id);

        if (query["error"] is { } error)
        {
            var description = query["error_description"] ?? error;
            var code = EntraException.CodeIn(description);
            (session ?? guard.Ensure(request, response)).SetNotice(new SignInNotice(pending?.Alias, false,
                EntraException.FirstLine(description), code, EntraException.CorrelationIn(description), AuthHints.ForCode(code)));
            log($"  sign-in failed{(pending is null ? "" : $" for '{pending.Alias}'")}: {code ?? error}");
            Redirect(response, Landing(pending?.ReturnTo, ok: false));
            return;
        }

        if (pending is null || session is null)
        {
            StaticServer.SendError(response, 400,
                "This sign-in expired or was started in another browser window. Go back to Detection Rule Health and sign in again.");
            return;
        }
        if (query["code"] is not { Length: > 0 } authorizationCode)
        {
            StaticServer.SendError(response, 400, "The sign-in returned no code. Sign in again.");
            return;
        }

        var ok = false;
        try
        {
            var outcome = await signIn.CompleteAsync(pending, authorizationCode, CancellationToken.None);
            if (outcome.NextUrl is not null)
            {
                // Microsoft's apps: Graph Command Line Tools is done, Azure PowerShell signs in next.
                log($"  sign-in step 1 of 2 done for '{pending.Alias}'");
                Redirect(response, outcome.NextUrl);
                return;
            }
            var signedIn = outcome.Session!;
            session.SetNotice(new SignInNotice(pending.Alias, true, $"Signed in{(signedIn.Upn is null ? "" : " as " + signedIn.Upn)}."));
            log($"  signed in to '{pending.Alias}'");
            ok = true;
        }
        catch (RuleHealthException ex)
        {
            // Entra's own sentence as the detail, our explanation as the hint; other failures (another tenant) say it all.
            var entra = ex as EntraException;
            session.SetNotice(entra is null
                ? new SignInNotice(pending.Alias, false, ex.Message)
                : new SignInNotice(pending.Alias, false, string.IsNullOrEmpty(entra.Description) ? entra.Error : entra.Description,
                    entra.Code, entra.CorrelationId, AuthHints.ForCode(entra.Code)));
            log($"  sign-in failed for '{pending.Alias}': {entra?.Code ?? entra?.Error ?? "see the UI"}");
        }
        Redirect(response, Landing(pending.ReturnTo, ok));
    }

    private void Logout(HttpListenerRequest request, HttpListenerResponse response)
    {
        var alias = request.QueryString["tenant"];
        if (!Documents.IsValidAlias(alias))
        {
            StaticServer.SendError(response, 400, "Give the tenant alias: /auth/logout?tenant=<alias>.");
            return;
        }
        if (tokens.Remove(alias!))
            log($"  signed out of '{alias}'");
        response.StatusCode = 204;
    }

    private static void Redirect(HttpListenerResponse response, string location)
    {
        response.StatusCode = 302;
        response.RedirectLocation = location;
    }
}
