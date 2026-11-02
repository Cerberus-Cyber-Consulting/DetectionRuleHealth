using System.Text.Json;

namespace RuleHealth.Core.Clients;

/// <summary>Whether Microsoft Sentinel is enabled on a workspace.</summary>
public static class SentinelStates
{
    public const string Enabled = "enabled";
    public const string NotEnabled = "notEnabled";
    /// <summary>The onboarding state could not be read (403: no Microsoft Sentinel Reader): shown, not hidden.</summary>
    public const string Unknown = "unknown";
}

/// <summary>One Log Analytics workspace the signed-in user can see, for the wizard's workspace step.</summary>
/// <param name="SubscriptionState">Enabled, Warned, PastDue, Disabled: anything but Enabled is shown as a warning.</param>
public sealed record DiscoveredWorkspace(
    string SubscriptionId, string SubscriptionName, string SubscriptionState, string ResourceGroup, string WorkspaceName,
    string WorkspaceId, string? Location, string Sentinel);

/// <summary>What discovery found, plus what it could not read (one line per subscription or workspace).</summary>
public sealed record DiscoveryResult(int Subscriptions, IReadOnlyList<DiscoveredWorkspace> Workspaces, IReadOnlyList<string> Warnings);

/// <summary>
/// Lists the subscriptions → Log Analytics workspaces → Sentinel onboarding state the signed-in user
/// can see (skill ms-security-apis, "Discover workspaces"; calls checked with ms-api-checker 2026-10-28).
/// Read-only ARM calls, at most four subscriptions at a time.
/// </summary>
public sealed class DiscoveryClient
{
    public const string SubscriptionsApiVersion = "2022-12-01";
    private const int Concurrency = 4;

    private static readonly ApiCall Subscriptions = new("List subscriptions", "Azure Service Management user_impersonation (delegated)");
    private static readonly ApiCall Workspaces = new("List Log Analytics workspaces", "Reader or Log Analytics Reader on the subscription or resource group");
    private static readonly ApiCall Onboarding = new("Read Microsoft Sentinel onboarding state", "Microsoft Sentinel Reader on the workspace");

    private readonly ApiConnection _api;

    public DiscoveryClient(ApiConnection api) => _api = api;

    public async Task<DiscoveryResult> DiscoverAsync(string tenantId, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var subscriptions = new List<(string Id, string Name, string State)>();
        foreach (var item in await _api.GetAllPagesAsync(_api.Arm, $"{ApiConnection.ArmBase}/subscriptions?api-version={SubscriptionsApiVersion}",
                     "nextLink", Subscriptions, cancellationToken))
        {
            var id = ApiConnection.Text(item, "subscriptionId");
            var state = ApiConnection.Text(item, "state") ?? "Unknown";
            if (id is null || string.Equals(state, "Deleted", StringComparison.OrdinalIgnoreCase))
                continue; // no operations work on a deleted subscription
            // Azure Lighthouse lists customer subscriptions delegated to this tenant too; their data is not
            // in this tenant's incidents or Defender XDR, so they do not belong to this tenant folder.
            if (ApiConnection.Text(item, "tenantId") is { } owner && !string.Equals(owner, tenantId, StringComparison.OrdinalIgnoreCase))
                continue;
            subscriptions.Add((id, ApiConnection.Text(item, "displayName") ?? id, state));
        }

        var found = new List<DiscoveredWorkspace>();
        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = subscriptions.Select(async subscription =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var workspaces = await ListWorkspacesAsync(subscription, warnings, cancellationToken);
                lock (found)
                    found.AddRange(workspaces);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        var ordered = found
            .OrderBy(w => w.Sentinel == SentinelStates.Enabled ? 0 : w.Sentinel == SentinelStates.Unknown ? 1 : 2)
            .ThenBy(w => w.SubscriptionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> sortedWarnings;
        lock (warnings)
            sortedWarnings = warnings.Order(StringComparer.Ordinal).ToList();
        return new DiscoveryResult(subscriptions.Count, ordered, sortedWarnings);
    }

    private async Task<List<DiscoveredWorkspace>> ListWorkspacesAsync((string Id, string Name, string State) subscription,
        List<string> warnings, CancellationToken cancellationToken)
    {
        List<JsonElement> items;
        try
        {
            items = await _api.GetAllPagesAsync(_api.Arm,
                $"{ApiConnection.ArmBase}/subscriptions/{subscription.Id}/providers/Microsoft.OperationalInsights/workspaces?api-version={WorkspaceClient.ApiVersion}",
                "nextLink", Workspaces with { Label = $"List workspaces in {subscription.Name}" }, cancellationToken);
        }
        catch (RuleHealthException ex) when (ex.HttpStatus is 400 or 403 or 404 or 409)
        {
            // AuthorizationFailed, MissingSubscriptionRegistration, NoRegisteredProviderFound: nothing
            // visible here, not a reason to stop discovery.
            Warn(warnings, $"{subscription.Name}: workspaces could not be listed (HTTP {ex.HttpStatus}).");
            return new List<DiscoveredWorkspace>();
        }

        var result = new List<DiscoveredWorkspace>();
        foreach (var item in items)
        {
            var provisioning = ApiConnection.PathText(item, "properties", "provisioningState");
            if (provisioning is "Deleting" or "Failed")
                continue;
            var customerId = ApiConnection.PathText(item, "properties", "customerId");
            if (ApiConnection.Text(item, "id") is not { } armId || customerId is null)
                continue;
            WorkspaceResourceId id;
            try
            {
                id = WorkspaceClient.ParseResourceId(armId);
            }
            catch (RuleHealthException)
            {
                continue;
            }
            // ARM returns the id in lower case; the name keeps the portal's casing.
            var name = ApiConnection.Text(item, "name") ?? id.WorkspaceName;
            id = id with { WorkspaceName = name };
            result.Add(new DiscoveredWorkspace(subscription.Id, subscription.Name, subscription.State, id.ResourceGroup, name, customerId,
                ApiConnection.Text(item, "location"), await SentinelStateAsync(id, warnings, cancellationToken)));
        }
        return result;
    }

    private async Task<string> SentinelStateAsync(WorkspaceResourceId id, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await _api.GetJsonAsync(_api.Arm,
                $"{ApiConnection.ArmBase}{id.Path}/providers/Microsoft.SecurityInsights/onboardingStates?api-version={SentinelRulesClient.ApiVersion}",
                Onboarding with { Label = $"Read Sentinel state of {id.WorkspaceName}" }, cancellationToken);
            // The documented state is named "default". What a workspace without Sentinel returns
            // (empty value or 404) is VERIFY; both read as not enabled.
            var enabled = json.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array &&
                          value.EnumerateArray().Any(s => string.Equals(ApiConnection.Text(s, "name"), "default", StringComparison.OrdinalIgnoreCase));
            return enabled ? SentinelStates.Enabled : SentinelStates.NotEnabled;
        }
        catch (RuleHealthException ex) when (ex.HttpStatus is 400 or 404 or 409)
        {
            return SentinelStates.NotEnabled; // 404 / MissingSubscriptionRegistration: no SecurityInsights here
        }
        catch (RuleHealthException ex) when (ex.HttpStatus == 403)
        {
            Warn(warnings, $"{id.WorkspaceName}: Microsoft Sentinel state not readable (needs Microsoft Sentinel Reader).");
            return SentinelStates.Unknown;
        }
    }

    private static void Warn(List<string> warnings, string text)
    {
        lock (warnings)
            warnings.Add(text);
    }
}
