using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Operations;

/// <summary>Body of POST /api/tenants: connect (or re-connect) a tenant.</summary>
/// <param name="ClientId">Own app registration only: the Application (client) id of the user's single-tenant public client.</param>
/// <param name="SignIn"><see cref="SignInOptions.OwnApp"/> (default, recommended) or <see cref="SignInOptions.MicrosoftApps"/> (quick start).</param>
public sealed record TenantRequest(string Alias, string TenantId, string? ClientId, string? DisplayName = null, string? SignIn = null);

/// <summary>The two sign-in options of the wizard, as the API names them (tenant.json: auth.mode pkce / microsoft-apps).</summary>
public static class SignInOptions
{
    public const string OwnApp = "own-app";
    public const string MicrosoftApps = "microsoft-apps";
}

/// <summary>Body of PUT /api/tenants/{alias}/settings (the Settings page and the wizard's collect step).</summary>
public sealed record SettingsRequest(string? DisplayName, TenantDefaults? Defaults);

/// <summary>Body of PUT /api/tenants/{alias}/workspaces.</summary>
/// <param name="Workspaces">ARM resource ids of the Sentinel workspaces, or portal URLs that contain one. Empty = Defender only.</param>
public sealed record WorkspacesRequest(IReadOnlyList<string> Workspaces);

/// <summary>
/// Adding a tenant, in two parts around the browser sign-in:
/// 1. <see cref="Prepare"/> + <see cref="SaveAsync"/>: check the input, write tenant.json (no sign-in yet);
/// 2. after sign-in, <see cref="SetWorkspacesAsync"/>: resolve each workspace through ARM and store it.
/// </summary>
public static class TenantOperation
{
    /// <summary>Every check that needs no network, so a typo is refused before the user is sent to sign in.</summary>
    public static TenantDocument Prepare(JsonStore store, TenantRequest request)
    {
        static RuleHealthException Bad(string message) => new(message) { HttpStatus = 400 };

        var alias = request.Alias?.Trim() ?? "";
        if (!Documents.IsValidAlias(alias))
            throw Bad($"The alias '{alias}' is not valid: use lowercase letters, digits and dashes (it becomes the folder name).");

        var tenantId = request.TenantId?.Trim() ?? "";
        if (!Guid.TryParse(tenantId, out _))
            throw Bad($"The tenant ID must be the tenant's GUID (Entra ID → Overview → Tenant ID), got '{tenantId}'.");

        var signIn = request.SignIn ?? SignInOptions.OwnApp;
        if (signIn is not (SignInOptions.OwnApp or SignInOptions.MicrosoftApps))
            throw Bad($"signIn must be '{SignInOptions.OwnApp}' or '{SignInOptions.MicrosoftApps}', got '{signIn}'.");

        var clientId = request.ClientId?.Trim() ?? "";
        if (signIn == SignInOptions.OwnApp && !Guid.TryParse(clientId, out _))
            throw Bad("The client ID must be the Application (client) ID of your own app registration (a GUID, " +
                      $"app registration → Overview), got '{clientId}'. The setup guide explains how to create it.");

        var existing = store.TryRead<TenantDocument>(alias, Documents.Tenant);
        if (existing?.Auth.Mode == AuthModes.Offline)
            throw new RuleHealthException($"The alias '{alias}' is an offline (markdown) tenant. Pick another alias.") { HttpStatus = 409 };
        if (existing?.TenantId is { } previous && !string.Equals(previous, tenantId, StringComparison.OrdinalIgnoreCase))
            throw new RuleHealthException(
                $"The alias '{alias}' already belongs to tenant {previous}. Pick another alias.") { HttpStatus = 409 };

        return new TenantDocument
        {
            Alias = alias,
            TenantId = tenantId.ToLowerInvariant(),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? existing?.DisplayName ?? alias : request.DisplayName.Trim(),
            // Re-adding the same tenant (new client id, other sign-in option, pre-M8 mode) keeps its workspaces and windows.
            Workspaces = existing?.Workspaces,
            Auth = signIn == SignInOptions.MicrosoftApps
                ? new AuthSettings { Mode = AuthModes.MicrosoftApps }
                : new AuthSettings { Mode = AuthModes.Pkce, ClientId = clientId.ToLowerInvariant() },
            Defaults = existing?.Defaults ?? new TenantDefaults { IncidentDays = 90, ValidationDays = 30 },
        };
    }

    /// <summary>Display name and windows; HttpStatus 400 for values out of range. Writes tenant.json.</summary>
    public static TenantDocument ApplySettings(JsonStore store, TenantDocument tenant, SettingsRequest request)
    {
        static RuleHealthException Bad(string message) => new(message) { HttpStatus = 400 };

        var name = request.DisplayName?.Trim();
        if (name is { Length: > 100 })
            throw Bad("The display name can be at most 100 characters.");
        var incident = request.Defaults?.IncidentDays ?? tenant.Defaults?.IncidentDays ?? 90;
        var validation = request.Defaults?.ValidationDays ?? tenant.Defaults?.ValidationDays ?? 30;
        if (incident is < 1 or > 365)
            throw Bad("The incident window must be 1 to 365 days.");
        // Advanced hunting keeps 30 days of Defender XDR data, so every engine validates over at most 30.
        if (validation is < 1 or > 30)
            throw Bad("The validation window must be 1 to 30 days.");

        var updated = tenant with
        {
            DisplayName = string.IsNullOrEmpty(name) ? tenant.DisplayName : name,
            Defaults = new TenantDefaults { IncidentDays = incident, ValidationDays = validation },
        };
        store.Write(tenant.Alias, Documents.Tenant, updated);
        return updated;
    }

    /// <summary>Writes tenant.json after the anonymous check that the tenant exists in Entra ID.</summary>
    /// <param name="discoveryHttp">For tests: the client for the anonymous tenant check (default: a shared one).</param>
    public static async Task SaveAsync(JsonStore store, TenantDocument tenant, CancellationToken cancellationToken, HttpClient? discoveryHttp = null)
    {
        // A wrong tenant ID (often a user's Object ID) fails here in one sentence, before the sign-in.
        await TenantDiscovery.EnsureExistsAsync(tenant.TenantId!, cancellationToken, discoveryHttp);
        store.Write(tenant.Alias, Documents.Tenant, tenant);
    }

    /// <summary>Resolves each workspace's customerId through ARM (needs the sign-in) and stores the list in tenant.json.</summary>
    public static async Task<TenantDocument> SetWorkspacesAsync(JsonStore store, ApiConnection api, TenantDocument tenant,
        IReadOnlyList<string> workspaces, Action<string> log, CancellationToken cancellationToken)
    {
        // Parse every entry before calling ARM, so a typo fails fast and nothing is half-written.
        var ids = new List<WorkspaceResourceId>();
        foreach (var text in workspaces.Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            try
            {
                ids.Add(WorkspaceClient.ParseResourceId(text));
            }
            catch (RuleHealthException ex)
            {
                throw new RuleHealthException(ex.Message) { HttpStatus = 400 };
            }
        }

        var client = new WorkspaceClient(api);
        var resolved = new List<WorkspaceRef>();
        foreach (var id in ids)
        {
            var workspace = await client.ResolveAsync(id, cancellationToken);
            log($"workspace {workspace.WorkspaceName}: workspaceId {workspace.WorkspaceId}");
            resolved.Add(workspace);
        }
        if (resolved.Count == 0)
            log("no workspace: only Defender custom detections will be collected.");

        var updated = tenant with { Workspaces = resolved };
        store.Write(tenant.Alias, Documents.Tenant, updated);
        return updated;
    }
}
