# Explicit interactive launcher for the independently generated 100,000-task fixture.
# Generate once with: .\generate-scale.ps1 -Profile 100k
& (Join-Path $PSScriptRoot 'run-scale.ps1') -Profile 100k
