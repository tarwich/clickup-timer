param([switch]$Inspect)
$prototypeExe = Join-Path $PSScriptRoot 'artifacts\phase1\ClickUpTimer.exe'
if (-not (Test-Path -LiteralPath $prototypeExe)) {
    throw 'Build the prototype first with Build-Prototype.ps1.'
}
if ($Inspect) {
    Start-Process -FilePath $prototypeExe -ArgumentList '--inspect'
} else {
    Start-Process -FilePath $prototypeExe
}
