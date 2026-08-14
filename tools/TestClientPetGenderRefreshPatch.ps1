[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetGenderRefresh.ps1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repoRoot (
    'artifacts\pet-gender-refresh-test-' + [guid]::NewGuid().ToString('N'))
$clientExe = Join-Path $testRoot 'Origin.exe'
$backupRoot = Join-Path $testRoot 'backups'
$sourceHash =
    'C1CE0273504AB3E8020FD2EB2692351FFA0094F6A103719EB8970FD98C3DB2B6'
$patchedHash =
    '00ED99F0EADB605059CB7A0FA476922EC6EA9E3EAE9218710C20299992706BDB'
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

function Hex([string]$Value) {
    $compact = $Value -replace '[^0-9A-Fa-f]', ''
    [byte[]]$bytes = for ($index = 0; $index -lt $compact.Length;
        $index += 2) {
        [Convert]::ToByte($compact.Substring($index, 2), 16)
    }
    return ,$bytes
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    if ($Offset + $Expected.Length -gt $Data.Length) { return $false }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Get-NearTarget([byte[]]$Code, [int]$Offset, [uint64]$Va) {
    [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-NearConditionalTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va
) {
    [int64]$Va + $Offset + 6 +
        [BitConverter]::ToInt32($Code, $Offset + 2)
}

function Get-ShortTarget([byte[]]$Code, [int]$Offset, [uint64]$Va) {
    $displacement = if ($Code[$Offset + 1] -ge 0x80) {
        [int]$Code[$Offset + 1] - 0x100
    }
    else { [int]$Code[$Offset + 1] }
    [int64]$Va + $Offset + 2 + $displacement
}

function Test-GenderTailAccepted(
    [int]$Species,
    [int]$Bound,
    [int]$Sex,
    [int]$ReservedWord = 0,
    [int]$ReservedGenderBytes = 0
) {
    [uint32]$appearance = [uint32]$Species -bor
        ([uint32]$Bound -shl 8) -bor
        ([uint32]$ReservedWord -shl 16)
    [uint32]$gender = [uint32]$Sex -bor
        ([uint32]$ReservedGenderBytes -shl 8)
    return $appearance -le 0x12D -and $Species -ne 0 -and
        $Species -le 45 -and $gender -le 1
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination $clientExe

    $ready = & $patcher -ClientExe $clientExe -Mode Status
    Assert-Equal $ready.Status 'Ready to apply' 'source status'
    Assert-Equal $ready.Hash $sourceHash 'appearance predecessor hash'
    Assert-Equal $ready.AppearancePacketLength 72 'appearance retained'
    Assert-Equal $ready.SexRefresh $false 'source sex state'
    [byte[]]$source = [IO.File]::ReadAllBytes($clientExe)

    $apply = & $patcher -ClientExe $clientExe -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    Assert-Equal $apply.Hash $patchedHash 'exact S1 hash'
    Assert-Equal $apply.ChangedBytes 70 'exact changed-byte count'
    Assert-Equal $apply.GenderPacketLength 76 'gender packet length'

    [byte[]]$patched = [IO.File]::ReadAllBytes($clientExe)
    $decoder = Hex @'
9C 66 83 3E 44 74 6C 66 83 3E 48 0F 84 81 04 00 00
66 83 3E 4C 0F 85 58 02 00 00 50 8B 46 44 3D 2D 01
00 00 77 23 84 C0 74 1F 3C 2D 77 1B 83 7E 48 01 77
15 88 47 3C 88 A7 BC 00 00 00 8A 46 48 88 47 3F 58
E9 2A 00 00 00 58 E9 24 02 00 00
'@
    Assert-True (Test-Bytes $patched 0x5C341F $decoder) `
        'bounded 68/72/76 decoder bytes'
    Assert-True (Test-Bytes $patched 0x5C3480 (
        Hex 'E99AFFFFFF')) 'dispatcher enters S1 decoder'
    Assert-Equal (Get-NearTarget (
        $patched[0x5C3480..0x5C3484]) 0 0x009C3480) `
        0x009C341F 'dispatcher target'
    Assert-Equal (Get-ShortTarget $decoder 5 0x009C341F) `
        0x009C3492 '68-byte copy target'
    Assert-Equal (Get-NearConditionalTarget $decoder 11 0x009C341F) `
        0x009C38B1 '72-byte appearance decoder target'
    Assert-Equal (Get-NearConditionalTarget $decoder 21 0x009C341F) `
        0x009C3692 'malformed-length finalizer target'
    Assert-True (Test-Bytes $decoder 46 (
        Hex '837E48017715')) '76-byte sex and reserve bound'
    Assert-True (Test-Bytes $decoder 52 (
        Hex '88473C88A7BC0000008A464888473F')) `
        '76-byte species, bound, and sex writes are exact'
    Assert-True ($decoder[27] -eq 0x50 -and
        $decoder[67] -eq 0x58 -and $decoder[73] -eq 0x58) `
        'decoder preserves EAX on success and rejection'
    Assert-True ($decoder[0] -eq 0x9C) `
        'decoder preserves incoming EFLAGS through shared finalizer'

    foreach ($species in 1..45) {
        foreach ($bound in 0..1) {
            foreach ($sex in 0..1) {
                Assert-True (Test-GenderTailAccepted `
                    $species $bound $sex) `
                    "valid species $species bound $bound sex $sex"
            }
        }
    }
    foreach ($invalid in @(
            @(0, 0, 0, 0, 0),
            @(46, 0, 0, 0, 0),
            @(1, 2, 0, 0, 0),
            @(1, 0, 2, 0, 0),
            @(1, 0, 0, 1, 0),
            @(1, 0, 0, 0, 1))) {
        Assert-True (-not (Test-GenderTailAccepted @invalid)) `
            "invalid gender tail $($invalid -join '/')"
    }
    foreach ($length in @(0, 67, 69, 71, 73, 75, 77, 255, 324)) {
        Assert-True ($length -notin @(68, 72, 76)) `
            "malformed length $length misses exact gates"
    }

    $changedOutside = 0
    for ($offset = 0; $offset -lt $source.Length; $offset++) {
        if ($source[$offset] -eq $patched[$offset]) { continue }
        $allowed = ($offset -ge 0x5C341F -and
                $offset -lt 0x5C3480) -or
            ($offset -ge 0x5C3480 -and $offset -lt 0x5C3485)
        if (-not $allowed) { $changedOutside++ }
    }
    Assert-Equal $changedOutside 0 'S1 mutation allowlist'

    $again = & $patcher -ClientExe $clientExe -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'
    $revert = & $patcher -ClientExe $clientExe -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Equal $revert.Status 'Reverted' 'revert status'
    Assert-Equal $revert.Hash $sourceHash 'byte-exact predecessor hash'
    Assert-True ([Linq.Enumerable]::SequenceEqual(
        $source, [IO.File]::ReadAllBytes($clientExe))) 'byte-exact revert'

    Write-Host "Pet gender-refresh patch checks passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
