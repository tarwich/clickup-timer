$ErrorActionPreference = 'Stop'
$timerDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $timerDotnet)) { $timerDotnet = 'dotnet' }
Push-Location $PSScriptRoot
try {
    & $timerDotnet run --project tests\PlacementChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Placement checks failed.' }
    & $timerDotnet run --project tests\Phase2Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Phase 2 checks failed.' }
    & $timerDotnet publish src\ClickUpTimer -c Release -r win-x64 --self-contained true -o artifacts\phase2
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close ClickUp Timer before rebuilding.' }
} finally { Pop-Location }
