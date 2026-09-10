param([switch]$Inspect)
$timerExe = Join-Path $PSScriptRoot 'artifacts\phase2\ClickUpTimer.exe'
if (-not (Test-Path -LiteralPath $timerExe)) { throw 'Build the app first with Build-Timer.ps1.' }
if ($Inspect) { Start-Process -FilePath $timerExe -ArgumentList '--inspect' }
else { Start-Process -FilePath $timerExe }
