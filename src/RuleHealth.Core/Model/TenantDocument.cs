using System.Text.Json.Serialization;

namespace RuleHealth.Core.Model;

/// <summary>tenant.json — see schemas/tenant.schema.json.</summary>
public sealed record TenantDocument
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public required string Alias { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkspaceRef>? Workspaces { get; init; }

    public required AuthSettings Auth { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TenantDefaults? Defaults { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Demo { get; init; }
}

public sealed record WorkspaceRef
{
    public required string SubscriptionId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string WorkspaceName { get; init; }
    /// <summary>Log Analytics workspace (customer) ID, a GUID.</summary>
    public required string WorkspaceId { get; init; }
}

public sealed record AuthSettings
{
    /// <summary><see cref="AuthModes"/>: pkce, microsoft-apps or offline (older files: browser, cli, cli-graph).</summary>
    public required string Mode { get; init; }

    /// <summary>Application (client) id of the user's own app registration (pkce only). Not a secret.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; init; }
}

public static class AuthModes
{
    /// <summary>Browser sign-in (authorization code + PKCE) with the user's own app registration (recommended).</summary>
    public const string Pkce = "pkce";

    /// <summary>
    /// Quick start without an app registration: the same browser sign-in through Microsoft's public
    /// apps (Graph Command Line Tools for Graph, Azure PowerShell for ARM and Log Analytics). No clientId.
    /// </summary>
    public const string MicrosoftApps = "microsoft-apps";

    /// <summary>Demo or markdown data: nothing to sign in to.</summary>
    public const string Offline = "offline";

    // Pre-M8 values, only read (TenantAuth): browser + clientId still signs in, as pkce; the Azure CLI
    // modes are gone and the tenant has to be added again.
    public const string Browser = "browser";
    public const string Cli = "cli";
    public const string CliGraph = "cli-graph";
}

public sealed record TenantDefaults
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IncidentDays { get; init; }

    /// <summary>Advanced hunting caps lookback at 30 days.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ValidationDays { get; init; }
}
