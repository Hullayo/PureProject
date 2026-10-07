[CmdletBinding()]
param(
    [ValidateSet('20k','100k')][string]$Profile = '100k',
    [string]$Scenarios = 'all',
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = 'native-all',
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot $(if ($Profile -eq '100k') { 'artifacts\scale-100k' } else { 'artifacts\scale-audit' })
$output = Join-Path $root $RunName
if (Test-Path (Join-Path $output 'process-monitor.json')) { throw 'Choose a new RunName to preserve previous evidence.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:PUREPROJECT_DATA_DIR = Join-Path $root 'data'
$env:PUREPROJECT_UI_SCALE_TEST = '1'
$env:PUREPROJECT_UI_SCALE_OUTPUT = $output
$env:PUREPROJECT_UI_SCALE_SCENARIOS = $Scenarios
$env:PUREPROJECT_UI_SMOKE_TEST = $null
$exe = Join-Path $PSScriptRoot 'artifacts\publish\win-x64-2.0.1\PureProject.exe'
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru
$timer = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
$guard = $null
while (!$process.HasExited) {
    $process.Refresh()
    if ($process.HasExited) { break }
    $samples.Add([pscustomobject]@{ elapsedMs = $timer.ElapsedMilliseconds; privateBytes = $process.PrivateMemorySize64; workingSetBytes = $process.WorkingSet64; osPeakWorkingSetBytes = $process.PeakWorkingSet64; cpuSeconds = $process.TotalProcessorTime.TotalSeconds })
    if ($process.PrivateMemorySize64 -gt 3GB) { $guard = 'Private memory exceeded 3 GiB'; break }
    if ($timer.Elapsed.TotalSeconds -gt $TimeoutSeconds) { $guard = 'Wall-clock deadline exceeded'; break }
    Start-Sleep -Milliseconds 250
}
if ($guard) { Stop-Process -Id $process.Id -ErrorAction Stop }
$process.WaitForExit()
$result = [ordered]@{
    processId = $process.Id; exitCode = $process.ExitCode; guard = $guard; elapsedMs = $timer.ElapsedMilliseconds
    sampleIntervalMs = 250; sampledPeakPrivateBytes = ($samples | Measure-Object privateBytes -Maximum).Maximum
    osPeakWorkingSetBytes = ($samples | Measure-Object osPeakWorkingSetBytes -Maximum).Maximum
    publishedAssemblySha256 = (Get-FileHash (Join-Path (Split-Path $exe) 'PureProject.dll') -Algorithm SHA256).Hash
    samples = $samples
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'process-monitor.json') -Encoding UTF8
[pscustomobject]($result | Select-Object *) | Out-Null
Write-Output "Scale run: $output; exit=$($process.ExitCode); elapsed=$($timer.Elapsed.TotalSeconds); guard=$guard"
if ($guard -or $process.ExitCode -ne 0) { exit 1 }
