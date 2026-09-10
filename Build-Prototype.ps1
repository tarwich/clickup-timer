$ErrorActionPreference = 'Stop'
$prototypeDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $prototypeDotnet)) { $prototypeDotnet = 'dotnet' }
Push-Location $PSScriptRoot
try {
    & $prototypeDotnet run --project tests\PlacementChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Placement checks failed.' }
    & $prototypeDotnet publish src\ClickUpTimer -c Release -r win-x64 --self-contained true -o artifacts\phase1
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close the running prototype before rebuilding.' }
} finally { Pop-Location }
