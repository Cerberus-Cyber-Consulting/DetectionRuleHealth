<#
.SYNOPSIS
    Regenerates docs/privacy.md, docs/security.md and docs/license.md from the root documents.

.DESCRIPTION
    PRIVACY.md, SECURITY.md and LICENSE are the source of truth. The docs pages are the same text
    with Jekyll front matter (the layout renders the title, so the first "# " heading is dropped).
    Test-ReleaseReadiness.ps1 fails when they drift. Run this after editing a root document.
#>
[CmdletBinding()]
param([string] $Root = (Join-Path $PSScriptRoot '..'))

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path

function Get-DocsBody([string] $sourcePath) {
    $lines = @(Get-Content -LiteralPath $sourcePath)
    $h1 = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match '^# ' })
    if ($h1 -ge 0) { $lines = @($lines | Select-Object -Skip ($h1 + 1)) }
    while ($lines.Count -and [string]::IsNullOrWhiteSpace($lines[0])) { $lines = @($lines | Select-Object -Skip 1) }
    $lines
}

$pages = @(
    @{ Source = 'PRIVACY.md';  Target = 'docs/privacy.md';  Title = 'Privacy statement'; Eyebrow = 'Project'
       Description = 'How Detection Rule Health handles data: everything stays on your machine, tokens in memory only, no telemetry.' }
    @{ Source = 'SECURITY.md'; Target = 'docs/security.md'; Title = 'Security policy';   Eyebrow = 'Project'
       Description = 'Supported versions, how to report a vulnerability, and how Detection Rule Health protects you.' }
)
foreach ($p in $pages) {
    $front = @('---', "title: $($p.Title)", "eyebrow: $($p.Eyebrow)", "description: '$($p.Description.Replace("'", "''"))'",
               "# Generated from $($p.Source) by tools/Sync-DocsPages.ps1. Edit $($p.Source), not this file.", '---', '')
    Set-Content -LiteralPath (Join-Path $Root $p.Target) -Value ($front + (Get-DocsBody (Join-Path $Root $p.Source))) -Encoding utf8NoBOM
    Write-Host "Wrote $($p.Target)"
}

$license = Get-Content -Raw -LiteralPath (Join-Path $Root 'LICENSE')
$licensePage = @'
---
title: License
eyebrow: Project
description: Detection Rule Health is free software under the MIT license.
# Generated from LICENSE by tools/Sync-DocsPages.ps1. Edit LICENSE, not this file.
---

Detection Rule Health is free and open source under the MIT license. You may use, copy, modify and
distribute it, also commercially, as long as the copyright notice and license text are included.
It is provided **as is, without warranty of any kind**.

Third-party components keep their own licenses; see
[THIRD-PARTY-NOTICES.md](https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/blob/main/THIRD-PARTY-NOTICES.md).
The Cerberus Cyber Consulting name and logos are not covered by the MIT license.

```text
'@ + $license.TrimEnd() + "`n" + '```' + "`n"
Set-Content -LiteralPath (Join-Path $Root 'docs/license.md') -Value $licensePage -Encoding utf8NoBOM -NoNewline
Write-Host 'Wrote docs/license.md'
