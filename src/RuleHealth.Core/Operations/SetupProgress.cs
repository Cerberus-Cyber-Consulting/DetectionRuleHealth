using RuleHealth.Core.Model;

namespace RuleHealth.Core.Operations;

/// <summary>
/// Where the setup wizard continues for a tenant (skill rulehealth-onboarding, "Rules"). There is no
/// wizard-state file: the step follows from what is in data/&lt;alias&gt;/ and from the sign-in, so
/// leaving the wizard halfway and coming back picks up at the right step.
/// </summary>
public static class SetupProgress
{
    public const string App = "app";
    public const string SignIn = "signin";
    public const string Workspaces = "workspaces";
    public const string Collect = "collect";
    public const string Done = "done";

    /// <param name="tenant">tenant.json, or null when the tenant is not added yet.</param>
    /// <param name="signedIn">The tenant has a sign-in in this process.</param>
    /// <param name="hasRules">rules.json exists (a collection finished at least once).</param>
    public static string StepFor(TenantDocument? tenant, bool signedIn, bool hasRules)
    {
        if (tenant is null)
            return App;
        // Demo and markdown tenants have nothing to set up; collected data can be read without a sign-in.
        if (tenant.Auth.Mode == AuthModes.Offline || tenant.Demo == true || hasRules)
            return Done;
        if (!signedIn)
            return SignIn;
        // null = the workspace step was never answered; an empty list = "No Sentinel workspace".
        return tenant.Workspaces is null ? Workspaces : Collect;
    }
}
