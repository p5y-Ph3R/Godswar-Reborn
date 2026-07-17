$ErrorActionPreference = 'Stop'

$clientDir = 'C:\Godswar Origin'
$source = Join-Path $clientDir 'Origin_sixsocket.exe'
$target = Join-Path $clientDir 'Origin.exe'
$backup = 'C:\Reborn\backups\six-socket-client-20260516-165022\Origin.exe'

if (Get-Process Origin -ErrorAction SilentlyContinue) {
    throw 'Origin.exe is still running. Close the game client first, then run this script again.'
}

if (-not (Test-Path -LiteralPath $source)) {
    throw "Patched executable not found: $source"
}

if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $target -Destination $backup -Force
}

Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "Installed six-socket client patch to $target"
