[CmdletBinding()]
param(
    [string]$PublishDirectory = '',
    [string]$OutputDirectory = '',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$FreezeManifest,
    [Parameter(Mandatory)][ValidatePattern('^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$')][string]$SourceCommit,
    [string]$SourceRepository = '',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Normalize-RelativePath([string]$Value) {
    $path = $Value.Replace('\', '/')
    if (!$path -or [IO.Path]::IsPathRooted($path) -or $path -match '[<>:"|?*\x00-\x1f]' -or
        @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw "Unsafe manifest path: $Value"
    }
    return $path
}

function Get-Inventory([string]$Root, [switch]$Sources) {
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { throw "Directory is missing: $Root" }
    $inventory = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        $relative = Normalize-RelativePath $entry.FullName.Substring($Root.Length + 1)
        if ($Sources -and $relative -match '(^|/)(bin|obj)(/|$)') { continue }
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not package inputs: $relative" }
        if ($entry.PSIsContainer) { continue }
        $inventory.Add($relative, [pscustomobject]@{path=$relative;bytes=$entry.Length;
            sha256=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
    return ,$inventory
}

function Require-SameInventory($Expected, $Actual, [string]$Label) {
    if ($Expected.Count -eq 0 -or $Expected.Count -ne $Actual.Count) { throw "$Label file count differs from the production freeze." }
    foreach ($path in $Expected.Keys) {
        if (!$Actual.ContainsKey($path)) { throw "$Label file is missing: $path" }
        if ($Actual[$path].sha256 -ine $Expected[$path].sha256 -or
            ($null -ne $Expected[$path].bytes -and $Actual[$path].bytes -ne $Expected[$path].bytes)) {
            throw "$Label content differs from the production freeze: $path"
        }
    }
}

function Get-GitBlobHash([string]$Repository, [string]$Commit, [string]$RelativePath) {
    # Direct process invocation: these validated arguments are never shell code.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:gitExecutable
    $start.Arguments = '-C "' + $Repository + '" cat-file blob "' + $Commit + ':src/' + $RelativePath + '"'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process = [Diagnostics.Process]::Start($start)
    $errorText = $process.StandardError.ReadToEndAsync()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $hasher.ComputeHash($process.StandardOutput.BaseStream)
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Commit is missing src/$RelativePath : $($errorText.GetAwaiter().GetResult())" }
        return ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally { $hasher.Dispose(); $process.Dispose() }
}

if (!(Test-Path -LiteralPath $FreezeManifest -PathType Leaf)) { throw 'An explicit production freeze manifest is required.' }
$freezePath = [IO.Path]::GetFullPath($FreezeManifest)
$freeze = Get-Content -LiteralPath $freezePath -Raw | ConvertFrom-Json
if ($freeze.schemaVersion -ne 1 -or $freeze.kind -ne 'pureproject-production-freeze') { throw 'Unsupported production freeze manifest.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/PureProject.WinUI/PureProject.WinUI.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if (!$version -or $version -notmatch '^\d+\.\d+\.\d+$' -or $freeze.productVersion -ne $version) { throw 'Project version and production freeze version differ.' }
if (!$PublishDirectory) { $PublishDirectory = Join-Path $PSScriptRoot "artifacts/publish/win-x64-$version" }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "artifacts/release/winui-$version-candidate" }
if (!$SourceRepository) { $SourceRepository = $PSScriptRoot }
$publish = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$repository = [IO.Path]::GetFullPath($SourceRepository).TrimEnd('\', '/')
if ($repository.Contains('"')) { throw 'Invalid source repository path.' }
if (Test-Path -LiteralPath $output) { throw 'Output already exists; preserve the earlier package and choose a new directory.' }
if ($output.Equals($publish, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($publish + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output cannot be inside the portable application.'
}

$expectedSources = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $freeze.sourceFiles.PSObject.Properties) {
    $path = Normalize-RelativePath $entry.Name
    if ([string]$entry.Value -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid source SHA-256: $path" }
    $expectedSources.Add($path, [pscustomobject]@{path=$path;bytes=$null;sha256=([string]$entry.Value).ToLowerInvariant()})
}
$expectedBinaries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $freeze.binaries) {
    $path = Normalize-RelativePath ([string]$entry.path)
    if ([string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$entry.bytes -lt 0) { throw "Invalid binary record: $path" }
    $expectedBinaries.Add($path, [pscustomobject]@{path=$path;bytes=[long]$entry.bytes;sha256=([string]$entry.sha256).ToLowerInvariant()})
}
$sources = Get-Inventory (Join-Path $PSScriptRoot 'src') -Sources
$binaries = Get-Inventory $publish
Require-SameInventory $expectedSources $sources 'Source'
Require-SameInventory $expectedBinaries $binaries 'Binary'

$required = @('PureProject.exe','PureProject.dll','PureProject.Core.dll','PureProject.Infrastructure.dll',
    'PureProject.pri','PureProject.deps.json','PureProject.runtimeconfig.json','App.xbf','MainWindow.xbf','Styles.xbf','ControlStyles.xbf',
    'coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll',
    'Assets/icon.ico','Assets/Fonts/HarmonyOS_Sans_SC.ttf','Assets/Fonts/README.md','Assets/Fonts/LICENSE.txt')
foreach ($path in $required) {
    if (!$binaries.ContainsKey($path) -or $binaries[$path].bytes -le 0) { throw "Required runtime/resource file is missing or empty: $path" }
}
$mainAssembly = Join-Path $publish 'PureProject.dll'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($mainAssembly).Version.ToString()
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($mainAssembly).ProductVersion
if ($assemblyVersion -ne $freeze.assemblyVersion -or $assemblyVersion -ne "$version.0" -or $productVersion.Split('+')[0] -ne $version) {
    throw 'Published assembly/product version does not match the project and freeze.'
}
$script:gitExecutable = (Get-Command git -ErrorAction Stop).Source
$resolvedCommit = & $script:gitExecutable -C $repository rev-parse --verify ($SourceCommit + '^{commit}') 2>$null
if ($LASTEXITCODE -ne 0 -or ([string]$resolvedCommit).Trim() -ine $SourceCommit) { throw 'SourceCommit is not the supplied full commit in SourceRepository.' }
$committedPaths = @(& $script:gitExecutable -C $repository -c core.quotepath=false ls-tree -r --name-only $SourceCommit -- src)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate committed production sources.' }
$committedPaths = @($committedPaths | ForEach-Object { Normalize-RelativePath $_.Substring('src/'.Length) })
if ($committedPaths.Count -ne $sources.Count) { throw 'Committed source inventory differs from the production freeze.' }
foreach ($path in $committedPaths) {
    if (!$sources.ContainsKey($path) -or (Get-GitBlobHash $repository $SourceCommit $path) -ine $sources[$path].sha256) {
        throw "SourceCommit differs from the frozen production source: $path"
    }
}

$documents = [ordered]@{
    'release-info/STRESS_REMEDIATION_20261006.md' = 'docs/STRESS_REMEDIATION_20261006.md'
    'release-info/SUPPORT_MATRIX.md' = 'docs/SUPPORT_MATRIX.md'
    'release-info/licenses/PureProject-MIT.txt' = 'LICENSE'
    'release-info/licenses/HarmonyOS-Sans/LICENSE.txt' = 'src/PureProject.WinUI/Assets/Fonts/LICENSE.txt'
    'release-info/licenses/HarmonyOS-Sans/README.md' = 'src/PureProject.WinUI/Assets/Fonts/README.md'
}
foreach ($path in $documents.Values) {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $path) -PathType Leaf)) { throw "Required release documentation is missing: $path" }
}
if (@($binaries.Keys | Where-Object { $_ -eq 'release-info' -or $_.StartsWith('release-info/', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
    throw 'The portable application already contains the reserved release-info path.'
}
if ($ValidateOnly) {
    Write-Output "Validated production freeze: $($sources.Count) source files, $($binaries.Count) portable files, version $version, commit $SourceCommit."
    return
}

New-Item -ItemType Directory -Path $output | Out-Null
$staging = Join-Path $output 'staging'
New-Item -ItemType Directory -Path $staging | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $staging -Recurse
Require-SameInventory $expectedBinaries (Get-Inventory $staging) 'Staged binary'
foreach ($item in $documents.GetEnumerator()) {
    $destination = Join-Path $staging $item.Key
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $item.Value) -Destination $destination
}
$candidate = @"
# 简项（PureProject）$version Windows x64 候选包

本包适用于独立的简项 WinUI 客户端。运行 PureProject.exe 时保留完整目录；需要 Windows 10 2004 或更新版本。默认数据目录为 %LOCALAPPDATA%\PureProject\WinUI，可通过 PUREPROJECT_DATA_DIR 指定隔离目录。

发布状态为 candidate。本包不代表全字段满长 100k 或完整 2+8 小时长稳已通过。已验证项、失败和待验证范围见 STRESS_REMEDIATION_20261006.md，容量和功能边界见 SUPPORT_MATRIX.md。打包过程验证冻结来源、文件完整性和版本，不执行原生 GUI 或性能验收。

简项独立仓库源码提交：$SourceCommit。软件许可见 licenses/PureProject-MIT.txt；HarmonyOS Sans 字体使用独立许可，见 licenses/HarmonyOS-Sans/LICENSE.txt。
"@
$candidate | Set-Content -LiteralPath (Join-Path $staging 'release-info/CANDIDATE.md') -Encoding utf8
$packageFiles = Get-Inventory $staging
$manifest = [ordered]@{
    schemaVersion=1; version=$version; channel='candidate'; sourceCommit=$SourceCommit; sourcePath='src'
    createdAt=[DateTimeOffset]::UtcNow.ToString('o'); assemblyVersion=$assemblyVersion
    productionFreezeSha256=(Get-FileHash -LiteralPath $freezePath -Algorithm SHA256).Hash.ToLowerInvariant()
    validation='Provenance, exact file inventories, versions and ZIP contents verified. Runtime and capacity acceptance remain as recorded in CANDIDATE.md and the accompanying reports.'
    sourceFiles=@($sources.Values | Sort-Object path); portableFiles=@($binaries.Values | Sort-Object path)
    packageFiles=@($packageFiles.Values | Sort-Object path)
}
$manifestPath = Join-Path $staging 'release-info/release-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'release-manifest.json')
$completeStaging = Get-Inventory $staging
$zip = Join-Path $output "PureProject-$version-win-x64-candidate.zip"
$partialZip = $zip + '.partial'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $partialZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($partialZip)
try {
    $zipFiles = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $path = Normalize-RelativePath $entry.FullName
        $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $hasher.Dispose(); $stream.Dispose() }
        $zipFiles.Add($path, [pscustomobject]@{path=$path;bytes=$entry.Length;sha256=$hash})
    }
    Require-SameInventory $completeStaging $zipFiles 'ZIP'
}
finally { $archive.Dispose() }
Move-Item -LiteralPath $partialZip -Destination $zip
$sum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$sum  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Candidate package: $zip"
Write-Output "SHA256: $sum"
