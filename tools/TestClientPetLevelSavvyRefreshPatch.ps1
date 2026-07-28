[CmdletBinding()]
param(
    [string]$BaseFixtureExe =
        'C:\Reborn\backups\origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25\Origin.exe',

    [string]$TimeoutFixtureExe =
        'C:\Reborn\artifacts\controlled-host-acceptance\20260728-031445-preview-ready-v5\candidate\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$baseSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$timeoutSha256 =
    'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
$basePatchedSha256 =
    '7FB43C8D6BBA42CE533EE4CB78075CA88D3D6C11F2F79224C56A8A4F50BA07F9'
$timeoutPatchedSha256 =
    '2BD6B3DD6FA9F608D0580264F1E548309F2C4F469E8CB69190CFE19083C8E0F7'
$patcher = Join-Path $PSScriptRoot `
    'PatchClientPetLevelSavvyRefresh.ps1'
$binaryHelpers = Join-Path $PSScriptRoot `
    'client_patch_helpers\AvatarPreviewGuard.Binary.ps1'
. $binaryHelpers

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'pet-level-savvy-refresh-test-' + [guid]::NewGuid().ToString('N'))
$assertionCount = 0

function Assert-Value {
    param($Actual, $Expected, [string]$Label)

    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertionCount++
}

function Assert-True {
    param([bool]$Condition, [string]$Label)

    if (-not $Condition) {
        throw "$Label failed."
    }
    $script:assertionCount++
}

function Assert-Throws {
    param(
        [scriptblock]$Operation,
        [string]$ExpectedMessage,
        [string]$Label
    )

    try {
        & $Operation
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "$Label threw an unexpected error: $($_.Exception.Message)"
        }
        $script:assertionCount++
        return
    }
    throw "Expected operation to be refused: $Label"
}

function Assert-RelativeBranch {
    param(
        [byte[]]$Code,
        [int]$Offset,
        [uint64]$CodeVa,
        [uint64]$Target,
        [string]$Label
    )

    Assert-Value $Code[$Offset] 0xE9 "$Label opcode"
    $actual = $CodeVa + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
    Assert-Value $actual $Target "$Label target"
}

function Assert-OnlyAllowedDifferences {
    param(
        [byte[]]$Before,
        [byte[]]$After,
        [object[]]$AllowedRanges,
        [int]$ExpectedCount,
        [string]$Label
    )

    $count = 0
    for ($offset = 0; $offset -lt $After.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) {
            continue
        }
        $count++
        Assert-True (
            Test-AllowedDifference $offset $AllowedRanges
        ) "$Label allowlisted offset 0x$('{0:X}' -f $offset)"
    }
    Assert-Value $count $ExpectedCount "$Label changed-byte count"
}

function Test-PetLevelSavvyRefreshFixture {
    param(
        [string]$FixtureExe,
        [string]$ExpectedBeforeHash,
        [string]$ExpectedBeforeState,
        [string]$ExpectedAfterHash,
        [string]$ExpectedAfterState,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "$Label fixture not found: $FixtureExe"
    }
    $fixturePath = (Resolve-Path -LiteralPath $FixtureExe).Path
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $ExpectedBeforeHash "$Label fixture SHA-256"

    $caseRoot = Join-Path $testRoot $Label
    $clientRoot = Join-Path $caseRoot 'client'
    $backupRoot = Join-Path $caseRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    Copy-Item -LiteralPath $fixturePath -Destination $copy
    $before = [IO.File]::ReadAllBytes($copy)

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State $ExpectedBeforeState "$Label initial state"
    Assert-Value $status.Status 'Ready to apply' "$Label initial status"
    Assert-Value $status.LegacyPacketLength 20 "$Label legacy length"
    Assert-Value $status.ExtendedPacketLength 44 "$Label extended length"
    Assert-True (
        -not (Test-Path -LiteralPath $backupRoot)
    ) "$Label Status creates no backup"

    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $apply.State $ExpectedAfterState "$Label applied state"
    Assert-Value $apply.ChangedBytes 44 "$Label mutation count"
    Assert-Value $apply.AfterSha256 $ExpectedAfterHash `
        "$Label result hash"
    Assert-Value $apply.SavvyFieldCount 6 "$Label savvy count"
    Assert-Value (
        Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256
    ).Hash $ExpectedBeforeHash "$Label apply backup hash"

    $after = [IO.File]::ReadAllBytes($copy)
    Assert-OnlyAllowedDifferences $before $after @(
        [pscustomobject]@{ Offset = 0x2A195C; Length = 11 },
        [pscustomobject]@{ Offset = 0x5C3480; Length = 64 }
    ) 44 "$Label apply"
    Assert-Value (
        Get-FileHash -LiteralPath $copy -Algorithm SHA256
    ).Hash $ExpectedAfterHash "$Label installed hash"

    $expectedHook = Convert-HexBytes @'
E9 1F 1B 32 00 90 90 90 90 90 90
'@
    $expectedCaveCode = Convert-HexBytes @'
9C
66 83 3E 2C
75 16
51 56 57
83 C6 14
81 C7 84 00 00 00
B9 06 00 00 00
F3 A5
5F 5E 59
9D
C7 84 24 E4 00 00 00 07 00 00 00
E9 B9 E4 CD FF
'@
    Assert-True (
        Test-Bytes $after 0x2A195C $expectedHook
    ) "$Label exact hook"
    Assert-True (
        Test-Bytes $after 0x5C3480 $expectedCaveCode
    ) "$Label exact cave"
    Assert-True (
        Test-Bytes $after (0x5C3480 + $expectedCaveCode.Length) (
            [byte[]]::new(64 - $expectedCaveCode.Length))
    ) "$Label unused cave remains zero"

    Assert-RelativeBranch $expectedHook 0 0x006A195C 0x009C3480 `
        "$Label hook"
    Assert-RelativeBranch $expectedCaveCode 41 0x009C3480 0x006A1967 `
        "$Label continuation"

    # Exact 44-byte packet gate: pushfd; cmp word [esi], 44; jne restore.
    Assert-True (
        Test-Bytes $expectedCaveCode 0 (
            Convert-HexBytes '9C 66 83 3E 2C 75 16')
    ) "$Label exact-length gate"
    Assert-Value (
        5 + 2 + [int][sbyte]$expectedCaveCode[6]
    ) 29 "$Label legacy restore branch"

    # Save ECX/ESI/EDI, source packet+20, destination pet+0x84, six dwords.
    Assert-True (
        Test-Bytes $expectedCaveCode 7 (
            Convert-HexBytes @'
51 56 57 83 C6 14 81 C7 84 00 00 00 B9 06 00 00 00 F3 A5
'@)
    ) "$Label six-field copy"
    Assert-True (
        Test-Bytes $expectedCaveCode 26 (
            Convert-HexBytes '5F 5E 59 9D')
    ) "$Label register and flags restore"
    Assert-True (
        Test-Bytes $expectedCaveCode 30 (
            Convert-HexBytes 'C7 84 24 E4 00 00 00 07 00 00 00')
    ) "$Label displaced instruction replay"

    # The native 10286 prefix and continuation remain byte-identical.
    Assert-True (
        Test-Bytes $after 0x2A194A (
            $before[0x2A194A..0x2A195B])
    ) "$Label native level/EXP prefix untouched"
    Assert-True (
        Test-Bytes $after 0x2A1967 (
            $before[0x2A1967..(0x2A1967 + 63)])
    ) "$Label stock continuation untouched"

    $backupCount = @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count
    $idempotentApply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $idempotentApply.Status 'Already patched' `
        "$Label idempotent Apply"
    Assert-Value @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count $backupCount "$Label idempotent Apply backup count"

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.State $ExpectedBeforeState "$Label reverted state"
    Assert-Value $revert.ChangedBytes 44 "$Label revert mutation count"
    Assert-Value $revert.AfterSha256 $ExpectedBeforeHash `
        "$Label revert result hash"
    Assert-Value (
        Get-FileHash -LiteralPath $revert.Backup -Algorithm SHA256
    ).Hash $ExpectedAfterHash "$Label revert backup hash"
    Assert-True (
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$before,
            [byte[]][IO.File]::ReadAllBytes($copy))
    ) "$Label exact apply/revert roundtrip"

    $idempotentRevert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $idempotentRevert.Status 'Already reverted' `
        "$Label idempotent Revert"
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $ExpectedBeforeHash "$Label source fixture untouched"
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null

    Test-PetLevelSavvyRefreshFixture `
        -FixtureExe $BaseFixtureExe `
        -ExpectedBeforeHash $baseSha256 `
        -ExpectedBeforeState 'AuditedBase' `
        -ExpectedAfterHash $basePatchedSha256 `
        -ExpectedAfterState 'PetLevelSavvyRefreshPatched' `
        -Label 'base'
    Test-PetLevelSavvyRefreshFixture `
        -FixtureExe $TimeoutFixtureExe `
        -ExpectedBeforeHash $timeoutSha256 `
        -ExpectedBeforeState 'TimeoutRetryGuardPatched' `
        -ExpectedAfterHash $timeoutPatchedSha256 `
        -ExpectedAfterState 'TimeoutAndPetLevelSavvyRefreshPatched' `
        -Label 'timeout'

    $foreign = Join-Path $testRoot 'ForeignOrigin.exe'
    Copy-Item -LiteralPath $BaseFixtureExe -Destination $foreign
    $foreignBytes = [IO.File]::ReadAllBytes($foreign)
    $foreignBytes[$foreignBytes.Length - 1] =
        $foreignBytes[$foreignBytes.Length - 1] -bxor 0xFF
    [IO.File]::WriteAllBytes($foreign, $foreignBytes)
    Assert-Throws {
        & $patcher -ClientExe $foreign -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'Foreign hash conflict'

    $partial = Join-Path $testRoot 'PartialOrigin.exe'
    Copy-Item -LiteralPath $BaseFixtureExe -Destination $partial
    $partialBytes = [IO.File]::ReadAllBytes($partial)
    Copy-Bytes (
        Convert-HexBytes 'E9 1F 1B 32 00 90 90 90 90 90 90'
    ) $partialBytes 0x2A195C
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $partial -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'Partial patch conflict'

    Write-Host "All $assertionCount pet-level savvy-refresh assertions passed."
}
finally {
    $resolvedArtifactRoot =
        [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
