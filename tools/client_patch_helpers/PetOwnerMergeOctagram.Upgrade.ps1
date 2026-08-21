function Assert-PetOwnerMergeOctagramUpgradePeersUnchanged([object]$State) {
    Assert-PetOwnerMergeOctagramFileHash `
        $State.ExePath $State.ExeState.Hash 'Upgrade executable peer'
    for ($index = 0; $index -lt 2; $index++) {
        Assert-PetOwnerMergeOctagramFileHash `
            $State.XmlPaths[$index] $State.XmlStates[$index].Hash `
            "Upgrade XML peer $index"
    }
    $palette = Assert-PetOwnerMergeOctagramPinnedAssets `
        $State.Root $State.AssetSpec
    if ($palette -ne $State.Effect0002Palette) {
        throw 'Effect 0002 palette changed during octagram asset upgrade.'
    }
}

function Invoke-PetOwnerMergeOctagramAssetUpgrade(
    [object]$State,
    [string]$BackupRoot,
    [string]$RepositoryRoot,
    [string]$TestFaultBoundary,
    [object]$TestTrace,
    [bool]$TestFixtureMode,
    [scriptblock]$TestAfterAssetCommit = $null
) {
    if (-not $State.Octagram -or
        $State.AssetVariant -ne 'LegacyCrossScanline') {
        throw 'Owner-Merge octagram asset upgrade requires the exact legacy package.'
    }
    if (-not $TestFixtureMode) {
        Assert-PetOwnerMergeOctagramClientClosed $State.ExePath
    }
    if ($null -ne $TestAfterAssetCommit -and -not $TestFixtureMode) {
        throw 'The asset-upgrade race hook is restricted to temp fixtures.'
    }
    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    $backupDirectory = New-PetOwnerMergeOctagramBackup `
        $State ([IO.Path]::GetFullPath($BackupRoot)) 'Upgrade'
    $operationId = [guid]::NewGuid().ToString('N')
    $stage = "$($State.AssetPath).$operationId.stage"
    $replaced = "$stage.replaced"
    $committed = $false
    try {
        Write-PetOwnerMergeOctagramVerifiedStage `
            $stage $State.CanonicalAsset $State.AssetSpec.Hash
        if (-not $TestFixtureMode) {
            Assert-PetOwnerMergeOctagramClientClosed $State.ExePath
        }
        Assert-PetOwnerMergeOctagramStateUnchanged $State
        Assert-PetOwnerMergeOctagramFileHash `
            $State.AssetPath $State.InstalledAssetHash 'Legacy upgrade target'
        [IO.File]::Replace($stage, $State.AssetPath, $replaced, $true)
        $committed = $true
        Assert-PetOwnerMergeOctagramFileHash `
            $State.AssetPath $State.AssetSpec.Hash 'Upgraded effect 0004'
        Assert-PetOwnerMergeOctagramFileHash `
            $replaced $State.InstalledAssetHash 'Displaced legacy effect 0004'
        Invoke-PetOwnerMergeOctagramTestBoundary `
            'Upgrade.Asset' $TestFaultBoundary $TestTrace
        if ($null -ne $TestAfterAssetCommit) { & $TestAfterAssetCommit }
        $recovery = Join-Path $backupDirectory `
            ('replaced-legacy-e_he_0004_all-' + $operationId + '.gwm')
        [IO.File]::Move($replaced, $recovery)
        Assert-PetOwnerMergeOctagramFileHash `
            $recovery $State.InstalledAssetHash 'Recovered legacy effect 0004'
        Assert-PetOwnerMergeOctagramFileHash `
            $State.AssetPath $State.AssetSpec.Hash 'Final upgraded effect 0004'
        Assert-PetOwnerMergeOctagramUpgradePeersUnchanged $State
    }
    catch {
        $failure = $_
        if ($committed) {
            Restore-PetOwnerMergeOctagramFileAtomic `
                $State.AssetPath $State.AssetBytes $State.AssetSpec.Hash `
                $State.InstalledAssetHash $operationId
            Add-PetOwnerMergeOctagramTestTrace $TestTrace 'rollback:asset'
        }
        Assert-PetOwnerMergeOctagramFileHash `
            $State.AssetPath $State.InstalledAssetHash `
            'Rolled-back legacy effect 0004'
        try { Assert-PetOwnerMergeOctagramUpgradePeersUnchanged $State }
        catch {
            throw 'Octagram asset upgrade rolled back, but a peer changed concurrently.'
        }
        throw $failure
    }
    finally {
        Remove-PetOwnerMergeOctagramKnownTemporary `
            $stage $State.AssetSpec.Hash
        Remove-PetOwnerMergeOctagramKnownTemporary `
            $replaced $State.InstalledAssetHash
    }
    return [pscustomobject]@{
        Mode = 'Apply'
        Status = 'Applied'
        PetOwnerMergeOctagram = 'Applied'
        AssetPackage = 'Fixed'
        AssetUpgradeRequired = $false
        BeforeEffect0004Sha256 = $State.InstalledAssetHash
        Effect0004Sha256 = $State.AssetSpec.Hash
        ExeSha256 = $State.ExeState.Hash
        PetXmlSha256 = $State.XmlStates[0].Hash
        BackupDirectory = $backupDirectory
        ManualRealmSelectionPreserved = $State.ExeState.Manual
        CharacterBackGuardPreserved = $State.ExeState.Back
        Effect0002Palette = $State.Effect0002Palette
    }
}
