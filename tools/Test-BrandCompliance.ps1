<#
.SYNOPSIS
    Static brand-compliance checks for the RuleHealth UI (Cerberus design system).

.DESCRIPTION
    Fails (exit 1) on:
      - raw colors (#hex, rgb(), rgba(), hsl()) in ui/ outside ui/brand/ and ui/vendor/
      - external URLs in ui/ (CDNs, Google Fonts): everything must be served locally
      - emoji / pictographs in ui/ HTML, JS and CSS (the brand uses none)
      - font-family declarations that do not use var(--font-sans) / var(--font-mono)
      - stylesheet load order in ui/index.html (cerberus-tokens.css before rulehealth-theme.css)
      - WCAG contrast below the required ratio for the token pairs listed in $ContrastPairs
        (values are resolved from ui/brand/*.css, rgba fills are composited on their surface)
    Warns on:
      - inline style attributes that set colors
      - monospace ligature rule missing from rulehealth-theme.css

.EXAMPLE
    pwsh ./tools/Test-BrandCompliance.ps1
    pwsh ./tools/Test-BrandCompliance.ps1 -UiPath ./ui -Verbose
#>
[CmdletBinding()]
param(
    [string] $UiPath = (Join-Path $PSScriptRoot '..' 'ui')
)

$ErrorActionPreference = 'Stop'
$UiPath = (Resolve-Path $UiPath).Path
$brandDir = Join-Path $UiPath 'brand'
$failures = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[object]]::new()

function Add-Finding($list, $check, $file, $line, $detail) {
    $rel = if ($file) { [System.IO.Path]::GetRelativePath($UiPath, $file) } else { '' }
    $list.Add([pscustomobject]@{ Check = $check; File = $rel; Line = $line; Detail = $detail })
}

# ---------------------------------------------------------------- files in scope
$appFiles = Get-ChildItem -Path $UiPath -Recurse -File -Include *.css, *.js, *.html |
    Where-Object { $_.FullName -notlike (Join-Path $UiPath 'vendor*') -and $_.FullName -notlike "$brandDir*" }
$brandAndApp = Get-ChildItem -Path $UiPath -Recurse -File -Include *.css, *.js, *.html |
    Where-Object { $_.FullName -notlike (Join-Path $UiPath 'vendor*') }

$colorPattern   = '(?<![\w-])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})\b|\b(?:rgba?|hsla?)\s*\('
$urlPattern     = '(?i)(?:src|href)\s*=\s*["'']https?://|url\(\s*["'']?https?://|@import\s+(?:url\()?["'']?https?://|import\s.*from\s+["'']https?://'
$emojiPattern   = '[☀-➿]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDEFF]|\uD83E[\uDD00-\uDEFF]'
$fontPattern    = '(?i)font-family\s*:\s*(?!\s*var\(--font-(?:sans|mono)\))[^;"}]+'
$inlineColorPattern = '(?i)style\s*=\s*"[^"]*(?:color|background)\s*:'

foreach ($f in $appFiles) {
    $lines = @(Get-Content -LiteralPath $f.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^\s*(//|/\*|\*|<!--)') { continue }   # skip comment lines
        if ($l -match $colorPattern)  { Add-Finding $failures 'raw-color'     $f.FullName ($i + 1) $Matches[0] }
        if ($l -match $fontPattern)   { Add-Finding $failures 'font-family'   $f.FullName ($i + 1) $Matches[0].Trim() }
        if ($l -match $inlineColorPattern) { Add-Finding $warnings 'inline-color' $f.FullName ($i + 1) 'color set in a style attribute; use a token class' }
    }
}
foreach ($f in $brandAndApp) {
    $lines = @(Get-Content -LiteralPath $f.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match $urlPattern)   { Add-Finding $failures 'external-url' $f.FullName ($i + 1) $l.Trim() }
        if ($l -match $emojiPattern) { Add-Finding $failures 'emoji'        $f.FullName ($i + 1) $l.Trim() }
    }
}

# ---------------------------------------------------------------- load order
$index = Join-Path $UiPath 'index.html'
if (Test-Path $index) {
    $html = Get-Content -Raw -LiteralPath $index
    $t = $html.IndexOf('brand/cerberus-tokens.css'); $r = $html.IndexOf('brand/rulehealth-theme.css')
    if ($t -lt 0 -or $r -lt 0) { Add-Finding $failures 'load-order' $index $null 'index.html must link brand/cerberus-tokens.css and brand/rulehealth-theme.css' }
    elseif ($t -gt $r)         { Add-Finding $failures 'load-order' $index $null 'cerberus-tokens.css must load before rulehealth-theme.css' }
    if ($html -notmatch 'echarts-cerberus\.js' -and $html -match 'echarts') { Add-Finding $failures 'chart-theme' $index $null 'ECharts is used but brand/echarts-cerberus.js is not loaded' }
} else {
    Add-Finding $warnings 'load-order' $null $null 'ui/index.html not found; load order not checked'
}

# ---------------------------------------------------------------- token resolution + contrast
$vars = @{}
foreach ($css in @('cerberus-tokens.css', 'rulehealth-theme.css')) {
    $p = Join-Path $brandDir $css
    if (-not (Test-Path $p)) { Add-Finding $failures 'tokens' $null $null "missing ui/brand/$css"; continue }
    $text = Get-Content -Raw -LiteralPath $p
    foreach ($m in [regex]::Matches($text, '(--[\w-]+)\s*:\s*([^;]+);')) { $vars[$m.Groups[1].Value] = $m.Groups[2].Value.Trim() }
}
if ((Test-Path (Join-Path $brandDir 'rulehealth-theme.css')) -and
    (Get-Content -Raw (Join-Path $brandDir 'rulehealth-theme.css')) -notmatch 'font-variant-ligatures:\s*none') {
    Add-Finding $warnings 'ligatures' $null $null 'monospace ligature rule missing: == and != will render as glyphs'
}

function Resolve-Token([string] $value, [int] $depth = 0) {
    if ($depth -gt 10) { return $null }
    $v = ($value -replace '/\*.*?\*/', '').Trim()
    if ($v -match '^var\((--[\w-]+)\)$') { if ($vars.ContainsKey($Matches[1])) { return Resolve-Token $vars[$Matches[1]] ($depth + 1) } else { return $null } }
    return $v
}
function ConvertTo-Rgba([string] $v) {
    if ($v -match '^#([0-9a-fA-F]{6})$') {
        $h = $Matches[1]; return @([Convert]::ToInt32($h.Substring(0,2),16), [Convert]::ToInt32($h.Substring(2,2),16), [Convert]::ToInt32($h.Substring(4,2),16), 1.0)
    }
    if ($v -match '^#([0-9a-fA-F]{3})$') {
        $h = $Matches[1]; return @(([Convert]::ToInt32("$($h[0])$($h[0])",16)), ([Convert]::ToInt32("$($h[1])$($h[1])",16)), ([Convert]::ToInt32("$($h[2])$($h[2])",16)), 1.0)
    }
    if ($v -match '^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+))?\s*\)$') {
        $a = if ($Matches[4]) { [double]$Matches[4] } else { 1.0 }
        return @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3], $a)
    }
    return $null
}
function Get-Composite($fg, $bg) {   # alpha-composite fg over an opaque bg
    $a = $fg[3]; return @(([math]::Round($fg[0]*$a + $bg[0]*(1-$a))), ([math]::Round($fg[1]*$a + $bg[1]*(1-$a))), ([math]::Round($fg[2]*$a + $bg[2]*(1-$a))), 1.0)
}
function Get-Luminance($c) {
    $ch = 0..2 | ForEach-Object { $x = $c[$_] / 255; if ($x -le 0.03928) { $x / 12.92 } else { [math]::Pow(($x + 0.055) / 1.055, 2.4) } }
    return 0.2126*$ch[0] + 0.7152*$ch[1] + 0.0722*$ch[2]
}
function Get-Contrast($a, $b) {
    $la = Get-Luminance $a; $lb = Get-Luminance $b
    return ([math]::Max($la,$lb) + 0.05) / ([math]::Min($la,$lb) + 0.05)
}

# Foreground token, fill token (may be translucent, composited on Surface), surface token, minimum ratio, why
$ContrastPairs = @(
    @('--rh-text',          $null,              '--rh-surface', 4.5, 'body text'),
    @('--rh-text-2',        $null,              '--rh-surface', 4.5, 'secondary text'),
    @('--rh-text-3',        $null,              '--rh-surface', 4.5, 'small/muted text'),
    @('--rh-text-3',        $null,              '--rh-raised',  4.5, 'muted text on raised controls'),
    @('--rh-accent',        $null,              '--rh-surface', 4.5, 'links, eyebrows'),
    @('--rh-text',          '--rh-active-fill', '--rh-surface', 4.5, 'active nav / selected segment'),
    @('--rh-secure-text',   '--rh-secure-fill', '--rh-surface', 4.5, 'present chip'),
    @('--rh-info-text',     '--rh-info-fill',   '--rh-surface', 4.5, 'info chip, badges'),
    @('--rh-warn-text',     '--rh-warn-fill',   '--rh-surface', 4.5, 'table-empty / demo'),
    @('--rh-crit-text',     '--rh-crit-fill',   '--rh-surface', 4.5, 'absent chip'),
    @('--rh-neutral-text',  '--rh-neutral-fill','--rh-surface', 4.5, 'skipped chip'),
    @('--rh-v-error-text',  '--rh-v-error-fill','--rh-surface', 4.5, 'error chip'),
    @('--rh-tp',            $null,              '--rh-surface', 3.0, 'chart series TP'),
    @('--rh-bp',            $null,              '--rh-surface', 3.0, 'chart series BP'),
    @('--rh-fp',            $null,              '--rh-surface', 3.0, 'chart series FP'),
    @('--rh-undetermined',  $null,              '--rh-surface', 3.0, 'chart series Undetermined'),
    @('--rh-open',          $null,              '--rh-surface', 3.0, 'chart series Open')
)
$contrastRows = foreach ($p in $ContrastPairs) {
    $fgV = Resolve-Token "var($($p[0]))"; $surV = Resolve-Token "var($($p[2]))"
    $fg = ConvertTo-Rgba $fgV; $sur = ConvertTo-Rgba $surV
    if (-not $fg -or -not $sur) { Add-Finding $failures 'contrast' $null $null "cannot resolve $($p[0]) on $($p[2])"; continue }
    $bg = $sur
    if ($p[1]) { $fill = ConvertTo-Rgba (Resolve-Token "var($($p[1]))"); if ($fill) { $bg = Get-Composite $fill $sur } }
    $fgC = if ($fg[3] -lt 1) { Get-Composite $fg $bg } else { $fg }
    $ratio = [math]::Round((Get-Contrast $fgC $bg), 2)
    if ($ratio -lt $p[3]) { Add-Finding $failures 'contrast' $null $null ("{0} on {1}{2}: {3}:1 < {4}:1 ({5})" -f $p[0], ($p[1] ?? ''), $p[2], $ratio, $p[3], $p[4]) }
    [pscustomobject]@{ Pair = "$($p[0]) on $(if ($p[1]) { "$($p[1]) over " })$($p[2])"; Ratio = $ratio; Required = $p[3]; Use = $p[4] }
}

# Adjacent chart series must differ in lightness (not hue alone)
function Get-LStar($c) { $y = Get-Luminance $c; if ($y -gt 0.008856) { 116 * [math]::Pow($y, 1/3) - 16 } else { 903.3 * $y } }
$order = '--rh-tp', '--rh-bp', '--rh-fp', '--rh-undetermined', '--rh-open'
for ($i = 0; $i -lt $order.Count - 1; $i++) {
    $a = ConvertTo-Rgba (Resolve-Token "var($($order[$i]))"); $b = ConvertTo-Rgba (Resolve-Token "var($($order[$i+1]))")
    if ($a -and $b) {
        $d = [math]::Abs((Get-LStar $a) - (Get-LStar $b))
        if ($d -lt 15) { Add-Finding $failures 'series-lightness' $null $null ("{0} vs {1}: ΔL* {2:N0} < 15" -f $order[$i], $order[$i+1], $d) }
    }
}

# ---------------------------------------------------------------- report
Write-Host "`nContrast (resolved from ui/brand tokens)" -ForegroundColor Cyan
$contrastRows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if ($warnings.Count) { Write-Host "Warnings: $($warnings.Count)" -ForegroundColor Yellow; $warnings | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host }
if ($failures.Count) {
    Write-Host "Failures: $($failures.Count)" -ForegroundColor Red
    $failures | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
    exit 1
}
Write-Host "Brand compliance: OK ($($appFiles.Count) app files checked)" -ForegroundColor Green
exit 0
