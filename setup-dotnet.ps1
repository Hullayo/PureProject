[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sdkVersion = '10.0.401'
$sdkHash = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$toolRoot = Join-Path $PSScriptRoot '.tools'
$installPath = Join-Path $toolRoot 'dotnet'
$dotnetPath = Join-Path $installPath 'dotnet.exe'
$sdkPath = Join-Path $installPath "sdk\$sdkVersion"

if ((Test-Path -LiteralPath $dotnetPath) -and (Test-Path -LiteralPath $sdkPath)) {
    Write-Host ".NET SDK $sdkVersion is already available at $installPath"
    return
}

$downloadPath = Join-Path $toolRoot 'downloads'
New-Item -ItemType Directory -Path $downloadPath -Force | Out-Null
$archive = Join-Path $downloadPath "dotnet-sdk-$sdkVersion-win-x64.zip"
$url = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"

if (!(Test-Path -LiteralPath $archive)) {
    Write-Host "Downloading .NET SDK $sdkVersion from Microsoft..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile "$archive.download" -UseBasicParsing
    Move-Item -LiteralPath "$archive.download" -Destination $archive -Force
}

$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash
if ($actualHash -ne $sdkHash) {
    throw "The SDK archive failed SHA512 verification. Remove $archive and retry."
}

Write-Host 'SHA512 verified. Extracting the SDK into the project directory...'
Expand-Archive -LiteralPath $archive -DestinationPath $installPath -Force
& $dotnetPath --version
if ($LASTEXITCODE -ne 0) { throw 'The downloaded .NET SDK did not start successfully.' }
