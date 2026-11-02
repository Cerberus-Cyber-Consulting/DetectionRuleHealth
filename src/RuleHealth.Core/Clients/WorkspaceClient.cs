using System.Text.RegularExpressions;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Clients;

/// <summary>Parts of a Log Analytics workspace ARM resource id.</summary>
public sealed record WorkspaceResourceId(string SubscriptionId, string ResourceGroup, string WorkspaceName)
{
    /// <summary>The canonical id, as ARM URLs need it.</summary>
    public string Path =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.OperationalInsights/workspaces/{WorkspaceName}";

    public static WorkspaceResourceId From(WorkspaceRef workspace) =>
        new(workspace.SubscriptionId, workspace.ResourceGroup, workspace.WorkspaceName);
}

/// <summary>Reads the workspace resource from ARM, to resolve its customerId (the Log Analytics workspace GUID).</summary>
public sealed partial class WorkspaceClient
{
    public const string ApiVersion = "2026-03-01";

    private static readonly ApiCall Call = new(
        "Read Log Analytics workspace",
        "Reader (or Log Analytics Reader / Microsoft Sentinel Reader) on the workspace");

    private readonly ApiConnection _api;

    public WorkspaceClient(ApiConnection api) => _api = api;

    /// <summary>
    /// Accepts the resource id as copied from the portal's JSON view (any casing, optional trailing slash),
    /// or any portal URL that contains it: the workspace page
    /// (https://portal.azure.com/#@contoso.onmicrosoft.com/resource/subscriptions/…/workspaces/law/overview)
    /// or a Sentinel blade, where the id is URL-encoded (…/id/%2Fsubscriptions%2F…).
    /// </summary>
    public static WorkspaceResourceId ParseResourceId(string resourceId)
    {
        var text = resourceId.Trim();
        var match = ResourceIdPattern().Match(text);
        if (!match.Success)
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(text); } catch (UriFormatException) { decoded = text; }
            match = ResourceIdInText().Match(decoded);
        }
        if (!match.Success)
            throw new RuleHealthException(
                $"'{resourceId}' is not a Log Analytics workspace resource id. Expected " +
                "/subscriptions/<guid>/resourceGroups/<rg>/providers/Microsoft.OperationalInsights/workspaces/<name> " +
                "(portal: workspace > Overview > JSON View), or the workspace's portal URL.");
        return new WorkspaceResourceId(match.Groups["sub"].Value, match.Groups["rg"].Value, match.Groups["ws"].Value);
    }

    public async Task<WorkspaceRef> ResolveAsync(WorkspaceResourceId id, CancellationToken cancellationToken)
    {
        var url = $"{ApiConnection.ArmBase}{id.Path}?api-version={ApiVersion}";
        using var json = await _api.GetJsonAsync(_api.Arm, url, Call with { Label = $"Read workspace {id.WorkspaceName}" }, cancellationToken);
        var root = json.RootElement;
        var customerId = ApiConnection.PathText(root, "properties", "customerId")
            ?? throw new RuleHealthException($"Workspace {id.WorkspaceName} has no properties.customerId in the ARM response.");

        return new WorkspaceRef
        {
            SubscriptionId = id.SubscriptionId,
            ResourceGroup = id.ResourceGroup,
            // ARM returns the canonical casing of the name; keep it so later URLs and labels match the portal.
            WorkspaceName = ApiConnection.Text(root, "name") ?? id.WorkspaceName,
            WorkspaceId = customerId,
        };
    }

    [GeneratedRegex(
        @"^/?subscriptions/(?<sub>[0-9a-fA-F-]{36})/resourceGroups/(?<rg>[^/]+)/providers/Microsoft\.OperationalInsights/workspaces/(?<ws>[^/]+)/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResourceIdPattern();

    // The same id anywhere inside a (decoded) URL; the name stops at the next path, query or fragment.
    [GeneratedRegex(
        @"/subscriptions/(?<sub>[0-9a-fA-F-]{36})/resourceGroups/(?<rg>[^/?#]+)/providers/Microsoft\.OperationalInsights/workspaces/(?<ws>[^/?#&\s]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResourceIdInText();
}
