<#
.SYNOPSIS
    Release-readiness checks for Detection Rule Health. Exit code 1 on any failure.

.DESCRIPTION
    Fails on:
      - missing public files (LICENSE, PRIVACY.md, SECURITY.md, CHANGELOG.md, THIRD-PARTY-NOTICES.md,
        README.md, Dockerfile, compose.yaml, licenses/, docs site pages)
      - LICENSE that is not MIT for Cerberus Cyber Consulting BV
      - forbidden third-party content: FalconForce / KQLAnalyzer / environments.json, XDR Reference /
        xdrinternals.com (anywhere except .claude/, CLAUDE.md, .gitignore and this script)
      - key material, JWTs or client secrets in the repo
      - telemetry or analytics code in src/, ui/ or docs/
      - external scripts, stylesheets, fonts or images in ui/ or the docs site
      - docs site brand rules: raw colors outside docs/assets/css/cerberus-tokens.css, font-family
        without var(--font-*), emoji
      - Dockerfile: final stage must run as uid 1654 and contain no RUN
      - README, docs and compose.yaml must publish the port as 127.0.0.1:5180:5180
      - non-placeholder GUIDs in README.md, PRIVACY.md and docs/
      - with -Tag: CHANGELOG section "## [x.y.z] - YYYY-MM-DD" for that version, and every
        GitHub Action pinned to a commit SHA
    Warns on: unpinned actions (without -Tag), GUIDs in tests/Fixtures.

.EXAMPLE
    pwsh ./tools/Test-ReleaseReadiness.ps1
    pwsh ./tools/Test-ReleaseReadiness.ps1 -Tag v0.1.0-beta.1
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..'),
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = (Resolve-Path $Root).Path
$failures = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[object]]::new()

function Rel([string] $p) { [System.IO.Path]::GetRelativePath($Root, $p) -replace '\\', '/' }
function Fail($check, $file, $line, $detail) { $failures.Add([pscustomobject]@{ Check = $check; File = $file; Line = $line; Detail = $detail }) }
function Warn($check, $file, $line, $detail) { $warnings.Add([pscustomobject]@{ Check = $check; File = $file; Line = $line; Detail = $detail }) }

$textExt = '.md', '.yml', '.yaml', '.json', '.cs', '.csproj', '.props', '.targets', '.sln', '.ps1', '.psm1',
           '.js', '.css', '.html', '.txt', '.xml', '.kql', '.sh', '.toml', '.svg', ''
$skipDirs = '/.git/', '/bin/', '/obj/', '/node_modules/', '/TestResults/', '/docs/_site/', '/docs/.jekyll-cache/', '/data/'

$allFiles = Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {
    $r = '/' + (Rel $_.FullName)
    -not ($skipDirs | Where-Object { $r.Contains($_) })
}
$textFiles = $allFiles | Where-Object { $textExt -contains $_.Extension.ToLowerInvariant() -and $_.Length -lt 5MB }

function Scan([System.IO.FileInfo[]] $files, [string] $pattern, [scriptblock] $onHit) {
    foreach ($f in $files) {
        $lines = @(Get-Content -LiteralPath $f.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $pattern) { & $onHit (Rel $f.FullName) ($i + 1) $Matches[0] $lines[$i] }
        }
    }
}
function Under([string] $prefix) { $textFiles | Where-Object { (Rel $_.FullName).StartsWith($prefix) } }

# ------------------------------------------------------------------ required files
$required = 'LICENSE', 'README.md', 'PRIVACY.md', 'SECURITY.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md',
            'Dockerfile', '.dockerignore', 'compose.yaml', '.github/workflows/ci.yml', '.github/workflows/release.yml',
            'docs/_config.yml', 'docs/_layouts/default.html', 'docs/index.md', 'docs/quick-start.md', 'docs/setup.md',
            'docs/first-run.md', 'docs/troubleshooting.md', 'docs/privacy.md', 'docs/security.md', 'docs/license.md',
            'docs/assets/css/cerberus-tokens.css', 'docs/assets/css/site.css',
            'tools/New-DrhAppRegistration.ps1'
foreach ($r in $required) { if (-not (Test-Path (Join-Path $Root $r))) { Fail 'required-file' $r $null 'missing' } }
if (-not (Get-ChildItem (Join-Path $Root 'licenses') -File -ErrorAction SilentlyContinue)) { Fail 'required-file' 'licenses/' $null 'third-party license texts missing' }

$lic = Join-Path $Root 'LICENSE'
if (Test-Path $lic) {
    $t = Get-Content -Raw $lic
    if ($t -notmatch '(?m)^MIT License' -or $t -notmatch 'Copyright \(c\) 20\d\d Cerberus Cyber Consulting BV') {
        Fail 'license' 'LICENSE' $null 'expected MIT License, Copyright (c) <year> Cerberus Cyber Consulting BV'
    }
}

# ------------------------------------------------------------------ forbidden third-party content
$forbidden = '(?i)falcon\s*force|kqlanalyzer|environments\.json|xdrinternals|xdr\s*reference'
$exempt = { param($rel) $rel.StartsWith('.claude/') -or $rel -eq 'CLAUDE.md' -or $rel -eq '.gitignore' -or $rel -eq 'tools/Test-ReleaseReadiness.ps1' }
Scan ($textFiles | Where-Object { -not (& $exempt (Rel $_.FullName)) }) $forbidden {
    param($file, $line, $hit) Fail 'third-party' $file $line "forbidden reference '$hit'"
}
$allFiles | Where-Object { $_.Name -ieq 'environments.json' } | ForEach-Object { Fail 'third-party' (Rel $_.FullName) $null 'third-party schema file' }
if (Test-Path (Join-Path $Root 'reference/spike')) { Fail 'third-party' 'reference/spike/' $null 'the spike depended on a third-party schema; remove it (see kql-literal-extraction skill)' }

# ------------------------------------------------------------------ secrets
$allFiles | Where-Object { $_.Extension -in '.pfx', '.p12', '.pem', '.key' -or $_.Name -eq '.env' } |
    ForEach-Object { Fail 'secret' (Rel $_.FullName) $null 'key or environment file' }
Scan ($textFiles | Where-Object { (Rel $_.FullName) -ne 'tools/Test-ReleaseReadiness.ps1' }) `
    'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?i)client_secret\s*[=:]\s*["'']?[A-Za-z0-9~._-]{20,}' {
    param($file, $line, $hit) Fail 'secret' $file $line 'token, private key or client secret'
}

# ------------------------------------------------------------------ telemetry / analytics
$telemetry = '(?i)googletagmanager|google-analytics|gtag\(|plausible\.io|clarity\.ms|segment\.(io|com)|applicationinsights|appinsights|sentry\.io|posthog|mixpanel|hotjar'
Scan (@(Under 'src/') + @(Under 'ui/') + @(Under 'docs/')) $telemetry {
    param($file, $line, $hit) Fail 'telemetry' $file $line "analytics/telemetry reference '$hit'"
}

# ------------------------------------------------------------------ external assets (ui + docs site)
$extAsset = '(?i)<script[^>]+src\s*=\s*["'']https?://|<link[^>]+href\s*=\s*["'']https?://|<img[^>]+src\s*=\s*["'']https?://|url\(\s*["'']?https?://|@import\s+(url\()?["'']?https?://|fonts\.googleapis|fonts\.gstatic|cdn\.jsdelivr|unpkg\.com|cdnjs'
$siteFiles = @(Under 'ui/' | Where-Object { (Rel $_.FullName) -notlike 'ui/vendor/*' }) +
             @(Under 'docs/' | Where-Object { $_.Extension -in '.html', '.css', '.js', '.md' })
Scan $siteFiles $extAsset { param($file, $line, $hit) Fail 'external-asset' $file $line $hit }

# ------------------------------------------------------------------ docs site brand rules
$docsStyle = @(Under 'docs/' | Where-Object { $_.Extension -in '.html', '.css', '.js' -and (Rel $_.FullName) -ne 'docs/assets/css/cerberus-tokens.css' })
Scan $docsStyle '(?<![\w&-])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})\b(?!\s*[,}]?\s*\{)|\b(?:rgba?|hsla?)\s*\(' {
    param($file, $line, $hit, $text) if ($text -notmatch '^\s*(/\*|\*|//|<!--)' -and $text -notmatch 'href="#') { Fail 'docs-brand' $file $line "raw color '$hit' (use a token)" }
}
Scan $docsStyle '(?i)font-family\s*:\s*(?!\s*var\(--font-(?:sans|mono)\))[^;"}]+' {
    param($file, $line, $hit) Fail 'docs-brand' $file $line "font-family without var(--font-*): $hit"
}
Scan (@(Under 'docs/') + @(Under 'ui/' | Where-Object { (Rel $_.FullName) -notlike 'ui/vendor/*' })) '[☀-➿]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDEFF]|\uD83E[\uDD00-\uDEFF]' {
    param($file, $line, $hit) Fail 'emoji' $file $line 'emoji (the brand uses none)'
}

# ------------------------------------------------------------------ Dockerfile
$df = Join-Path $Root 'Dockerfile'
if (Test-Path $df) {
    $lines = @(Get-Content $df)
    $lastFrom = ($lines | Select-String -Pattern '^\s*FROM\s' | Select-Object -Last 1).LineNumber
    $final = $lines[($lastFrom - 1)..($lines.Count - 1)]
    if (-not ($final -match '^\s*USER\s+1654\s*$')) { Fail 'docker' 'Dockerfile' $lastFrom 'final stage must set USER 1654' }
    if ($final -match '^\s*RUN\s') { Fail 'docker' 'Dockerfile' $lastFrom 'final stage (chiseled) must not contain RUN' }
    if ($lines[$lastFrom - 1] -notmatch 'chiseled') { Fail 'docker' 'Dockerfile' $lastFrom 'final stage should use a chiseled runtime-deps image' }
}

# ------------------------------------------------------------------ localhost-only port binding
foreach ($f in @('README.md', 'compose.yaml', 'docs/quick-start.md', 'docs/setup.md')) {
    $p = Join-Path $Root $f
    if (Test-Path $p) {
        $t = Get-Content -Raw $p
        if ($t -notmatch '127\.0\.0\.1:5180:5180') { Fail 'port-binding' $f $null 'must show the port published as 127.0.0.1:5180:5180' }
        foreach ($m in [regex]::Matches($t, '(?<![\d.:])-p\s+5180:5180|"5180:5180"|-\s+5180:5180')) { Fail 'port-binding' $f $null "port published on all interfaces: $($m.Value)" }
    }
}

# ------------------------------------------------------------------ GUIDs in public docs
$guid = '\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b'
$knownGuids = @(
    '00000000-0000-0000-0000-000000000000',   # placeholder / default app role
    '00000003-0000-0000-c000-000000000000',   # Microsoft Graph
    '797f4846-ba00-4fd7-ba43-dac1f8f63013',   # Azure Service Management
    'ca7f3f0b-7d91-482c-8e09-c5d840d0eac5'    # Log Analytics API
)
$isPlaceholder = { param($g) $knownGuids -contains $g.ToLowerInvariant() -or $g -match '^([0-9a-f])\1{7}-' }
Scan (@(Under 'docs/') + @($textFiles | Where-Object { (Rel $_.FullName) -in 'README.md', 'PRIVACY.md', 'SECURITY.md' })) $guid {
    param($file, $line, $hit) if (-not (& $isPlaceholder $hit)) { Fail 'guid' $file $line "GUID $hit (use a placeholder such as 11111111-1111-1111-1111-111111111111)" }
}
Scan (Under 'tests/Fixtures/') $guid {
    param($file, $line, $hit) if (-not (& $isPlaceholder $hit)) { Warn 'guid' $file $line "GUID $($hit): confirm it is scrubbed" }
}

# ------------------------------------------------------------------ actions pinned
$unpinned = [System.Collections.Generic.List[string]]::new()
Scan (Under '.github/workflows/') '^\s*-?\s*uses:\s*([^\s#]+)' {
    param($file, $line, $hit, $text)
    $ref = ($text -replace '^\s*-?\s*uses:\s*', '' -replace '\s*#.*$', '').Trim()
    if ($ref -notmatch '@[0-9a-f]{40}$' -and $ref -notmatch '^\./') { $unpinned.Add("$file`:$line $ref") }
}
foreach ($u in $unpinned) {
    if ($Tag) { Fail 'action-pin' $u $null 'pin to a commit SHA (pwsh tools/Update-ActionPins.ps1)' }
    else      { Warn 'action-pin' $u $null 'not pinned to a commit SHA yet' }
}

# ------------------------------------------------------------------ docs pages in sync with root documents
function Normalize([string[]] $lines) { (($lines -join "`n") -replace '\s+$', '').Trim() }
foreach ($pair in @(@('PRIVACY.md', 'docs/privacy.md'), @('SECURITY.md', 'docs/security.md'))) {
    $src = Join-Path $Root $pair[0]; $dst = Join-Path $Root $pair[1]
    if (-not ((Test-Path $src) -and (Test-Path $dst))) { continue }
    $s = @(Get-Content -LiteralPath $src); $h1 = [array]::FindIndex($s, [Predicate[string]] { param($l) $l -match '^# ' })
    if ($h1 -ge 0) { $s = @($s | Select-Object -Skip ($h1 + 1)) }
    $d = @(Get-Content -LiteralPath $dst); $fm = @(0..($d.Count - 1) | Where-Object { $d[$_] -eq '---' })
    if ($fm.Count -ge 2) { $d = @($d | Select-Object -Skip ($fm[1] + 1)) }
    if ((Normalize $s) -ne (Normalize $d)) { Fail 'docs-sync' $pair[1] $null "differs from $($pair[0]); run pwsh tools/Sync-DocsPages.ps1" }
}
if ((Test-Path (Join-Path $Root 'LICENSE')) -and (Test-Path (Join-Path $Root 'docs/license.md'))) {
    if (-not (Get-Content -Raw (Join-Path $Root 'docs/license.md')).Contains((Get-Content -Raw (Join-Path $Root 'LICENSE')).Trim())) {
        Fail 'docs-sync' 'docs/license.md' $null 'does not contain the LICENSE text; run pwsh tools/Sync-DocsPages.ps1'
    }
}

# ------------------------------------------------------------------ version / changelog
if ($Tag) {
    $version = $Tag -replace '^v', ''
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { Fail 'version' $Tag $null 'tag is not SemVer (vX.Y.Z or vX.Y.Z-pre)' }
    $cl = Get-Content -Raw (Join-Path $Root 'CHANGELOG.md')
    $m = [regex]::Match($cl, "(?m)^## \[$([regex]::Escape($version))\] - (\S+)\s*$")
    if (-not $m.Success) { Fail 'changelog' 'CHANGELOG.md' $null "no section '## [$version] - YYYY-MM-DD'" }
    elseif ($m.Groups[1].Value -notmatch '^\d{4}-\d{2}-\d{2}$') { Fail 'changelog' 'CHANGELOG.md' $null "release date '$($m.Groups[1].Value)' is not YYYY-MM-DD" }
}

# ------------------------------------------------------------------ report
if ($warnings.Count) {
    Write-Host "Warnings ($($warnings.Count))" -ForegroundColor Yellow
    $warnings | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
}
if ($failures.Count) {
    Write-Host "Release readiness: FAILED ($($failures.Count))" -ForegroundColor Red
    $failures | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
    exit 1
}
Write-Host 'Release readiness: passed' -ForegroundColor Green
exit 0
