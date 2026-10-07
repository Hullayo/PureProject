[CmdletBinding()]
param(
    [ValidateSet('20k', '100k')][string]$Profile = '20k',
    [string]$PublishDirectory = ''
)
$ErrorActionPreference = 'Stop'
$artifactName = if ($Profile -eq '100k') { 'scale-100k' } else { 'scale-audit' }
$tasksPerStatus = if ($Profile -eq '100k') { 100 } else { 20 }
$dataDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts\$artifactName\data"))
$markerPath = Join-Path $dataDirectory 'scale-fixture.json'
$publishRoot = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $PSScriptRoot 'artifacts\publish\win-x64-2.0.1' }
$executable = Join-Path $publishRoot 'PureProject.exe'
if (!(Test-Path -LiteralPath $markerPath)) { throw 'Scale data is missing. Run generate-scale.ps1 once before using this launcher.' }
$marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($marker.schemaVersion -ne 1 -or $marker.fixtureKind -ne 'pureproject-scale-audit' -or $marker.synthetic -ne $true -or $marker.idPrefix -ne 'scale-' -or
    ![IO.Path]::GetFullPath($marker.dataDirectory).Equals($dataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This directory does not have the expected isolated synthetic fixture marker.'
}
if ($marker.dimensions.projects -ne 10 -or $marker.dimensions.groupsPerProject -ne 10 -or
    $marker.dimensions.statusesPerGroup -ne 10 -or $marker.dimensions.tasksPerStatus -ne $tasksPerStatus -or
    $marker.verifiedCounts.tasks -ne (1000 * $tasksPerStatus)) {
    throw 'The marker dimensions do not match the selected profile.'
}
if (!(Test-Path -LiteralPath (Join-Path $dataDirectory 'projects.json')) -or !(Test-Path -LiteralPath (Join-Path $dataDirectory 'settings.json'))) {
    throw 'The scale fixture is incomplete. Its existing files have not been changed.'
}
if (!(Test-Path -LiteralPath $executable)) { throw 'The latest published application is missing. Publish the WinUI application first.' }
# Launch an interactive session, not inherited smoke or audit automation. Changes
# made in this isolated fixture remain available the next time this script runs.
Get-ChildItem Env: | Where-Object Name -Like 'PUREPROJECT_*' | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
$env:PUREPROJECT_DATA_DIR = $dataDirectory
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
