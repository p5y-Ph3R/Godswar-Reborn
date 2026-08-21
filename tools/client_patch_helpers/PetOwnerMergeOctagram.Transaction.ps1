function Add-PetOwnerMergeOctagramTestTrace(
    [object]$Trace,
    [string]$Event
) {
    if ($null -ne $Trace) { [void]$Trace.Add($Event) }
}

function Invoke-PetOwnerMergeOctagramTestBoundary(
    [string]$Boundary,
    [string]$FaultBoundary,
    [object]$Trace
) {
    Add-PetOwnerMergeOctagramTestTrace $Trace "commit:$Boundary"
    if ($FaultBoundary -eq $Boundary) {
        Add-PetOwnerMergeOctagramTestTrace $Trace "fault:$Boundary"
        throw "Injected owner-Merge octagram fault after $Boundary."
    }
}

function Assert-PetOwnerMergeOctagramFileHash(
    [string]$Path,
    [string]$ExpectedHash,
    [string]$Label
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $ExpectedHash) {
        throw "$Label hash changed: $actual"
    }
}

function Write-PetOwnerMergeOctagramVerifiedStage(
    [string]$Path,
    [byte[]]$Data,
    [string]$ExpectedHash
) {
    if (Test-Path -LiteralPath $Path) {
        throw "Owner-Merge octagram stage already exists: $Path"
    }
    [IO.File]::WriteAllBytes($Path, $Data)
    Assert-PetOwnerMergeOctagramFileHash `
        $Path $ExpectedHash 'Owner-Merge octagram stage'
}

function Remove-PetOwnerMergeOctagramKnownTemporary(
    [string]$Path,
    [string]$ExpectedHash
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq
            $ExpectedHash) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function Install-PetOwnerMergeOctagramStagedReplacement(
    [string]$StagePath,
    [string]$TargetPath,
    [string]$ReplacedPath,
    [string]$ExpectedBeforeHash,
    [string]$ExpectedAfterHash,
    [object]$Journal,
    [string]$Component,
    [int]$XmlIndex
) {
    Assert-PetOwnerMergeOctagramFileHash `
        $StagePath $ExpectedAfterHash 'Replacement stage'
    Assert-PetOwnerMergeOctagramFileHash `
        $TargetPath $ExpectedBeforeHash 'Replacement target'
    [IO.File]::Replace($StagePath, $TargetPath, $ReplacedPath, $true)
    if ($Component -eq 'Exe') {
        $Journal.ExeCommitted = $true
    }
    else {
        $Journal.XmlCommitted[$XmlIndex] = $true
    }
    Assert-PetOwnerMergeOctagramFileHash `
        $TargetPath $ExpectedAfterHash 'Installed replacement'
    Assert-PetOwnerMergeOctagramFileHash `
        $ReplacedPath $ExpectedBeforeHash 'Displaced source'
    Remove-Item -LiteralPath $ReplacedPath -Force
}

function Restore-PetOwnerMergeOctagramFileAtomic(
    [string]$TargetPath,
    [byte[]]$OriginalData,
    [string]$ExpectedCurrentHash,
    [string]$OriginalHash,
    [string]$OperationId
) {
    Assert-PetOwnerMergeOctagramFileHash `
        $TargetPath $ExpectedCurrentHash 'Rollback target'
    $stage = "$TargetPath.$OperationId.restore"
    $replaced = "$stage.replaced"
    try {
        Write-PetOwnerMergeOctagramVerifiedStage `
            $stage $OriginalData $OriginalHash
        [IO.File]::Replace($stage, $TargetPath, $replaced, $true)
        Assert-PetOwnerMergeOctagramFileHash `
            $TargetPath $OriginalHash 'Restored target'
        Assert-PetOwnerMergeOctagramFileHash `
            $replaced $ExpectedCurrentHash 'Rollback displaced file'
        Remove-Item -LiteralPath $replaced -Force
    }
    finally {
        Remove-PetOwnerMergeOctagramKnownTemporary $stage $OriginalHash
        Remove-PetOwnerMergeOctagramKnownTemporary `
            $replaced $ExpectedCurrentHash
    }
}

function Move-PetOwnerMergeOctagramExactAssetToRecovery(
    [string]$AssetPath,
    [string]$RecoveryPath,
    [string]$ExpectedHash,
    [bool]$AllowMissing,
    [scriptblock]$TestAfterPrecheck = $null
) {
    if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf)) {
        if ($AllowMissing) { return $false }
        throw "Owner-Merge octagram asset is missing: $AssetPath"
    }
    Assert-PetOwnerMergeOctagramFileHash `
        $AssetPath $ExpectedHash 'Asset before recoverable removal'
    if ($null -ne $TestAfterPrecheck) { & $TestAfterPrecheck }
    if (Test-Path -LiteralPath $RecoveryPath) {
        throw "Owner-Merge octagram recovery path already exists: $RecoveryPath"
    }
    [IO.File]::Move($AssetPath, $RecoveryPath)
    $movedHash = (Get-FileHash -LiteralPath $RecoveryPath `
        -Algorithm SHA256).Hash
    if ($movedHash -ne $ExpectedHash) {
        if (-not (Test-Path -LiteralPath $AssetPath)) {
            [IO.File]::Move($RecoveryPath, $AssetPath)
        }
        throw 'A foreign effect 0004 raced with recoverable removal; it was not deleted.'
    }
    return $true
}

function Restore-PetOwnerMergeOctagramAssetAtomic(
    [object]$State,
    [object]$Journal,
    [string]$OperationId
) {
    if (Test-Path -LiteralPath $State.AssetPath -PathType Leaf) {
        Assert-PetOwnerMergeOctagramFileHash `
            $State.AssetPath $State.InstalledAssetHash 'Rollback applied asset'
        return
    }
    $stage = "$($State.AssetPath).$OperationId.restore"
    if ($Journal.AssetRemoved -and
        (Test-Path -LiteralPath $Journal.AssetRecoveryPath -PathType Leaf)) {
        Assert-PetOwnerMergeOctagramFileHash `
            $Journal.AssetRecoveryPath $State.InstalledAssetHash `
            'Recoverable removed asset'
        [IO.File]::Move($Journal.AssetRecoveryPath, $State.AssetPath)
    }
    else {
        Write-PetOwnerMergeOctagramVerifiedStage `
            $stage $State.AssetBytes $State.InstalledAssetHash
        [IO.File]::Move($stage, $State.AssetPath)
    }
    Assert-PetOwnerMergeOctagramFileHash `
        $State.AssetPath $State.InstalledAssetHash 'Restored applied asset'
    Remove-PetOwnerMergeOctagramKnownTemporary `
        $stage $State.InstalledAssetHash
}
