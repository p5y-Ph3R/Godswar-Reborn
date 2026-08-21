[CmdletBinding()]
param(
    [string]$FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientCharacterBackRealmReturn.ps1'
$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'RealmComposite.States.ps1')
. (Join-Path $helperRoot 'RealmComposite.TestFixtures.ps1')
. (Join-Path $helperRoot 'CharacterBackRealmReturn.Patch.ps1')

$stateMap = Get-RealmCompositeStateMap
$baseState = Get-RealmCompositeState $stateMap $false $false $false
$manualState = Get-RealmCompositeState $stateMap $true $false $false
$guardState = Get-RealmCompositeState $stateMap $false $true $false
$manualGuardState = Get-RealmCompositeState $stateMap $true $true $false
$octagramState = Get-RealmCompositeState $stateMap $false $false $true
$octagramManualState = Get-RealmCompositeState $stateMap $true $false $true
$octagramGuardState = Get-RealmCompositeState $stateMap $false $true $true
$octagramManualGuardState = Get-RealmCompositeState `
    $stateMap $true $true $true

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'character-back-realm-return-test-' + [guid]::NewGuid().ToString('N'))
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

function Invoke-CharacterBackGuardModel {
    param(
        [int]$LifecycleState,
        [bool]$FirstRootReady,
        [bool]$SecondRootReady
    )

    $missing = $LifecycleState -eq 2 -and
        (-not $FirstRootReady -or -not $SecondRootReady)
    return [pscustomobject]@{
        Path = if ($missing) { 'MissingRoot' } else { 'Native' }
        RetryFlag = if ($missing) { 1 } else { $null }
        PendingState = if ($missing) { 2 } else { $null }
        Edi = if ($missing) { 2 } else { $null }
    }
}

function Invoke-VariantRoundTrip {
    param(
        [string]$Name,
        [byte[]]$SourceBytes,
        [object]$SourceState,
        [object]$PatchedState
    )

    $variantRoot = Join-Path $testRoot $Name
    $clientRoot = Join-Path $variantRoot 'client'
    $backupRoot = Join-Path $variantRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    [IO.File]::WriteAllBytes($copy, $SourceBytes)
    $octagramStatus = Get-RealmCompositeOctagramStatus $SourceState

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.Status 'Ready to apply' "$Name source status"
    Assert-Value $status.State $SourceState.Name "$Name source state"
    Assert-Value $status.ManualRealmSelection $SourceState.ManualPatched `
        "$Name source manual-selection state"
    Assert-Value $status.PetOwnerMergeOctagram $octagramStatus `
        "$Name source octagram state"
    Assert-Value $status.Sha256 $SourceState.Hash "$Name source status hash"
    Assert-Value $status.HookFileOffset '0x1F58B6' "$Name hook offset"
    Assert-Value $status.HookVa '0x005F58B6' "$Name hook VA"
    Assert-Value $status.CaveFileOffset '0x53E3E0' "$Name cave offset"
    Assert-Value $status.CaveVa '0x0093E3E0' "$Name cave VA"
    Assert-Value $status.CaveInboundRelativeXrefs 0 `
        "$Name source cave xrefs"
    Assert-Value $status.CaveAbsoluteReferences 0 `
        "$Name source absolute cave references"
    Assert-True (-not (Test-Path -LiteralPath $backupRoot)) `
        "$Name Status creates no backup"

    $before = [IO.File]::ReadAllBytes($copy)
    $apply = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $apply.Status 'Patched' "$Name Apply status"
    Assert-Value $apply.State $PatchedState.Name "$Name Apply state"
    Assert-Value $apply.ManualRealmSelection $SourceState.ManualPatched `
        "$Name Apply preserves manual selection"
    Assert-Value $apply.PetOwnerMergeOctagram $octagramStatus `
        "$Name Apply preserves octagram state"
    Assert-Value $apply.ChangedBytes 64 "$Name Apply changed bytes"
    Assert-Value $apply.BeforeSha256 $SourceState.Hash `
        "$Name Apply before hash"
    Assert-Value $apply.AfterSha256 $PatchedState.Hash `
        "$Name Apply after hash"
    Assert-Value (
        Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256
    ).Hash $SourceState.Hash "$Name Apply backup hash"

    $after = [IO.File]::ReadAllBytes($copy)
    Assert-Value (
        Get-CharacterBackRealmReturnSha256 $after
    ) $PatchedState.Hash "$Name installed hash"
    Assert-OnlyAllowedDifferences $before $after @(
        [pscustomobject]@{ Offset = 0x1F58B6; Length = 6 },
        [pscustomobject]@{ Offset = 0x53E3E0; Length = 112 }
    ) 64 "$Name Apply"

    $patchedHook = Convert-HexBytes 'E9 25 8B 34 00 90'
    $caveCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12
A1 A0 60 57 01 85 C0 74 14
A1 8C 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 B6 74 CB FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 CD 74 CB FF
'@
    Assert-True (Test-Bytes $after 0x1F58B6 $patchedHook) `
        "$Name installed hook"
    Assert-True (Test-Bytes $after 0x53E3E0 $caveCode) `
        "$Name installed cave"
    Assert-True (Test-Bytes $after (0x53E3E0 + $caveCode.Length) (
            [byte[]]::new(112 - $caveCode.Length))) `
        "$Name cave tail remains zero"
    Assert-RelativeBranch $patchedHook 0 0x005F58B6 `
        0x0093E3E0 "$Name hook"
    Assert-RelativeBranch $caveCode 0x21 0x0093E3E0 `
        0x005F58BC "$Name native continuation"
    Assert-RelativeBranch $caveCode 0x38 0x0093E3E0 `
        0x005F58EA "$Name missing-root continuation"

    $manualBytes = if ($SourceState.ManualPatched) {
        Convert-HexBytes '90 90 90 90 90'
    }
    else {
        Convert-HexBytes 'E8 92 23 00 00'
    }
    Assert-True (Test-Bytes $after 0x1F9A19 $manualBytes) `
        "$Name manual-selection peer bytes preserved"
    Assert-True (Test-Bytes $after 0x0C14C5 (
            Convert-HexBytes 'E9 36 1E 50 00')) `
        "$Name lifecycle reset hook preserved"

    $pe = Get-PeMetadata $after
    $hookMap = Resolve-ExecutableFileRange $pe 0x1F58B6 6
    $caveMap = Resolve-ExecutableFileRange $pe 0x53E3E0 112
    Assert-Value $hookMap.Section '.text' "$Name hook section"
    Assert-Value $hookMap.Va 0x005F58B6 "$Name hook mapping"
    Assert-Value $caveMap.Section '.rdata' "$Name cave section"
    Assert-Value $caveMap.Va 0x0093E3E0 "$Name cave mapping"
    $xrefs = @(Get-CharacterBackRealmReturnRelativeCaveXrefs `
        $after $pe 0x0093E3E0 0x0093E450)
    Assert-Value $xrefs.Count 1 "$Name patched cave xref count"
    Assert-Value $xrefs[0].Offset 0x1F58B6 "$Name patched cave xref source"
    Assert-Value $xrefs[0].Target 0x0093E3E0 `
        "$Name patched cave xref target"
    Assert-Value @(Get-CharacterBackRealmReturnAbsoluteCaveReferences `
        $after 0x0093E3E0 0x0093E450).Count 0 `
        "$Name patched cave absolute references"

    $backupCount = @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count
    $again = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $again.Status 'Already patched' "$Name idempotent Apply"
    Assert-Value @(
        Get-ChildItem -LiteralPath $backupRoot -Directory
    ).Count $backupCount "$Name idempotent Apply backup count"
    $patchedStatus = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $patchedStatus.Status 'Patched' "$Name patched Status"
    Assert-Value $patchedStatus.State $PatchedState.Name `
        "$Name patched Status state"
    Assert-Value $patchedStatus.PetOwnerMergeOctagram $octagramStatus `
        "$Name patched Status octagram state"
    Assert-Value $patchedStatus.Sha256 $PatchedState.Hash `
        "$Name patched Status hash"

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.Status 'Reverted' "$Name Revert status"
    Assert-Value $revert.State $SourceState.Name "$Name Revert state"
    Assert-Value $revert.ManualRealmSelection $SourceState.ManualPatched `
        "$Name Revert preserves manual selection"
    Assert-Value $revert.PetOwnerMergeOctagram $octagramStatus `
        "$Name Revert preserves octagram state"
    Assert-Value $revert.ChangedBytes 64 "$Name Revert changed bytes"
    Assert-Value $revert.BeforeSha256 $PatchedState.Hash `
        "$Name Revert before hash"
    Assert-Value $revert.AfterSha256 $SourceState.Hash `
        "$Name Revert after hash"
    Assert-Value (
        Get-FileHash -LiteralPath $revert.Backup -Algorithm SHA256
    ).Hash $PatchedState.Hash "$Name Revert backup hash"
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            [byte[]]$before,
            [byte[]][IO.File]::ReadAllBytes($copy))) `
        "$Name exact Apply/Revert roundtrip"

    $revertAgain = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revertAgain.Status 'Already reverted' `
        "$Name idempotent Revert"
}

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Fixture not found: $FixtureExe"
    }
    $fixturePath = (Resolve-Path -LiteralPath $FixtureExe).Path
    $fixtureBytes = [IO.File]::ReadAllBytes($fixturePath)
    $fixtureHash = Get-CharacterBackRealmReturnSha256 $fixtureBytes
    Assert-True ($stateMap.ContainsKey($fixtureHash)) `
        'fixture is an exact supported state'

    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $emptyCave = [byte[]]::new(112)

    $baseBytes = New-RealmCompositeFixture `
        $fixtureBytes $false $false $false
    Assert-Value (
        Get-CharacterBackRealmReturnSha256 $baseBytes
    ) $baseState.Hash 'derived base fixture hash'

    $manualBytes = New-RealmCompositeFixture `
        $fixtureBytes $true $false $false
    Assert-Value (
        Get-CharacterBackRealmReturnSha256 $manualBytes
    ) $manualState.Hash 'derived manual-selection fixture hash'

    $octagramBytes = New-RealmCompositeFixture `
        $fixtureBytes $false $false $true
    Assert-Value (
        Get-CharacterBackRealmReturnSha256 $octagramBytes
    ) $octagramState.Hash 'derived octagram fixture hash'

    $octagramManualBytes = New-RealmCompositeFixture `
        $fixtureBytes $true $false $true
    Assert-Value (
        Get-CharacterBackRealmReturnSha256 $octagramManualBytes
    ) $octagramManualState.Hash 'derived octagram+manual fixture hash'

    Assert-True (Test-Bytes $baseBytes 0x53E3E0 $emptyCave) `
        'source cave is exactly 112 zero bytes'
    Assert-True (Test-Bytes $baseBytes 0x53E3C8 (
            Convert-HexBytes @'
E9 64 EB 05 01 C9 83 E9 28 89 4C 24 18 C3
00 00 00 00 00 00 00 00 00 00
'@)) 'source cave prefix is pinned'
    Assert-True (Test-Bytes $baseBytes 0x53E450 (
            Convert-HexBytes @'
20 00 20 00 20 00 20 00 20 00 20 00 20 00 20 00
'@)) 'source cave suffix is pinned'
    $backDispatch = Convert-HexBytes 'E8 64 20 00 00'
    Assert-True (Test-Bytes $baseBytes 0x1F37A7 $backDispatch) `
        'Back event dispatch CALL is pinned'
    Assert-Value (0x005F37A7 + 5 +
        [BitConverter]::ToInt32($backDispatch, 1)) 0x005F5810 `
        'Back event dispatch targets the guarded routine'
    $sourcePe = Get-PeMetadata $baseBytes
    Assert-Value @(Get-CharacterBackRealmReturnRelativeCaveXrefs `
        $baseBytes $sourcePe 0x0093E3E0 0x0093E450).Count 0 `
        'source cave relative xrefs'
    Assert-Value @(Get-CharacterBackRealmReturnAbsoluteCaveReferences `
        $baseBytes 0x0093E3E0 0x0093E450).Count 0 `
        'source cave absolute references'

    Invoke-VariantRoundTrip 'base' $baseBytes $baseState $guardState
    Invoke-VariantRoundTrip 'manual' $manualBytes `
        $manualState $manualGuardState
    Invoke-VariantRoundTrip 'octagram' $octagramBytes `
        $octagramState $octagramGuardState
    Invoke-VariantRoundTrip 'octagram-manual' $octagramManualBytes `
        $octagramManualState $octagramManualGuardState

    $nativeModel = Invoke-CharacterBackGuardModel 3 $false $false
    Assert-Value $nativeModel.Path 'Native' 'non-LOGIN state stays native'
    Assert-Value $nativeModel.PendingState $null `
        'non-LOGIN state does not schedule state 2'
    $readyModel = Invoke-CharacterBackGuardModel 2 $true $true
    Assert-Value $readyModel.Path 'Native' 'both-ready state stays native'
    $firstMissing = Invoke-CharacterBackGuardModel 2 $false $true
    Assert-Value $firstMissing.Path 'MissingRoot' 'first missing root path'
    Assert-Value $firstMissing.RetryFlag 1 'first missing root retry flag'
    Assert-Value $firstMissing.PendingState 2 `
        'first missing root pending state'
    Assert-Value $firstMissing.Edi 2 'first missing root EDI state'
    $secondMissing = Invoke-CharacterBackGuardModel 2 $true $false
    Assert-Value $secondMissing.Path 'MissingRoot' 'second missing root path'
    Assert-Value $secondMissing.RetryFlag 1 'second missing root retry flag'
    Assert-Value $secondMissing.PendingState 2 `
        'second missing root pending state'

    $partial = Join-Path $testRoot 'PartialOrigin.exe'
    $partialBytes = [byte[]]$manualBytes.Clone()
    $partialBytes[0x1F58B6] = 0xE9
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    Assert-Throws {
        & $patcher -ClientExe $partial -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'partial hook conflict'

    $continuationTamper = Join-Path $testRoot 'ContinuationTamper.exe'
    $continuationBytes = [byte[]]$manualBytes.Clone()
    $continuationBytes[0x1F58BC] = 0x90
    [IO.File]::WriteAllBytes($continuationTamper, $continuationBytes)
    Assert-Throws {
        & $patcher -ClientExe $continuationTamper -Mode Status | Out-Null
    } 'native prerequisites' 'native continuation conflict'

    $caveTamper = Join-Path $testRoot 'CaveTamper.exe'
    $caveBytes = [byte[]]$manualBytes.Clone()
    $caveBytes[0x53E400] = 0xCC
    [IO.File]::WriteAllBytes($caveTamper, $caveBytes)
    Assert-Throws {
        & $patcher -ClientExe $caveTamper -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'foreign cave conflict'

    $manualTamper = Join-Path $testRoot 'ManualPeerTamper.exe'
    $manualTamperBytes = [byte[]]$manualBytes.Clone()
    $manualTamperBytes[0x1F9A19] = 0xCC
    [IO.File]::WriteAllBytes($manualTamper, $manualTamperBytes)
    Assert-Throws {
        & $patcher -ClientExe $manualTamper -Mode Status | Out-Null
    } 'Unsupported Origin.exe SHA-256/state' 'manual peer conflict'

    $currentProcessPath = (Get-Process -Id $PID).Path
    Assert-Throws {
        Assert-CharacterBackRealmReturnProcessClosed $currentProcessPath
    } 'is running' 'running-process mutation guard'

    foreach ($path in @(
        $patcher,
        (Join-Path $helperRoot 'CharacterBackRealmReturn.Patch.ps1'),
        (Join-Path $helperRoot 'RealmComposite.States.ps1'),
        (Join-Path $helperRoot 'RealmComposite.TestFixtures.ps1'),
        $PSCommandPath
    )) {
        $item = Get-Item -LiteralPath $path
        Assert-True ($item.Length -lt 20000) `
            "$($item.Name) remains below 20 KB"
        Assert-True (@(Get-Content -LiteralPath $path).Count -lt 600) `
            "$($item.Name) remains below 600 lines"
    }
    Assert-Value (
        Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256
    ).Hash $fixtureHash 'live/source fixture remains untouched by tests'

    Write-Host "All $assertionCount character Back realm-return assertions passed."
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
