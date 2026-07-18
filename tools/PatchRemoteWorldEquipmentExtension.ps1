param(
    [string]$ClientExe = "C:\Godswar Origin\Origin.exe",
    [ValidateSet("Apply", "Revert")]
    [string]$Mode = "Apply",
    [string]$BackupRoot
)

$ErrorActionPreference = "Stop"

function Convert-HexBytes {
    param([string]$Hex)

    if (($Hex.Length % 2) -ne 0) {
        throw "Hex byte string must contain an even number of characters."
    }

    [byte[]]$bytes = for ($offset = 0; $offset -lt $Hex.Length; $offset += 2) {
        [Convert]::ToByte($Hex.Substring($offset, 2), 16)
    }
    return $bytes
}

# This patch is specific to the tracked 6,676,480-byte Origin.exe build. The
# native 0x2725 decoder at VA 0x004731A5 normally splits packet byte 81+i into
# Q/G nibbles. It is redirected to reserved executable .rdata slack at
# VA 0x009C3270, where a guarded decoder reads the local GWX1 tail:
#
#   +260  uint32  "GWX1"
#   +264  byte[18] full quality
#   +282  byte[18] full grade
#
# The declared packet length must be at least 300 and the marker must match. Every other
# packet follows the original nibble decoder, including native 260-byte spawns.
$expectedLength = 6676480
$hookOffset = 0x731A5
$caveOffset = 0x5C3270

$originalHook = Convert-HexBytes (
    "8A4433510FB6D0")
$patchedHook = Convert-HexBytes (
    "E9C60055009090")
$caveCode = Convert-HexBytes (
    "66813B2C01721D81BB040100004757583175110FB69433080100008A84331A010000" +
    "EB0D8A4433510FB6D083E20FC0E804894C2420E916FFAAFF")
$emptyCave = [byte[]]::new(64)
$patchedCave = [byte[]]::new(64)
[Array]::Copy($caveCode, 0, $patchedCave, 0, $caveCode.Length)

function Test-Bytes {
    param(
        [byte[]]$Data,
        [int]$Offset,
        [byte[]]$Expected
    )

    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Data.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }

    return $true
}

function Copy-Bytes {
    param(
        [byte[]]$Source,
        [byte[]]$Destination,
        [int]$Offset
    )

    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Test-ExecutableFileOffset {
    param(
        [byte[]]$Data,
        [int]$FileOffset
    )

    $peOffset = [BitConverter]::ToInt32($Data, 0x3C)
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $optionalHeaderSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $sectionTableOffset = $peOffset + 24 + $optionalHeaderSize
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount; $sectionIndex++) {
        $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
        $rawSize = [BitConverter]::ToInt32($Data, $sectionOffset + 16)
        $rawOffset = [BitConverter]::ToInt32($Data, $sectionOffset + 20)
        if ($FileOffset -lt $rawOffset -or $FileOffset -ge $rawOffset + $rawSize) {
            continue
        }

        $characteristics = [BitConverter]::ToUInt32($Data, $sectionOffset + 36)
        return ($characteristics -band 0x20000000) -ne 0
    }

    return $false
}

if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
    throw "Origin client executable not found: $ClientExe"
}

$resolvedClientExe = (Resolve-Path -LiteralPath $ClientExe).Path
$clientProcessName = [IO.Path]::GetFileNameWithoutExtension($resolvedClientExe)
$runningClient = Get-Process -Name $clientProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $resolvedClientExe }
if ($null -ne $runningClient) {
    throw "$([IO.Path]::GetFileName($resolvedClientExe)) is running. Close it before changing the executable."
}

$data = [IO.File]::ReadAllBytes($resolvedClientExe)
if ($data.Length -ne $expectedLength) {
    throw "Unsupported Origin.exe size $($data.Length); expected $expectedLength bytes."
}
if (-not (Test-ExecutableFileOffset $data $caveOffset)) {
    throw "The reserved decoder cave is not in an executable PE section; refusing to patch."
}

$hasOriginalHook = Test-Bytes $data $hookOffset $originalHook
$hasPatchedHook = Test-Bytes $data $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $data $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $data $caveOffset $patchedCave

if ($Mode -eq "Apply" -and $hasPatchedHook -and $hasPatchedCave) {
    [pscustomobject]@{
        Mode = $Mode
        Path = $resolvedClientExe
        Status = "Already patched"
        Sha256 = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
    }
    return
}

if ($Mode -eq "Revert" -and $hasOriginalHook -and $hasEmptyCave) {
    [pscustomobject]@{
        Mode = $Mode
        Path = $resolvedClientExe
        Status = "Already original"
        Sha256 = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
    }
    return
}

if ($Mode -eq "Apply" -and (-not $hasOriginalHook -or -not $hasEmptyCave)) {
    throw "Origin.exe does not match the expected unpatched hook/cave bytes; refusing a partial patch."
}

if ($Mode -eq "Revert" -and (-not $hasPatchedHook -or -not $hasPatchedCave)) {
    throw "Origin.exe does not match the exact GWX1 hook/cave bytes; refusing a partial revert."
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "backups"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmssfff"
$backupDirectory = Join-Path $BackupRoot "origin-remote-world-equipment-$Mode-$timestamp"
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
$backupPath = Join-Path $backupDirectory "Origin.exe"
Copy-Item -LiteralPath $resolvedClientExe -Destination $backupPath

$beforeHash = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
if ($Mode -eq "Apply") {
    Copy-Bytes $patchedHook $data $hookOffset
    Copy-Bytes $patchedCave $data $caveOffset
}
else {
    Copy-Bytes $originalHook $data $hookOffset
    Copy-Bytes $emptyCave $data $caveOffset
}

[IO.File]::WriteAllBytes($resolvedClientExe, $data)

$written = [IO.File]::ReadAllBytes($resolvedClientExe)
$expectedHook = if ($Mode -eq "Apply") { $patchedHook } else { $originalHook }
$expectedCave = if ($Mode -eq "Apply") { $patchedCave } else { $emptyCave }
if (-not (Test-Bytes $written $hookOffset $expectedHook) -or
    -not (Test-Bytes $written $caveOffset $expectedCave)) {
    throw "Origin.exe verification failed after $Mode. Backup: $backupPath"
}

[pscustomobject]@{
    Mode = $Mode
    Path = $resolvedClientExe
    Status = if ($Mode -eq "Apply") { "Patched" } else { "Reverted" }
    HookFileOffset = ('0x{0:X}' -f $hookOffset)
    CaveFileOffset = ('0x{0:X}' -f $caveOffset)
    Backup = $backupPath
    BeforeSha256 = $beforeHash
    AfterSha256 = (Get-FileHash -LiteralPath $resolvedClientExe -Algorithm SHA256).Hash
}
