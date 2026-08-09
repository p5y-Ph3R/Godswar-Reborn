[CmdletBinding()]
param(
    [string] $ClientRoot = 'C:\Godswar Origin',
    [string] $BackupRoot = (Join-Path $PSScriptRoot '..\backups'),
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-HexBytes([string] $Hex) {
    $normalized = $Hex -replace '\s', ''
    if ($normalized.Length % 2 -ne 0) {
        throw 'Hex input has an odd number of characters.'
    }

    [byte[]] $bytes = for ($index = 0;
        $index -lt $normalized.Length;
        $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $bytes
}

function Test-Bytes(
    [byte[]] $Data,
    [int] $Offset,
    [byte[]] $Expected
) {
    if ($Offset -lt 0 -or
        $Offset + $Expected.Length -gt $Data.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Set-Bytes(
    [byte[]] $Data,
    [int] $Offset,
    [byte[]] $Value
) {
    [Array]::Copy($Value, 0, $Data, $Offset, $Value.Length)
}

$clientRootPath = [IO.Path]::GetFullPath($ClientRoot)
$clientExe = Join-Path $clientRootPath 'Origin.exe'
if (-not (Test-Path -LiteralPath $clientExe -PathType Leaf)) {
    throw "Origin.exe is missing: $clientExe"
}

$defaultClientExe = [IO.Path]::GetFullPath(
    'C:\Godswar Origin\Origin.exe')
if (-not $Check -and
    [string]::Equals(
        $clientExe,
        $defaultClientExe,
        [StringComparison]::OrdinalIgnoreCase) -and
    @(Get-Process Origin -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Origin.exe before applying the mount Speed parser fix.'
}

[byte[]] $data = [IO.File]::ReadAllBytes($clientExe)
$speedAttributeGuardOffset = 0x03CECA
$speedIntermediateGuardOffset = 0x03CF5D
$finalConversionOffset = 0x03D017
$integerMaterializationOffset = 0x03D01C
$speedRendererGuardOffset = 0x181A8D

$speedAttributeGuard = Convert-HexBytes @'
BF 94 12 95 00 8B F0 B9 06 00 00 00 33 D2 F3 A6
'@
$speedIntermediateFloatCall = Convert-HexBytes @'
E8 32 DC 37 00 D9 5C 24 1C 83 C4 04
'@
$originalFinalConversion = Convert-HexBytes 'E8 B8 D7 37 00'
$patchedFinalConversion = Convert-HexBytes 'E8 78 DB 37 00'
$originalIntegerMaterialization = Convert-HexBytes @'
89 44 24 1C DB 44 24 1C
'@
$patchedIntegerMaterialization = Convert-HexBytes @'
90 90 90 90 90 90 90 90
'@
$speedRendererGuard = Convert-HexBytes @'
0F BE 6E 48 8B B6 F4 00 00 00 8B 7B 1C
8B 86 D0 02 00 00 81 C6 CC 02 00 00
'@

if (-not (Test-Bytes $data $speedAttributeGuardOffset $speedAttributeGuard) -or
    -not (Test-Bytes $data $speedIntermediateGuardOffset (
        $speedIntermediateFloatCall)) -or
    -not (Test-Bytes $data $speedRendererGuardOffset $speedRendererGuard)) {
    throw 'Origin.exe does not contain the audited Speed parser/renderer path.'
}

$hasOriginalCall = Test-Bytes (
    $data) $finalConversionOffset $originalFinalConversion
$hasPatchedCall = Test-Bytes (
    $data) $finalConversionOffset $patchedFinalConversion
$hasOriginalMaterialization = Test-Bytes (
    $data) $integerMaterializationOffset $originalIntegerMaterialization
$hasPatchedMaterialization = Test-Bytes (
    $data) $integerMaterializationOffset $patchedIntegerMaterialization

$state = if ($hasOriginalCall -and $hasOriginalMaterialization) {
    'Original'
}
elseif ($hasPatchedCall -and $hasPatchedMaterialization) {
    'Patched'
}
else {
    throw 'Origin.exe has an unknown or partially applied mount Speed parser state.'
}

if ($Check) {
    if ($state -ne 'Patched') {
        throw 'The mount Speed final-decimal parser fix is not installed.'
    }
    Write-Host 'Verified mount Speed final-decimal parser fix.'
    return
}

if ($state -eq 'Patched') {
    Write-Host 'Mount Speed final-decimal parser fix is already installed.'
    return
}

$backupDirectory = Join-Path (
    [IO.Path]::GetFullPath($BackupRoot)) (
    'client-mount-speed-parser-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
Copy-Item -LiteralPath $clientExe -Destination (
    Join-Path $backupDirectory 'Origin.exe')

Set-Bytes $data $finalConversionOffset $patchedFinalConversion
Set-Bytes $data $integerMaterializationOffset (
    $patchedIntegerMaterialization)

$temporary = "$clientExe.mount-speed-$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllBytes($temporary, $data)
    Move-Item -LiteralPath $temporary -Destination $clientExe -Force
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

[byte[]] $installed = [IO.File]::ReadAllBytes($clientExe)
if (-not (Test-Bytes $installed $finalConversionOffset (
            $patchedFinalConversion)) -or
    -not (Test-Bytes $installed $integerMaterializationOffset (
            $patchedIntegerMaterialization))) {
    throw 'Origin.exe mount Speed parser post-write verification failed.'
}

Write-Host 'Installed mount Speed final-decimal parser fix.'
Write-Host "Backup: $backupDirectory"
