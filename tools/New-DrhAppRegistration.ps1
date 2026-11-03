<#
.SYNOPSIS
    Creates the app registration Detection Rule Health signs in with, in your own tenant.

.DESCRIPTION
    Uses the Azure CLI only as an authenticated HTTP client (`az rest` against Microsoft Graph).
    It creates:
      - a single-tenant app registration (no secret, no certificate),
        platform "Mobile and desktop applications",
        redirect URI http://localhost:5180/auth/callback
      - its service principal (enterprise application)
      - the delegated permissions Detection Rule Health needs (read-only):
          Microsoft Graph            CustomDetection.Read.All, ThreatHunting.Read.All,
                                     SecurityIncident.Read.All, SecurityAlert.Read.All
          Azure Service Management   user_impersonation
          Log Analytics API          Data.Read
    Permission ids are looked up by name from the service principals in your tenant, so nothing is
    hard-coded except the well-known application ids of the three Microsoft APIs.

    Admin consent is still needed afterwards. The script prints the consent link; with
    -GrantAdminConsent it grants tenant-wide consent itself (requires Global Administrator or
    Privileged Role Administrator).

    Requirements: PowerShell 7+, Azure CLI 2.60+, `az login --tenant <your tenant>` with an account
    that may create app registrations (Application Developer or higher, or "Users can register
    applications" enabled).

.PARAMETER DisplayName
    Name of the app registration. Default: "Detection Rule Health".

.PARAMETER TenantId
    Optional safety check: stop if the Azure CLI is signed in to another tenant.

.PARAMETER RestrictToAssignedUsers
    Require user assignment on the enterprise application and assign the signed-in user, so only
    assigned users can sign in to Detection Rule Health. Recommended.

.PARAMETER GrantAdminConsent
    Grant tenant-wide admin consent for the delegated permissions (creates oauth2PermissionGrants).

.PARAMETER Force
    Create the app even if one with the same display name exists.

.EXAMPLE
    az login --tenant contoso.onmicrosoft.com
    pwsh ./tools/New-DrhAppRegistration.ps1 -RestrictToAssignedUsers

.NOTES
    Part of Detection Rule Health (MIT). https://detectionhealth.cerberuscyberconsulting.com/setup/
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $DisplayName = 'Detection Rule Health',
    [string] $TenantId,
    [switch] $RestrictToAssignedUsers,
    [switch] $GrantAdminConsent,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RedirectUri = 'http://localhost:5180/auth/callback'
$Graph = 'https://graph.microsoft.com/v1.0'

# Well-known application ids of the Microsoft APIs (identical in every tenant).
$Apis = @(
    [pscustomobject]@{ Name = 'Microsoft Graph';          AppId = '00000003-0000-0000-c000-000000000000'
                       Scopes = 'CustomDetection.Read.All', 'ThreatHunting.Read.All', 'SecurityIncident.Read.All', 'SecurityAlert.Read.All' }
    [pscustomobject]@{ Name = 'Azure Service Management'; AppId = '797f4846-ba00-4fd7-ba43-dac1f8f63013'
                       Scopes = @('user_impersonation') }
    [pscustomobject]@{ Name = 'Log Analytics API';        AppId = 'ca7f3f0b-7d91-482c-8e09-c5d840d0eac5'
                       Scopes = @('Data.Read') }
)

function Invoke-Graph {
    param(
        [ValidateSet('GET', 'POST', 'PATCH')] [string] $Method = 'GET',
        [Parameter(Mandatory)] [string] $Uri,
        [object] $Body
    )
    $azArgs = @('rest', '--method', $Method, '--url', $Uri, '--only-show-errors')
    $tmp = $null
    if ($null -ne $Body) {
        # Pass the body through a file: avoids JSON quoting problems between PowerShell and az on Windows.
        $tmp = New-TemporaryFile
        $Body | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tmp -Encoding utf8NoBOM
        $azArgs += @('--body', "@$($tmp.FullName)", '--headers', 'Content-Type=application/json')
    }
    try {
        $out = & az @azArgs 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Graph $Method $Uri failed: $($out -join ' ')" }
        $text = ($out | Where-Object { $_ -is [string] }) -join "`n"
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return $text | ConvertFrom-Json -Depth 20
    }
    finally {
        if ($tmp) { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
    }
}

function Get-ResourceServicePrincipal([pscustomobject] $Api) {
    $sp = Invoke-Graph -Uri "$Graph/servicePrincipals(appId='$($Api.AppId)')?`$select=id,appId,displayName,oauth2PermissionScopes"
    if (-not $sp) { throw "Service principal for $($Api.Name) ($($Api.AppId)) not found in this tenant." }
    return $sp
}

# ------------------------------------------------------------------ preflight
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI (az) not found. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli, or follow the portal steps in the setup guide.'
}
$account = az account show --only-show-errors 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not signed in. Run: az login --tenant <your tenant id or domain>' }
$tid = $account.tenantId
if ($TenantId -and $TenantId -ne $tid) {
    throw "Azure CLI is signed in to tenant $tid, not $TenantId. Run: az login --tenant $TenantId"
}
Write-Host "Tenant:  $tid"
Write-Host "Account: $($account.user.name)"

$existing = Invoke-Graph -Uri "$Graph/applications?`$filter=displayName eq '$($DisplayName.Replace("'", "''"))'&`$select=id,appId,displayName"
if ($existing.value.Count -gt 0 -and -not $Force) {
    $ids = ($existing.value | ForEach-Object { $_.appId }) -join ', '
    Write-Warning "An app registration named '$DisplayName' already exists (client id: $ids). Use it, or run again with -Force to create another."
    return
}

# ------------------------------------------------------------------ permissions by name
$resourceAccess = @()
$grants = @()
foreach ($api in $Apis) {
    $sp = Get-ResourceServicePrincipal $api
    $access = foreach ($scopeName in $api.Scopes) {
        $scope = $sp.oauth2PermissionScopes | Where-Object { $_.value -eq $scopeName }
        if (-not $scope) { throw "Delegated permission '$scopeName' not found on $($api.Name)." }
        @{ id = $scope.id; type = 'Scope' }
    }
    $resourceAccess += @{ resourceAppId = $api.AppId; resourceAccess = @($access) }
    $grants += [pscustomobject]@{ Api = $api.Name; ResourceSpId = $sp.id; Scope = ($api.Scopes -join ' ') }
}

# ------------------------------------------------------------------ create
$appBody = @{
    displayName            = $DisplayName
    description            = 'Detection Rule Health: local overview of Microsoft Sentinel analytics rules and Defender XDR custom detections. Delegated, read-only.'
    signInAudience         = 'AzureADMyOrg'
    isFallbackPublicClient = $false
    publicClient           = @{ redirectUris = @($RedirectUri) }
    requiredResourceAccess = $resourceAccess
    notes                  = 'Created by tools/New-DrhAppRegistration.ps1. No secrets. Redirect URI must stay http://localhost:5180/auth/callback under Mobile and desktop applications.'
}

if (-not $PSCmdlet.ShouldProcess("tenant $tid", "Create app registration '$DisplayName'")) { return }

$app = Invoke-Graph -Method POST -Uri "$Graph/applications" -Body $appBody
Write-Host "Created app registration $($app.appId)"

# The service principal can take a few seconds to become visible after the application is created.
$sp = $null
for ($i = 1; $i -le 5 -and -not $sp; $i++) {
    try { $sp = Invoke-Graph -Method POST -Uri "$Graph/servicePrincipals" -Body @{ appId = $app.appId } }
    catch { if ($i -eq 5) { throw }; Start-Sleep -Seconds (2 * $i) }
}
Write-Host "Created enterprise application $($sp.id)"

if ($RestrictToAssignedUsers) {
    Invoke-Graph -Method PATCH -Uri "$Graph/servicePrincipals/$($sp.id)" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
    $me = Invoke-Graph -Uri "$Graph/me?`$select=id,userPrincipalName"
    Invoke-Graph -Method POST -Uri "$Graph/servicePrincipals/$($sp.id)/appRoleAssignedTo" -Body @{
        principalId = $me.id; resourceId = $sp.id; appRoleId = '00000000-0000-0000-0000-000000000000'
    } | Out-Null
    Write-Host "User assignment required; assigned $($me.userPrincipalName). Assign other users or groups under Enterprise applications."
}

$consentUrl = "https://login.microsoftonline.com/$tid/adminconsent?client_id=$($app.appId)"
if ($GrantAdminConsent) {
    try {
        foreach ($g in $grants) {
            Invoke-Graph -Method POST -Uri "$Graph/oauth2PermissionGrants" -Body @{
                clientId = $sp.id; consentType = 'AllPrincipals'; resourceId = $g.ResourceSpId; scope = $g.Scope
            } | Out-Null
            Write-Host "Admin consent granted: $($g.Api) ($($g.Scope))"
        }
    }
    catch {
        Write-Warning "Could not grant admin consent from the CLI: $($_.Exception.Message)"
        Write-Warning "Ask an administrator to open the consent link below, or use 'Grant admin consent' in the portal."
        $GrantAdminConsent = $false
    }
}

# ------------------------------------------------------------------ result
Write-Host ''
Write-Host 'Enter these in the Detection Rule Health setup wizard (http://localhost:5180):' -ForegroundColor Cyan
[pscustomobject]@{
    TenantId    = $tid
    ClientId    = $app.appId
    RedirectUri = $RedirectUri
} | Format-List

if (-not $GrantAdminConsent) {
    Write-Host 'Next: admin consent. An administrator opens this link (or uses "Grant admin consent" under API permissions):' -ForegroundColor Cyan
    Write-Host "  $consentUrl"
    Write-Host '  After consenting, the browser returns to http://localhost:5180/auth/callback. If the app is not running yet, that page will not load; consent is still saved.'
}
