$ErrorActionPreference = 'Stop'

$clientDir = 'C:\Godswar Origin'
$targets = @(
    (Join-Path $clientDir 'Origin_sixsocket.exe'),
    (Join-Path $clientDir 'Origin.exe')
)
$backupDir = Join-Path $PSScriptRoot '..\backups\six-socket-client-20260516-165022'
$imageBase = 0x400000
$patchVa = 0x0058055E
$caveVa = 0x009C3230

function Get-Offset([int]$va) {
    return $va - $imageBase
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

function New-Call([int]$fromVa, [int]$toVa) {
    $rel = [int]($toVa - ($fromVa + 5))
    return [byte[]](0xE8) + [BitConverter]::GetBytes($rel)
}

function Write-Patch([byte[]]$bytes, [int]$va, [byte[]]$patch) {
    $patch.CopyTo($bytes, (Get-Offset $va))
}

if (Get-Process Origin,Origin_sixsocket -ErrorAction SilentlyContinue) {
    throw 'Origin client is running. Close the client before patching the executable.'
}

New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        continue
    }

    $backupPath = Join-Path $backupDir ((Split-Path -Leaf $target) + '.pre-sixsocket-layout-cap.exe')
    if (-not (Test-Path -LiteralPath $backupPath)) {
        Copy-Item -LiteralPath $target -Destination $backupPath -Force
    }

    $bytes = [IO.File]::ReadAllBytes($target)
    Assert-Bytes $bytes $patchVa ([byte[]](0x0F,0xB7,0x43,0x52,0x50,0xE8,0x88,0x2D,0x00,0x00)) 'holy-stone layout count call'
    Assert-ZeroRange $bytes $caveVa 0x40 'holy-stone layout cap'

    $body = [byte[]](
        0x0F,0xB7,0x43,0x52,
        0x83,0xF8,0x04,
        0x7E,0x05,
        0xB8,0x04,0x00,0x00,0x00,
        0x50
    )
    $body += New-Call ($caveVa + $body.Length) 0x005832F0
    $body += New-Jmp ($caveVa + $body.Length) 0x00580568
    Write-Patch $bytes $caveVa $body

    $jump = New-Jmp $patchVa $caveVa
    $jump += [byte[]](,0x90 * 5)
    Write-Patch $bytes $patchVa $jump

    [IO.File]::WriteAllBytes($target, $bytes)
    Write-Host "Patched six-socket layout cap in $target"
    Write-Host "Backup: $backupPath"
}
