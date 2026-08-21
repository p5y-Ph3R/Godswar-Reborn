[CmdletBinding()]
param(
    [string]$FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientManualRealmSelection.ps1'
$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'RealmComposite.States.ps1')
. (Join-Path $helperRoot 'RealmComposite.TestFixtures.ps1')
. (Join-Path $helperRoot 'ManualRealmSelection.Patch.ps1')

$stateMap = Get-RealmCompositeStateMap
$baseState = Get-RealmCompositeState $stateMap $false $false $false
$manualState = Get-RealmCompositeState $stateMap $true $false $false
$guardState = Get-RealmCompositeState $stateMap $false $true $false
$combinedState = Get-RealmCompositeState $stateMap $true $true $false
$octagramState = Get-RealmCompositeState $stateMap $false $false $true
$octagramManualState = Get-RealmCompositeState $stateMap $true $false $true
$octagramGuardState = Get-RealmCompositeState $stateMap $false $true $true
$octagramCombinedState = Get-RealmCompositeState $stateMap $true $true $true
$expectedBaseHash = $baseState.Hash
$expectedPatchedHash = $manualState.Hash
$expectedGuardHash = $guardState.Hash

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'manual-realm-selection-test-' + [guid]::NewGuid().ToString('N'))
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

function Assert-RelativeCall {
    param(
        [byte[]]$Code,
        [int]$Offset,
        [uint64]$CodeVa,
        [uint64]$Target,
        [string]$Label
    )

    Assert-Value $Code[$Offset] 0xE8 "$Label opcode"
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

    Assert-Value $After.Length $Before.Length "$Label file length"
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

function Invoke-ManualPlaneRoundTrip {
    param(
        [string]$Name,
        [byte[]]$SourceBytes,
        [object]$SourceState,
        [object]$PatchedState
    )

    $copy = Join-Path $testRoot "$Name-Origin.exe"
    $variantBackups = Join-Path $testRoot "$Name-backups"
    [IO.File]::WriteAllBytes($copy, $SourceBytes)
    $octagramStatus = Get-RealmCompositeOctagramStatus $SourceState
    $guardStatus = if ($SourceState.GuardPatched) { 'Patched' } else { 'Original' }

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.Status 'Ready to apply' "$Name source status"
    Assert-Value $status.State $SourceState.Name "$Name source state"
    Assert-Value $status.Sha256 $SourceState.Hash "$Name source hash"
    Assert-Value $status.PetOwnerMergeOctagram $octagramStatus `
        "$Name source octagram state"
    Assert-Value $status.CharacterBackGuard $guardStatus `
        "$Name source Back-guard state"

    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $variantBackups
    Assert-Value $apply.Status 'Patched' "$Name Apply status"
    Assert-Value $apply.State $PatchedState.Name "$Name Apply state"
    Assert-Value $apply.BeforeSha256 $SourceState.Hash `
        "$Name Apply source hash"
    Assert-Value $apply.AfterSha256 $PatchedState.Hash `
        "$Name Apply target hash"
    Assert-Value $apply.PetOwnerMergeOctagram $octagramStatus `
        "$Name Apply preserves octagram state"
    Assert-Value $apply.ChangedBytes 5 "$Name Apply mutation count"
    $after = [IO.File]::ReadAllBytes($copy)
    Assert-OnlyAllowedDifferences $SourceBytes $after @(
        [pscustomobject]@{ Offset = 0x1F9A19; Length = 5 }
    ) 5 "$Name Apply"
    $patchedStatus = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $patchedStatus.Status 'Patched' "$Name patched Status"
    Assert-Value $patchedStatus.State $PatchedState.Name `
        "$Name patched Status state"
    Assert-Value $patchedStatus.PetOwnerMergeOctagram $octagramStatus `
        "$Name patched Status preserves octagram state"
    $idempotentApply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $variantBackups
    Assert-Value $idempotentApply.Status 'Already patched' `
        "$Name idempotent Apply"

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $variantBackups
    Assert-Value $revert.Status 'Reverted' "$Name Revert status"
    Assert-Value $revert.State $SourceState.Name "$Name Revert state"
    Assert-Value $revert.AfterSha256 $SourceState.Hash `
        "$Name Revert hash"
    Assert-Value $revert.PetOwnerMergeOctagram $octagramStatus `
        "$Name Revert preserves octagram state"
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            [byte[]]$SourceBytes,
            [byte[]][IO.File]::ReadAllBytes($copy))) `
        "$Name exact roundtrip"
    $idempotentRevert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $variantBackups
    Assert-Value $idempotentRevert.Status 'Already reverted' `
        "$Name idempotent Revert"
}

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Fixture not found: $FixtureExe"
    }
    $fixturePath = (Resolve-Path -LiteralPath $FixtureExe).Path
    $fixtureHash = (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash
    Assert-True ($stateMap.ContainsKey($fixtureHash)) `
        'source fixture is an exact supported state'

    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $clientRoot = Join-Path $testRoot 'client'
    $backupRoot = Join-Path $testRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    $fixtureBytes = [IO.File]::ReadAllBytes($fixturePath)
    $before = New-RealmCompositeFixture $fixtureBytes $false $false $false
    [IO.File]::WriteAllBytes($copy, $before)
    Assert-Value (
        Get-ManualRealmSelectionSha256 $before
    ) $expectedBaseHash 'normalized base SHA-256'

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.Status 'Ready to apply' 'initial status'
    Assert-Value $status.State 'CurrentComposite' 'initial state'
    Assert-Value $status.PetOwnerMergeOctagram 'Reverted' `
        'initial octagram state'
    Assert-Value $status.HookFileOffset '0x1F9A19' 'reported hook offset'
    Assert-Value $status.HookVa '0x005F9A19' 'reported hook VA'
    Assert-Value $status.ManualCallVa '0x005F699A' `
        'reported manual call VA'
    Assert-Value $status.CharacterBackGuard 'Original' `
        'reported original Back guard state'
    Assert-True (
        -not (Test-Path -LiteralPath $backupRoot)
    ) 'Status creates no backup'

    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $apply.Status 'Patched' 'apply status'
    Assert-Value $apply.State 'ManualRealmSelectionPatched' `
        'applied state'
    Assert-Value $apply.PetOwnerMergeOctagram 'Reverted' `
        'Apply preserves reverted octagram state'
    Assert-Value $apply.ChangedBytes 5 'apply mutation count'
    Assert-Value $apply.AfterSha256 $expectedPatchedHash 'apply hash'
    Assert-Value (
        Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256
    ).Hash $expectedBaseHash 'apply backup hash'

    $after = [IO.File]::ReadAllBytes($copy)
    Assert-Value (
        Get-ManualRealmSelectionSha256 $after
    ) $expectedPatchedHash 'installed patched hash'
    Assert-OnlyAllowedDifferences $before $after @(
        [pscustomobject]@{ Offset = 0x1F9A19; Length = 5 }
    ) 5 'apply'

    $originalAutoCall = Convert-HexBytes 'E8 92 23 00 00'
    $manualPath = Convert-HexBytes @'
8B CF E8 11 54 00 00 E9 18 01 00 00
'@
    Assert-True (
        Test-Bytes $before 0x1F9A19 $originalAutoCall
    ) 'base contains automatic selection call'
    Assert-RelativeCall $originalAutoCall 0 0x005F9A19 `
        0x005FBDB0 'automatic selection'
    Assert-True (
        Test-Bytes $after 0x1F9A19 (
            Convert-HexBytes '90 90 90 90 90')
    ) 'automatic selection call is suppressed'
    Assert-True (
        Test-Bytes $after 0x1F6998 $manualPath
    ) 'manual Enter Game path remains exact'
    Assert-RelativeCall $manualPath 2 0x005F6998 `
        0x005FBDB0 'manual Enter Game selection'

    $serverListDispatch = Convert-HexBytes 'E8 F7 EF 10 00'
    Assert-True (
        Test-Bytes $after 0x0EA8E4 $serverListDispatch
    ) 'terminal server-list dispatcher remains native'
    Assert-RelativeCall $serverListDispatch 0 0x004EA8E4 `
        0x005F98E0 'terminal server-list dispatch'
    Assert-True (
        Test-Bytes $after 0x1F990D (
            Convert-HexBytes @'
80 B8 58 02 00 00 00 0F 85 04 01 00 00
'@)
    ) 'Back return auto-selection gate remains native'

    $lastSelectionPath = Convert-HexBytes @'
6A 00 57 E8 AE 2F 00 00
'@
    Assert-True (
        Test-Bytes $after 0x1F991A $lastSelectionPath
    ) 'saved-server lookup remains native'
    Assert-RelativeCall $lastSelectionPath 3 0x005F991A `
        0x005FC8D0 'saved-server lookup'
    Assert-True (
        Test-Bytes $after 0x1FBF25 (
            Convert-HexBytes @'
66 C7 44 24 30 2C 00 66 C7 44 24 32 04 00
'@)
    ) 'manual selection still builds opcode 4'
    Assert-True (
        Test-Bytes $after 0x1FC31E (
            Convert-HexBytes @'
66 C7 44 24 54 5C 00 66 C7 44 24 56 06 00
'@)
    ) 'post-selection flow still builds opcode 6'

    $pe = Get-PeMetadata $after
    $mapping = Resolve-ExecutableFileRange $pe 0x1F9A19 5
    Assert-Value $mapping.Section '.text' 'hook executable section'
    Assert-Value $mapping.Va 0x005F9A19 'hook PE VA'

    $backupCount = @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count
    $idempotentApply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $idempotentApply.Status 'Already patched' `
        'idempotent Apply'
    Assert-Value @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count $backupCount 'idempotent Apply creates no backup'
    $patchedStatus = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $patchedStatus.Status 'Patched' 'patched Status'

    $partial = Join-Path $testRoot 'PartialOrigin.exe'
    $partialBytes = [byte[]]$before.Clone()
    $partialBytes[0x1F9A19] = 0x90
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $partial -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'partial patch conflict'

    $manualTamper = Join-Path $testRoot 'ManualTamperOrigin.exe'
    $manualTamperBytes = [byte[]]$before.Clone()
    $manualTamperBytes[0x1F699A] = 0x90
    [IO.File]::WriteAllBytes($manualTamper, $manualTamperBytes)
    Assert-Throws {
        & $patcher -ClientExe $manualTamper -Mode Status | Out-Null
    } 'native prerequisites' 'manual-call tamper conflict'

    $foreign = Join-Path $testRoot 'ForeignOrigin.exe'
    $foreignBytes = [byte[]]$before.Clone()
    $foreignBytes[$foreignBytes.Length - 1] =
        $foreignBytes[$foreignBytes.Length - 1] -bxor 0xFF
    [IO.File]::WriteAllBytes($foreign, $foreignBytes)
    Assert-Throws {
        & $patcher -ClientExe $foreign -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'foreign hash conflict'

    # Exercise the mutation guard without launching Origin.exe. This process
    # necessarily has its own exact executable path open.
    $currentProcessPath = (Get-Process -Id $PID).Path
    Assert-Throws {
        Assert-ManualRealmSelectionProcessClosed $currentProcessPath
    } 'is running' 'running-process mutation guard'

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.Status 'Reverted' 'revert status'
    Assert-Value $revert.State 'CurrentComposite' 'reverted state'
    Assert-Value $revert.PetOwnerMergeOctagram 'Reverted' `
        'Revert preserves reverted octagram state'
    Assert-Value $revert.ChangedBytes 5 'revert mutation count'
    Assert-Value $revert.AfterSha256 $expectedBaseHash 'revert hash'
    Assert-Value (
        Get-FileHash -LiteralPath $revert.Backup -Algorithm SHA256
    ).Hash $expectedPatchedHash 'revert backup hash'
    Assert-True (
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$before,
            [byte[]][IO.File]::ReadAllBytes($copy))
    ) 'exact apply/revert rollback roundtrip'

    $idempotentRevert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $idempotentRevert.Status 'Already reverted' `
        'idempotent Revert'

    $guardBytes = New-RealmCompositeFixture `
        $fixtureBytes $false $true $false
    Assert-Value (Get-ManualRealmSelectionSha256 $guardBytes) `
        $expectedGuardHash 'synthesized guard-only fixture hash'
    Invoke-ManualPlaneRoundTrip 'guard' $guardBytes `
        $guardState $combinedState

    $octagramBytes = New-RealmCompositeFixture `
        $fixtureBytes $false $false $true
    Assert-Value (Get-ManualRealmSelectionSha256 $octagramBytes) `
        $octagramState.Hash 'synthesized octagram fixture hash'
    Invoke-ManualPlaneRoundTrip 'octagram' $octagramBytes `
        $octagramState $octagramManualState

    $octagramGuardBytes = New-RealmCompositeFixture `
        $fixtureBytes $false $true $true
    Assert-Value (Get-ManualRealmSelectionSha256 $octagramGuardBytes) `
        $octagramGuardState.Hash 'synthesized octagram+guard fixture hash'
    Invoke-ManualPlaneRoundTrip 'octagram-guard' $octagramGuardBytes `
        $octagramGuardState $octagramCombinedState
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $fixtureHash 'source fixture remains untouched'

    Write-Host "All $assertionCount manual realm-selection assertions passed."
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
