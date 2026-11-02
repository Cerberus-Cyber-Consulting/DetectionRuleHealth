<#
.SYNOPSIS
    Pins every `uses: owner/action@tag` in .github/workflows to the tag's commit SHA.

.DESCRIPTION
    Rewrites   uses: actions/checkout@v5
    into       uses: actions/checkout@<40-char sha> # v5
    using the GitHub CLI (`gh api`). Already pinned lines keep their SHA unless -Refresh is given,
    in which case the tag in the trailing comment is resolved again. Dependabot keeps the pins
    current afterwards.

.EXAMPLE
    pwsh ./tools/Update-ActionPins.ps1 -WhatIf
    pwsh ./tools/Update-ActionPins.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $WorkflowPath = (Join-Path $PSScriptRoot '..' '.github' 'workflows'),
    [switch] $Refresh
)

$ErrorActionPreference = 'Stop'
# With the GitHub CLI the calls are authenticated; without it the public REST API is used anonymously
# (60 requests per hour per IP, plenty for a handful of actions; tags of public repos need no sign-in).
$useGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)
if (-not $useGh) { Write-Host 'GitHub CLI (gh) not found: using the public GitHub API without sign-in.' }

$cache = @{}
function Resolve-Sha([string] $action, [string] $ref) {
    $repo = ($action -split '/')[0..1] -join '/'        # owner/repo, drops sub-paths like github/codeql-action/init
    $key = "$repo@$ref"
    if (-not $cache.ContainsKey($key)) {
        # commits/{ref} resolves a tag (also an annotated one) to the commit it points at.
        if ($useGh) {
            $sha = gh api "repos/$repo/commits/$ref" --jq .sha 2>$null
            if ($LASTEXITCODE -ne 0) { $sha = $null }
        }
        else {
            $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'DetectionRuleHealth-tools' }
            try { $sha = (Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/commits/$ref" -Headers $headers).sha }
            catch { $sha = $null }
        }
        if ($sha -notmatch '^[0-9a-f]{40}$') { throw "Cannot resolve $repo@$ref" }
        $cache[$key] = $sha
    }
    $cache[$key]
}

$pattern = '^(?<lead>\s*-?\s*uses:\s*)(?<action>[\w.-]+/[\w./-]+)@(?<ref>[^\s#]+)(?:\s*#\s*(?<comment>\S+))?\s*$'
foreach ($file in Get-ChildItem -LiteralPath $WorkflowPath -Filter *.yml) {
    $lines = @(Get-Content -LiteralPath $file.FullName)
    $changed = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], $pattern)
        if (-not $m.Success) { continue }
        $action = $m.Groups['action'].Value; $ref = $m.Groups['ref'].Value
        $isPinned = $ref -match '^[0-9a-f]{40}$'
        if ($isPinned -and -not $Refresh) { continue }
        $tag = if ($isPinned) { $m.Groups['comment'].Value } else { $ref }
        if (-not $tag) { Write-Warning "$($file.Name):$($i + 1) pinned without a version comment; skipped"; continue }
        $sha = Resolve-Sha $action $tag
        $new = "$($m.Groups['lead'].Value)$action@$sha # $tag"
        if ($new -ne $lines[$i]) {
            Write-Host "$($file.Name):$($i + 1)  $action@$tag -> $sha"
            $lines[$i] = $new; $changed = $true
        }
    }
    if ($changed -and $PSCmdlet.ShouldProcess($file.FullName, 'write pinned actions')) {
        # LF on every OS (Set-Content would write CRLF on Windows).
        [IO.File]::WriteAllText($file.FullName, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    }
}
