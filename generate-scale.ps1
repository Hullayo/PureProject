[CmdletBinding()]
param(
    [ValidateSet('20k', '100k')][string]$Profile = '20k',
    [string]$ReferenceDate = (Get-Date -Format 'yyyy-MM-dd'),
    [switch]$VerifyOnly,
    [switch]$Replace,
    [switch]$Benchmark,
    [switch]$PersistenceCheck,
    [ValidateRange(1, 10)][int]$Iterations = 5
)
$ErrorActionPreference = 'Stop'
$dotnetPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { throw 'Run setup-dotnet.ps1 first to install the workspace .NET SDK.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$project = Join-Path $PSScriptRoot 'tools\ScaleFixture\PureProject.ScaleFixture.csproj'
$source = Join-Path $PSScriptRoot '.tools\nuget-feed'
$artifactName = if ($Profile -eq '100k') { 'scale-100k' } else { 'scale-audit' }
$toolArgs = @('--profile', $Profile, '--output', (Join-Path $PSScriptRoot "artifacts\$artifactName\data"), '--date', $ReferenceDate)
if ($VerifyOnly) { $toolArgs += '--verify' }
if ($Replace) { $toolArgs += '--replace' }
if ($Benchmark) { $toolArgs += @('--benchmark', '--iterations', $Iterations.ToString()) }
if ($PersistenceCheck) { $toolArgs += '--persistence-check' }
Push-Location $PSScriptRoot
try {
    & $dotnetPath restore $project --source $source
    if ($LASTEXITCODE -ne 0) { throw "Scale fixture restore failed: $LASTEXITCODE" }
    & $dotnetPath run --project $project -c Release --no-restore -- @toolArgs
    if ($LASTEXITCODE -ne 0) { throw "Scale fixture generation or validation failed: $LASTEXITCODE" }
}
finally { Pop-Location }
