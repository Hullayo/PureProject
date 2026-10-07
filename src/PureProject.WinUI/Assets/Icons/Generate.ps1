[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Source,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SidebarSource
)

$ErrorActionPreference = 'Stop'
$original = Get-Content -LiteralPath $Source -Raw
$generic = [regex]::Match($original, '(?s)<svg class="icon \{cls\}" width=\{size\} height=\{size\} viewBox="0 0 24 24"[^>]*>(?<branches>.*?)</svg>')
if (-not $generic.Success) { throw 'The original generic SVG element was not found.' }
$branches = [regex]::Matches($generic.Groups['branches'].Value, "(?s)\{(?:#if|:else if) name === '(?<name>[^']+)'\}(?<body>.*?)(?=\{(?::else if name|/if))")
$logo = [regex]::Match($original, "(?s)\{#if name === 'logo'\}\s*(?<svg><svg.*?</svg>)").Groups['svg'].Value
if (-not $logo -or $branches.Count -ne 29) { throw "The original icon component changed unexpectedly ($($branches.Count) generic icons)." }
$sidebar = Get-Content -LiteralPath $SidebarSource -Raw
$dashboard = [regex]::Match($sidebar, '(?s)class="dashboard-entry".*?(?<svg><svg\s.*?</svg>)').Groups['svg'].Value
if (-not $dashboard -or ([regex]::Matches($dashboard, '<rect\b')).Count -ne 4) { throw 'The original dashboard SVG with four rectangles was not found.' }

$variants = @(
    @{ Suffix = 'light'; Color = '#62594e'; Accent = '#9f352f' },
    @{ Suffix = 'dark'; Color = '#c9c0b3'; Accent = '#cf6b60' },
    @{ Suffix = 'light-accent'; Color = '#9f352f'; Accent = '#9f352f' },
    @{ Suffix = 'dark-accent'; Color = '#cf6b60'; Accent = '#cf6b60' }
)
$encoding = [System.Text.UTF8Encoding]::new($false)
foreach ($variant in $variants) {
    foreach ($branch in $branches) {
        $name = $branch.Groups['name'].Value
        $body = $branch.Groups['body'].Value.Trim().Replace('currentColor', $variant.Color)
        $svg = '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="' + $variant.Color + '" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + "`n" + $body + "`n</svg>`n"
        [void][xml]$svg
        [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot "$name-$($variant.Suffix).svg"), $svg, $encoding)
    }
    $logoSvg = $logo.Replace('class="icon {cls}" width={size} height={size}', 'xmlns="http://www.w3.org/2000/svg" width="128" height="128"').Replace('var(--accent)', $variant.Accent)
    $logoSvg = $logoSvg.Replace('stroke="rgba(255,250,241,0.82)"', 'stroke="#fffaf1" stroke-opacity="0.82"').Replace('fill="rgba(255,250,241,0.92)"', 'fill="#fffaf1" fill-opacity="0.92"').Replace('fill="rgba(255,250,241,0.76)"', 'fill="#fffaf1" fill-opacity="0.76"')
    [void][xml]$logoSvg
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot "logo-$($variant.Suffix).svg"), $logoSvg + "`n", $encoding)
    $dashboardSvg = $dashboard.Replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ').Replace('currentColor', $variant.Color)
    [void][xml]$dashboardSvg
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot "dashboard-$($variant.Suffix).svg"), $dashboardSvg + "`n", $encoding)
}
Write-Output "Generated $((2 + $branches.Count) * $variants.Count) SVG files from the original Icon.svelte and Sidebar.svelte"
