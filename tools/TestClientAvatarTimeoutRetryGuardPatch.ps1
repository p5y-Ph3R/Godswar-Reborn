[CmdletBinding()]
param(
    [string]$FixtureExe =
        'C:\Reborn\backups\origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$priorSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$patchedSha256 =
    'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
$patcher = Join-Path $PSScriptRoot `
    'PatchClientAvatarTimeoutRetryGuard.ps1'
$binaryHelpers = Join-Path $PSScriptRoot `
    'client_patch_helpers\AvatarPreviewGuard.Binary.ps1'
. $binaryHelpers

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'avatar-timeout-retry-guard-test-' + [guid]::NewGuid().ToString('N'))
$runningProbe = $null
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

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Avatar timeout/retry fixture not found: $FixtureExe"
    }
    $fixturePath = (Resolve-Path -LiteralPath $FixtureExe).Path
    Assert-True (
        -not $fixturePath.StartsWith(
            'C:\RebornNetworkAcceptanceClient\',
            [StringComparison]::OrdinalIgnoreCase)
    ) 'Fixture is not the live acceptance client'
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $priorSha256 'Fixture SHA-256'

    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $clientRoot = Join-Path $testRoot 'client'
    $backupRoot = Join-Path $testRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    Copy-Item -LiteralPath $fixturePath -Destination $copy
    $before = [IO.File]::ReadAllBytes($copy)

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State 'AuditedPredecessor' 'Initial state'
    Assert-Value $status.Sha256 $priorSha256 'Initial status hash'
    Assert-True (
        -not (Test-Path -LiteralPath $backupRoot)
    ) 'Status creates no backup'

    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $apply.State 'TimeoutRetryGuardPatched' 'Applied state'
    Assert-Value $apply.ChangedBytes 64 'Apply mutation count'
    Assert-Value $apply.GuardedRoots 2 'Exact guarded dereference count'
    Assert-Value $apply.AfterSha256 $patchedSha256 'Apply result hash'
    Assert-Value (
        Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256
    ).Hash $priorSha256 'Apply backup hash'

    $after = [IO.File]::ReadAllBytes($copy)
    Assert-OnlyAllowedDifferences $before $after @(
        [pscustomobject]@{ Offset = 0x1F58B6; Length = 6 },
        [pscustomobject]@{ Offset = 0x5C341F; Length = 96 }
    ) 64 'Apply'
    Assert-Value (
        Get-FileHash -LiteralPath $copy -Algorithm SHA256
    ).Hash $patchedSha256 'Installed hash'

    $expectedHook = Convert-HexBytes 'E9 64 DB 3C 00 90'
    $expectedCaveCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12
A1 A0 60 57 01 85 C0 74 14
A1 8C 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 77 24 C3 FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 8E 24 C3 FF
'@
    Assert-True (
        Test-Bytes $after 0x1F58B6 $expectedHook
    ) 'Exact patched hook bytes'
    Assert-True (
        Test-Bytes $after 0x5C341F $expectedCaveCode
    ) 'Exact two-dereference cave bytes'
    Assert-True (
        Test-Bytes $after (0x5C341F + $expectedCaveCode.Length) (
            [byte[]]::new(96 - $expectedCaveCode.Length))
    ) 'Unused cave reserve remains zero'

    Assert-RelativeBranch $expectedHook 0 0x005F58B6 0x009C341F `
        'Hook'
    Assert-RelativeBranch $expectedCaveCode 0x21 0x009C341F `
        0x005F58BC 'Both-ready branch'
    Assert-RelativeBranch $expectedCaveCode 0x38 0x009C341F `
        0x005F58EA 'Missing-root branch'

    $rootAddresses = @(
        0x015760A0,
        0x0157608C
    )
    $rootOffsets = @(0x09, 0x12)
    $shortBranches = @(0x10, 0x19)
    for ($rootIndex = 0; $rootIndex -lt $rootAddresses.Count;
        $rootIndex++) {
        $checkOffset = $rootOffsets[$rootIndex]
        Assert-Value $expectedCaveCode[$checkOffset] 0xA1 `
            "Root $rootIndex load opcode"
        Assert-Value (
            [BitConverter]::ToUInt32($expectedCaveCode, $checkOffset + 1)
        ) $rootAddresses[$rootIndex] "Root $rootIndex address"
        $branchOffset = $shortBranches[$rootIndex]
        Assert-Value $expectedCaveCode[$branchOffset] 0x74 `
            "Root $rootIndex null branch opcode"
        Assert-Value (
            $branchOffset + 2 +
                [int][sbyte]$expectedCaveCode[$branchOffset + 1]
        ) 0x26 "Root $rootIndex missing-path target"
    }

    Assert-Value $expectedCaveCode[0] 0x83 'Lifecycle compare opcode'
    Assert-Value (
        [BitConverter]::ToUInt32($expectedCaveCode, 2)
    ) 0x01575F4C 'Lifecycle current-state address'
    Assert-Value $expectedCaveCode[6] 2 'LOGIN lifecycle state'
    Assert-Value $expectedCaveCode[7] 0x75 'Lifecycle mismatch opcode'
    Assert-Value (
        7 + 2 + [int][sbyte]$expectedCaveCode[8]
    ) 0x1B 'Lifecycle mismatch stock target'

    # Lifecycle mismatch and both-ready state replay the exact displaced
    # instruction, then enter the byte-identical stock continuation.
    $originalHook = Convert-HexBytes '8B 0D A0 60 57 01'
    Assert-True (
        Test-Bytes $expectedCaveCode 0x1B $originalHook
    ) 'Both-ready displaced instruction replay'
    Assert-True (
        Test-Bytes $after 0x1F58BC (
            $before[0x1F58BC..(0x1F58BC + 63)])
    ) 'All-ready stock continuation remains byte-identical'

    # Missing state retains the three native state-2 side effects but omits the
    # unsafe virtual calls before rejoining native cleanup.
    Assert-True (
        Test-Bytes $expectedCaveCode 0x26 (
            $before[0x1F58C4..0x1F58C8])
    ) 'Missing path preserves EDI state 2'
    Assert-True (
        Test-Bytes $expectedCaveCode 0x2B (
            $before[0x1F58CA..0x1F58D0])
    ) 'Missing path preserves retry flag'
    Assert-True (
        Test-Bytes $expectedCaveCode 0x32 (
            $before[0x1F58D1..0x1F58D6])
    ) 'Missing path preserves state write'
    Assert-True (
        Test-Bytes $after 0x1F58EA (
            $before[0x1F58EA..(0x1F58EA + 31)])
    ) 'Missing-root cleanup continuation remains byte-identical'

    # The rejected synchronous preload hook and its cave are not part of this
    # candidate.
    Assert-True (
        Test-Bytes $after 0x0C14D6 (
            Convert-HexBytes '68 A0 39 95 00')
    ) 'Rejected preload hook remains absent'
    Assert-True (
        Test-Bytes $after 0x5C3366 ([byte[]]::new(154))
    ) 'Rejected preload cave remains empty'

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State 'TimeoutRetryGuardPatched' `
        'Patched status state'
    $backupCount = @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count
    $idempotentApply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $idempotentApply.Status 'Already patched' `
        'Idempotent Apply'
    Assert-Value @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count $backupCount 'Idempotent Apply backup count'

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.State 'AuditedPredecessor' 'Reverted state'
    Assert-Value $revert.ChangedBytes 64 'Revert mutation count'
    Assert-Value $revert.AfterSha256 $priorSha256 'Revert result hash'
    Assert-Value (
        Get-FileHash -LiteralPath $revert.Backup -Algorithm SHA256
    ).Hash $patchedSha256 'Revert backup hash'
    Assert-True (
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$before,
            [byte[]][IO.File]::ReadAllBytes($copy))
    ) 'Apply/Revert exact byte roundtrip'

    $backupCount = @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count
    $idempotentRevert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $idempotentRevert.Status 'Already reverted' `
        'Idempotent Revert'
    Assert-Value @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count $backupCount 'Idempotent Revert backup count'
    Assert-Value @(
        Get-ChildItem -LiteralPath $clientRoot -File |
            Where-Object Name -ne 'Origin.exe'
    ).Count 0 'Transactional temporary-file cleanup'

    $foreign = Join-Path $testRoot 'ForeignOrigin.exe'
    Copy-Item -LiteralPath $fixturePath -Destination $foreign
    $foreignBytes = [IO.File]::ReadAllBytes($foreign)
    $foreignBytes[$foreignBytes.Length - 1] = [byte](
        $foreignBytes[$foreignBytes.Length - 1] -bxor 0xFF)
    [IO.File]::WriteAllBytes($foreign, $foreignBytes)
    Assert-Throws {
        & $patcher -ClientExe $foreign -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'Foreign hash conflict'

    $partial = Join-Path $testRoot 'PartialOrigin.exe'
    Copy-Item -LiteralPath $fixturePath -Destination $partial
    $partialBytes = [IO.File]::ReadAllBytes($partial)
    Copy-Bytes $expectedHook $partialBytes 0x1F58B6
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $partial -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'Partial patch conflict'

    $runningRoot = Join-Path $testRoot 'running'
    [IO.Directory]::CreateDirectory($runningRoot) | Out-Null
    $runningExe = Join-Path $runningRoot 'Origin.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\ping.exe" `
        -Destination $runningExe
    $runningProbe = Start-Process -FilePath $runningExe `
        -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 250
    Assert-Throws {
        & $patcher -ClientExe $runningExe -Mode Apply | Out-Null
    } 'is running' 'Running executable guard'

    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $priorSha256 'Source fixture remains untouched'

    Write-Host "All $assertionCount avatar timeout/retry guard assertions passed."
}
finally {
    if ($runningProbe -and -not $runningProbe.HasExited) {
        Stop-Process -Id $runningProbe.Id -Force
        $runningProbe.WaitForExit()
    }

    $resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
