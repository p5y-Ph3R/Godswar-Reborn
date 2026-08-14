[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetMergeRemainingSavvy.ps1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repoRoot (
    'artifacts\pet-merge-remaining-test-' + [guid]::NewGuid().ToString('N'))
$clientExe = Join-Path $testRoot 'Origin.exe'
$backupRoot = Join-Path $testRoot 'backups'
$sourceSha256 =
    '39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B'
$patchedSha256 =
    'F8D832D97A1C910AF31645DBD8B6FC2BDADF4AD30196470553A8668DB81A1D17'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    if ($Offset + $Expected.Length -gt $Data.Length) { return $false }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Measure-ByteSequence([byte[]]$Data, [byte[]]$Sequence) {
    $matches = 0
    for ($offset = 0; $offset -le $Data.Length - $Sequence.Length;
        $offset++) {
        if ($Data[$offset] -ne $Sequence[0]) { continue }
        $equal = $true
        for ($index = 1; $index -lt $Sequence.Length; $index++) {
            if ($Data[$offset + $index] -eq $Sequence[$index]) { continue }
            $equal = $false
            break
        }
        if (-not $equal) { continue }
        $matches++
    }
    return $matches
}

function Hex([string]$Value) {
    [byte[]]$bytes = for ($index = 0; $index -lt $Value.Length;
        $index += 2) {
        [Convert]::ToByte($Value.Substring($index, 2), 16)
    }
    return ,$bytes
}

function Encode-Remaining([int]$Q, [int]$Species, [int]$Marker) {
    $threshold = if ($Species -in @(2, 3, 6, 10)) { -3990 } else { -4000 }
    $remaining = $threshold - $Q
    return -($remaining * 10 + $Marker)
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination $clientExe

    $ready = & $patcher -ClientExe $clientExe -Mode Status
    if ($ready.Status -eq 'Patched') {
        $normalized = & $patcher -ClientExe $clientExe -Mode Revert `
            -BackupRoot $backupRoot
        Assert-Equal $normalized.Status 'Reverted' `
            'patched fixture normalizes in disposable copy'
        Assert-Equal $normalized.Hash $sourceSha256 `
            'fixture normalization restores predecessor hash'
        $ready = & $patcher -ClientExe $clientExe -Mode Status
    }
    Assert-Equal $ready.Status 'Ready to apply' 'source status'
    Assert-Equal $ready.ExactRemainingSavvy $false 'source bridge state'
    Assert-Equal $ready.Hash $sourceSha256 'exact guarded predecessor hash'

    [byte[]]$source = [IO.File]::ReadAllBytes($clientExe)
    Assert-Equal (Measure-ByteSequence $source (
        [BitConverter]::GetBytes([uint32]0x009C320F))) 0 `
        'reserved cave has no pre-existing absolute xrefs'
    $apply = & $patcher -ClientExe $clientExe -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    Assert-Equal $apply.Hash $patchedSha256 'exact native successor hash'
    Assert-True ($apply.ChangedBytes -gt 0) 'apply changes guarded bytes'
    [byte[]]$patched = [IO.File]::ReadAllBytes($clientExe)
    Assert-True (Test-Bytes $patched 0x16EF44 (
        Hex 'E9C64245009090')) 'formula hook targets bridge cave'
    Assert-True (Test-Bytes $patched 0x5C320F (Hex (
        '51BA60F0FFFF2BD00FB64E3C80F902740F80F903740A' +
        '80F906740580F90A750383C20A6BD20A8D543A01F7DA' +
        '89542420595051B8C0E84900FFD0684BEF5600C3'))) `
        'bridge preserves config ECX and displaced pushes/call'
    Assert-True (Test-Bytes $patched 0x5C324F ([byte[]]::new(33))) `
        'unused remainder of 97-byte cave stays zero'

    $changedOutside = 0
    for ($offset = 0; $offset -lt $source.Length; $offset++) {
        if ($source[$offset] -eq $patched[$offset]) { continue }
        $allowed = ($offset -ge 0x16EF44 -and $offset -lt 0x16EF4B) -or
            ($offset -ge 0x5C320F -and $offset -lt 0x5C3270)
        if (-not $allowed) { $changedOutside++ }
    }
    Assert-Equal $changedOutside 0 'mutation allowlist'

    # At q=-4740, an ordinary species needs 7.40 more; a low-factor species
    # needs 7.50 because Values=1
    # truncates to zero under 0.8. Markers retain the result-row identity.
    Assert-Equal (Encode-Remaining -4740 1 2) (-7402) `
        'ordinary species encoded remaining value'
    Assert-Equal (Encode-Remaining -4740 2 2) (-7502) `
        'low-factor species encoded remaining value'
    $userQ = [math]::Floor(10438 / 5) - 15782 + 2205
    Assert-Equal $userQ (-11490) 'user example exact fixed-hundredth q'
    Assert-Equal (Encode-Remaining $userQ 1 2) (-74902) `
        'user example needs exactly 74.90 more for Rock Elf'

    $status = & $patcher -ClientExe $clientExe -Mode Status
    Assert-Equal $status.Status 'Patched' 'patched status'
    Assert-Equal $status.ExactRemainingSavvy $true 'patched bridge state'
    Assert-Equal $status.Hash $patchedSha256 'patched status hash'
    $again = & $patcher -ClientExe $clientExe -Mode Apply `
        -BackupRoot $backupRoot
    Assert-True ($again.Status -like 'Already*') 'idempotent apply'

    $partial = Join-Path $testRoot 'partial.exe'
    [IO.File]::WriteAllBytes($partial, $patched)
    $partialBytes = [IO.File]::ReadAllBytes($partial)
    $partialBytes[0x5C3210] = $partialBytes[0x5C3210] -bxor 1
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    $partialRefused = $false
    try { & $patcher -ClientExe $partial -Mode Status | Out-Null }
    catch { $partialRefused = $_.Exception.Message.Contains('partial') }
    Assert-True $partialRefused 'partial cave is refused'

    $revert = & $patcher -ClientExe $clientExe -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Equal $revert.Status 'Reverted' 'revert status'
    Assert-True ([Linq.Enumerable]::SequenceEqual(
        $source, [IO.File]::ReadAllBytes($clientExe))) `
        'revert is byte-exact'

    Write-Host "Pet Merge remaining-Savvy patch checks passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
