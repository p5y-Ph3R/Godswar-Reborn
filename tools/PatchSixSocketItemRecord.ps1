$ErrorActionPreference = 'Stop'

$clientDir = 'C:\Godswar Origin'
$exePath = Join-Path $clientDir 'Origin_sixsocket.exe'
$backupDir = Join-Path $PSScriptRoot '..\backups\six-socket-client-20260516-165022'
$backupPath = Join-Path $backupDir 'Origin_sixsocket.pre-item-record-layout.exe'

$imageBase = 0x400000
$caveBase = 0x009C3100

function Get-Offset([int]$va) {
    return $va - $imageBase
}

function Read-U16([byte[]]$bytes, [int]$offset) {
    return [BitConverter]::ToUInt16($bytes, $offset)
}

function Read-U32([byte[]]$bytes, [int]$offset) {
    return [BitConverter]::ToUInt32($bytes, $offset)
}

function Write-U32([byte[]]$bytes, [int]$offset, [uint32]$value) {
    [BitConverter]::GetBytes($value).CopyTo($bytes, $offset)
}

function Assert-Bytes([byte[]]$bytes, [int]$va, [byte[]]$expected, [string]$name) {
    $offset = Get-Offset $va
    for ($i = 0; $i -lt $expected.Length; $i++) {
        if ($bytes[$offset + $i] -ne $expected[$i]) {
            $actual = ($bytes[$offset..($offset + $expected.Length - 1)] | ForEach-Object { $_.ToString('X2') }) -join ' '
            $want = ($expected | ForEach-Object { $_.ToString('X2') }) -join ' '
            throw "$name byte validation failed at VA 0x$($va.ToString('X8')). Expected [$want], got [$actual]."
        }
    }
}

function Assert-ZeroRange([byte[]]$bytes, [int]$va, [int]$length, [string]$name) {
    $offset = Get-Offset $va
    for ($i = 0; $i -lt $length; $i++) {
        if ($bytes[$offset + $i] -ne 0) {
            throw "$name cave is not empty at VA 0x$(($va + $i).ToString('X8'))."
        }
    }
}

function New-Jmp([int]$fromVa, [int]$toVa) {
    $rel = [int]($toVa - ($fromVa + 5))
    return [byte[]](0xE9) + [BitConverter]::GetBytes($rel)
}

function Write-Patch([byte[]]$bytes, [int]$va, [byte[]]$patch) {
    $offset = Get-Offset $va
    $patch.CopyTo($bytes, $offset)
}

function Write-JmpPatch([byte[]]$bytes, [int]$fromVa, [int]$toVa, [int]$overwriteLength) {
    if ($overwriteLength -lt 5) {
        throw "Cannot write a near jump into $overwriteLength bytes."
    }

    $patch = New-Jmp $fromVa $toVa
    if ($overwriteLength -gt 5) {
        $patch += [byte[]](,0x90 * ($overwriteLength - 5))
    }

    Write-Patch $bytes $fromVa $patch
}

function Write-CodeCave([byte[]]$bytes, [int]$va, [byte[]]$body, [int]$returnVa) {
    $jmp = New-Jmp ($va + $body.Length) $returnVa
    Write-Patch $bytes $va ($body + $jmp)
}

function Enable-RDataExecute([byte[]]$bytes) {
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    $sectionCount = Read-U16 $bytes ($peOffset + 6)
    $optionalHeaderSize = Read-U16 $bytes ($peOffset + 20)
    $sectionOffset = $peOffset + 24 + $optionalHeaderSize

    for ($i = 0; $i -lt $sectionCount; $i++) {
        $current = $sectionOffset + ($i * 40)
        $name = [Text.Encoding]::ASCII.GetString($bytes, $current, 8).Trim([char]0)
        if ($name -eq '.rdata') {
            $characteristicsOffset = $current + 36
            $characteristics = Read-U32 $bytes $characteristicsOffset
            $executable = $characteristics -bor 0x20000000
            Write-U32 $bytes $characteristicsOffset $executable
            return
        }
    }

    throw 'Could not find .rdata section header.'
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Patched client executable not found: $exePath"
}

if (Get-Process Origin_sixsocket -ErrorAction SilentlyContinue) {
    throw 'Origin_sixsocket.exe is running. Close the client before patching the executable.'
}

New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $exePath -Destination $backupPath -Force
}

$bytes = [IO.File]::ReadAllBytes($exePath)

Assert-ZeroRange $bytes $caveBase 0x140 'six-socket item record'

Assert-Bytes $bytes 0x0044203B ([byte[]](0x8B,0x50,0x38,0x89,0x57,0x68,0x8B,0x40,0x3C,0x89,0x47,0x6C)) 'item parser tail copy'
Assert-Bytes $bytes 0x00582349 ([byte[]](0x83,0xC1,0x30,0x83,0xC0,0x5C,0x89,0x74,0x24,0x30,0x89,0x4C,0x24,0x14,0x89,0x44,0x24,0x18)) 'socket display pointer init'
Assert-Bytes $bytes 0x0058235B ([byte[]](0x8B,0x44,0x24,0x18,0x0F,0xB7,0x48,0xF8)) 'socket display first effect load'
Assert-Bytes $bytes 0x005824E0 ([byte[]](0x0F,0xB7,0x47,0xF8,0x6A,0x0A)) 'socket display second effect load'
Assert-Bytes $bytes 0x00582582 ([byte[]](0x0F,0xB7,0x17,0x89,0x54,0x24,0x20)) 'socket display percent value load'
Assert-Bytes $bytes 0x005825F0 ([byte[]](0x0F,0xB7,0x07,0x66,0x3B,0xC5)) 'socket display flat value load'
Assert-Bytes $bytes 0x005823C1 ([byte[]](0x8B,0x70,0xF0)) 'socket display first button load'
Assert-Bytes $bytes 0x00582425 ([byte[]](0x8B,0x71,0xF0)) 'socket display second button load'
Assert-Bytes $bytes 0x0058263C ([byte[]](0x8B,0x4F,0xF0)) 'socket display final button load'

Enable-RDataExecute $bytes

$parserCave = $caveBase
$initCave = $caveBase + 0x40
$effectToEcxCave = $caveBase + 0x60
$effectToEaxPushCave = $caveBase + 0x90
$valueToEdxStoreCave = $caveBase + 0xC0
$valueToEaxCmpCave = $caveBase + 0xF0

Write-CodeCave $bytes $parserCave ([byte[]](
    0x8B,0x50,0x38,
    0x89,0x57,0x68,
    0x8B,0x48,0x3C,
    0x89,0x4F,0x6C,
    0x0F,0xB7,0x50,0x34,
    0x66,0x89,0x57,0x64,
    0x0F,0xB7,0x50,0x36,
    0x66,0x89,0x57,0x66,
    0x0F,0xB7,0x50,0x40,
    0x66,0x89,0x57,0x70,
    0x0F,0xB7,0x50,0x42,
    0x66,0x89,0x57,0x72,
    0x8B,0xC1
)) 0x00442047

Write-CodeCave $bytes $initCave ([byte[]](
    0x81,0xC1,0x98,0x01,0x00,0x00,
    0x83,0xC0,0x5C,
    0x89,0x74,0x24,0x30,
    0x89,0x4C,0x24,0x14,
    0x89,0x44,0x24,0x18
)) 0x0058235B

Write-CodeCave $bytes $effectToEcxCave ([byte[]](
    0x8B,0x44,0x24,0x24,
    0x83,0xF8,0x04,
    0x7C,0x0E,
    0x83,0xE8,0x04,
    0x8B,0x54,0x24,0x34,
    0x0F,0xB7,0x4C,0x42,0x64,
    0xEB,0x08,
    0x8B,0x44,0x24,0x18,
    0x0F,0xB7,0x48,0xF8
)) 0x00582363

Write-CodeCave $bytes $effectToEaxPushCave ([byte[]](
    0x8B,0x44,0x24,0x24,
    0x83,0xF8,0x04,
    0x7C,0x0E,
    0x83,0xE8,0x04,
    0x8B,0x54,0x24,0x34,
    0x0F,0xB7,0x44,0x42,0x64,
    0xEB,0x04,
    0x0F,0xB7,0x47,0xF8,
    0x6A,0x0A
)) 0x005824E6

Write-CodeCave $bytes $valueToEdxStoreCave ([byte[]](
    0x8B,0x54,0x24,0x24,
    0x83,0xFA,0x04,
    0x7C,0x0E,
    0x83,0xEA,0x04,
    0x8B,0x44,0x24,0x34,
    0x0F,0xB7,0x54,0x50,0x70,
    0xEB,0x03,
    0x0F,0xB7,0x17,
    0x89,0x54,0x24,0x20
)) 0x00582589

Write-CodeCave $bytes $valueToEaxCmpCave ([byte[]](
    0x8B,0x44,0x24,0x24,
    0x83,0xF8,0x04,
    0x7C,0x0E,
    0x83,0xE8,0x04,
    0x8B,0x54,0x24,0x34,
    0x0F,0xB7,0x44,0x42,0x70,
    0xEB,0x03,
    0x0F,0xB7,0x07,
    0x66,0x3B,0xC5
)) 0x005825F6

Write-JmpPatch $bytes 0x0044203B $parserCave 12
Write-JmpPatch $bytes 0x00582349 $initCave 18
Write-JmpPatch $bytes 0x0058235B $effectToEcxCave 8
Write-JmpPatch $bytes 0x005824E0 $effectToEaxPushCave 6
Write-JmpPatch $bytes 0x00582582 $valueToEdxStoreCave 7
Write-JmpPatch $bytes 0x005825F0 $valueToEaxCmpCave 6

$bytes[(Get-Offset 0x005823C3)] = 0xE8
$bytes[(Get-Offset 0x00582427)] = 0xE8
$bytes[(Get-Offset 0x0058263E)] = 0xE8

[IO.File]::WriteAllBytes($exePath, $bytes)
Write-Host "Patched six-socket item record and display logic in $exePath"
Write-Host "Backup: $backupPath"
