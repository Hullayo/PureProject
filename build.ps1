[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Test', 'Publish', 'Run')]
    [string]$Task = 'Build',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Offline,
    [string]$PublishDirectory = ''
)

$ErrorActionPreference = 'Stop'
$dotnetPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) {
    if ($Offline) { throw 'The local .NET SDK is missing. Run setup-dotnet.ps1 while online first.' }
    & (Join-Path $PSScriptRoot 'setup-dotnet.ps1')
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.nuget\http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

$appProject = Join-Path $PSScriptRoot 'src\PureProject.WinUI\PureProject.WinUI.csproj'
$restoreOptions = @()
if ($Offline) { $restoreOptions = @('--no-restore') }
function Invoke-DotNet {
    param([string[]]$Arguments)
    & $dotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

Push-Location $PSScriptRoot
try {
    switch ($Task) {
        'Restore' {
            $sources = @()
            if ($Offline) { $sources = @('--source', (Join-Path $PSScriptRoot '.tools\nuget-feed')) }
            Invoke-DotNet -Arguments (@('restore', 'PureProject.sln', '-p:Platform=x64') + $sources)
        }
        'Build' {
            Invoke-DotNet -Arguments (@('build', $appProject, '-c', $Configuration, '-p:Platform=x64') + $restoreOptions)
        }
        'Test' {
            $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.csproj' -Recurse | Sort-Object FullName)
            if ($testProjects.Count -eq 0) { throw 'No self-test projects were found.' }
            foreach ($testProject in $testProjects) {
                Write-Host "Running $($testProject.BaseName)..."
                Invoke-DotNet -Arguments (@('run', '--project', $testProject.FullName, '-c', $Configuration) + $restoreOptions)
            }
        }
        'Publish' {
            $outputPath = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $PSScriptRoot 'artifacts\publish\win-x64-2.0.1' }
            Invoke-DotNet -Arguments (@('publish', $appProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '-p:Platform=x64', '-o', $outputPath) + $restoreOptions)
            Write-Host "Portable application: $outputPath\PureProject.exe"
        }
        'Run' {
            Invoke-DotNet -Arguments (@('build', $appProject, '-c', $Configuration, '-p:Platform=x64') + $restoreOptions)
            $targetPath = & $dotnetPath msbuild $appProject "-p:Configuration=$Configuration" '-p:Platform=x64' '-getProperty:TargetPath'
            if ($LASTEXITCODE -ne 0) { throw 'Could not determine the application output path.' }
            $executablePath = [IO.Path]::ChangeExtension(($targetPath -join '').Trim(), '.exe')
            if (!(Test-Path -LiteralPath $executablePath)) { throw "Application executable was not found: $executablePath" }
            Start-Process -FilePath $executablePath -WorkingDirectory (Split-Path -Parent $executablePath)
        }
    }
}
finally {
    Pop-Location
}
