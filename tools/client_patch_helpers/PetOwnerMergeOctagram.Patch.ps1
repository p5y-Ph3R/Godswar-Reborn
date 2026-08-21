function Assert-PetOwnerMergeOctagramClientClosed(
    [string]$ExePath,
    [object[]]$Processes = $null
) {
    $resolved = [IO.Path]::GetFullPath($ExePath)
    $candidates = if ($null -ne $Processes) {
        @($Processes)
    }
    else { @(Get-Process Origin -ErrorAction SilentlyContinue) }
    foreach ($process in $candidates) {
        try {
            $path = try { $process.Path }
                catch {
                    throw 'Cannot inspect a running Origin process; refusing client mutation.'
                }
            if ([string]::IsNullOrWhiteSpace($path)) {
                throw 'A running Origin process has no inspectable path; refusing client mutation.'
            }
            if ([string]::Equals(
                    [IO.Path]::GetFullPath($path), $resolved,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Close Origin.exe before changing its Merge octagram visual.'
            }
        }
        finally {
            if ($process -is [IDisposable]) { $process.Dispose() }
        }
    }
}

function Get-PetOwnerMergeOctagramPatchState(
    [string]$ClientRoot,
    [string]$RepositoryRoot
) {
    $root = [IO.Path]::GetFullPath($ClientRoot)
    $exePath = Join-Path $root 'Origin.exe'
    $xmlPaths = @('en_us', 'zh_cn') | ForEach-Object {
        Join-Path $root "Localization\$_\Settings\Sys\Pet.xml"
    }
    foreach ($path in @($exePath) + $xmlPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required owner-Merge octagram client file is missing: $path"
        }
    }

    $binarySpec = Get-PetOwnerMergeOctagramBinarySpec
    $assetSpec = Get-PetOwnerMergeOctagramAssetSpec $RepositoryRoot
    [byte[]]$canonicalAsset = Get-PetOwnerMergeOctagramCanonicalAsset $assetSpec
    $effect0002Palette = Assert-PetOwnerMergeOctagramPinnedAssets `
        $root $assetSpec

    [byte[]]$exe = [IO.File]::ReadAllBytes($exePath)
    $exeState = Get-PetOwnerMergeOctagramExeState $exe $binarySpec
    $xmlBytes = @($xmlPaths | ForEach-Object {
        ,([IO.File]::ReadAllBytes($_))
    })
    $xmlStates = @($xmlBytes | ForEach-Object {
        Get-PetOwnerMergeOctagramXmlState $_
    })
    if ($xmlStates[0].Octagram -ne $xmlStates[1].Octagram -or
        $xmlStates[0].Octagram -ne $exeState.Octagram) {
        throw 'Owner-Merge octagram executable and Pet.xml files are mixed.'
    }

    $assetPath = Join-Path $root $assetSpec.RelativeClientPath
    $assetExists = Test-Path -LiteralPath $assetPath -PathType Leaf
    [byte[]]$assetBytes = if ($assetExists) {
        [IO.File]::ReadAllBytes($assetPath)
    }
    else { [byte[]]::new(0) }
    $assetVariant = 'Absent'
    $installedAssetHash = $null
    if ($assetExists) {
        $assetVariant = Get-PetOwnerMergeOctagramInstalledAssetState `
            $assetBytes $assetSpec 'Installed asset'
        $installedAssetHash = Get-PetOwnerMergeOctagramSha256 $assetBytes
    }
    if ($assetExists -ne $exeState.Octagram) {
        throw 'Owner-Merge octagram executable/XML and asset are mixed.'
    }

    return [pscustomobject]@{
        Root = $root
        ExePath = $exePath
        XmlPaths = $xmlPaths
        AssetPath = $assetPath
        BinarySpec = $binarySpec
        AssetSpec = $assetSpec
        CanonicalAsset = $canonicalAsset
        Exe = $exe
        ExeState = $exeState
        XmlBytes = $xmlBytes
        XmlStates = $xmlStates
        AssetBytes = $assetBytes
        AssetVariant = $assetVariant
        InstalledAssetHash = $installedAssetHash
        Effect0002Palette = $effect0002Palette
        Octagram = $exeState.Octagram
    }
}

function Assert-PetOwnerMergeOctagramStateUnchanged([object]$State) {
    if ((Get-FileHash -LiteralPath $State.ExePath -Algorithm SHA256).Hash -ne
            $State.ExeState.Hash) {
        throw 'Origin.exe changed while the octagram patch was staged.'
    }
    for ($index = 0; $index -lt 2; $index++) {
        if ((Get-FileHash -LiteralPath $State.XmlPaths[$index] `
                -Algorithm SHA256).Hash -ne $State.XmlStates[$index].Hash) {
            throw 'Pet.xml changed while the octagram patch was staged.'
        }
    }
    $assetExists = Test-Path -LiteralPath $State.AssetPath -PathType Leaf
    if ($assetExists -ne $State.Octagram -or
        $assetExists -and
        (Get-FileHash -LiteralPath $State.AssetPath -Algorithm SHA256).Hash -ne
            $State.InstalledAssetHash) {
        throw 'Effect 0004 changed while the octagram patch was staged.'
    }
    $palette = Assert-PetOwnerMergeOctagramPinnedAssets `
        $State.Root $State.AssetSpec
    if ($palette -ne $State.Effect0002Palette) {
        throw 'Effect 0002 palette changed while the octagram patch was staged.'
    }
}

function New-PetOwnerMergeOctagramBackup(
    [object]$State,
    [string]$BackupRoot,
    [string]$Mode
) {
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $directory = Join-Path $BackupRoot (
        'client-pet-owner-merge-octagram-' + $Mode.ToLowerInvariant() + '-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $files = @(
        @('Origin.exe', $State.Exe),
        @('Pet.en_us.xml', $State.XmlBytes[0]),
        @('Pet.zh_cn.xml', $State.XmlBytes[1])
    )
    if ($State.Octagram) {
        $files += ,@('e_he_0004_all.gwm', $State.AssetBytes)
    }
    foreach ($file in $files) {
        $path = Join-Path $directory $file[0]
        [IO.File]::WriteAllBytes($path, [byte[]]$file[1])
        if ((Get-PetOwnerMergeOctagramSha256 (
                [IO.File]::ReadAllBytes($path))) -ne
            (Get-PetOwnerMergeOctagramSha256 ([byte[]]$file[1]))) {
            throw "Owner-Merge octagram backup verification failed: $path"
        }
    }
    return $directory
}

function Restore-PetOwnerMergeOctagramState(
    [object]$State,
    [object]$Journal,
    [string]$BackupDirectory,
    [object]$TestTrace,
    [bool]$TestFixtureMode
) {
    if (-not $TestFixtureMode) {
        Assert-PetOwnerMergeOctagramClientClosed $State.ExePath
    }
    $operationId = [guid]::NewGuid().ToString('N')
    if ($State.Octagram) {
        Restore-PetOwnerMergeOctagramAssetAtomic `
            $State $Journal $operationId
        Add-PetOwnerMergeOctagramTestTrace $TestTrace `
            $(if ($Journal.AssetRemoved) {
                'rollback:asset'
            }
            else { 'rollback:asset-ready' })
        for ($index = 1; $index -ge 0; $index--) {
            if (-not $Journal.XmlCommitted[$index]) { continue }
            Restore-PetOwnerMergeOctagramFileAtomic `
                $State.XmlPaths[$index] ([byte[]]$State.XmlBytes[$index]) `
                $Journal.TargetXmlHash $State.XmlStates[$index].Hash `
                $operationId
            Add-PetOwnerMergeOctagramTestTrace $TestTrace `
                "rollback:xml:$($Journal.XmlLocales[$index])"
        }
        if ($Journal.ExeCommitted) {
            Restore-PetOwnerMergeOctagramFileAtomic `
                $State.ExePath $State.Exe $Journal.TargetExeHash `
                $State.ExeState.Hash $operationId
            Add-PetOwnerMergeOctagramTestTrace $TestTrace 'rollback:exe'
        }
    }
    else {
        if ($Journal.ExeCommitted) {
            Restore-PetOwnerMergeOctagramFileAtomic `
                $State.ExePath $State.Exe $Journal.TargetExeHash `
                $State.ExeState.Hash $operationId
            Add-PetOwnerMergeOctagramTestTrace $TestTrace 'rollback:exe'
        }
        for ($index = 1; $index -ge 0; $index--) {
            if (-not $Journal.XmlCommitted[$index]) { continue }
            Restore-PetOwnerMergeOctagramFileAtomic `
                $State.XmlPaths[$index] ([byte[]]$State.XmlBytes[$index]) `
                $Journal.TargetXmlHash $State.XmlStates[$index].Hash `
                $operationId
            Add-PetOwnerMergeOctagramTestTrace $TestTrace `
                "rollback:xml:$($Journal.XmlLocales[$index])"
        }
        if ($Journal.AssetInstalled) {
            $recovery = Join-Path $BackupDirectory (
                'rollback-removed-e_he_0004_all-' + $operationId + '.gwm')
            [void](Move-PetOwnerMergeOctagramExactAssetToRecovery `
                $State.AssetPath $recovery $State.AssetSpec.Hash $true)
            Add-PetOwnerMergeOctagramTestTrace $TestTrace 'rollback:asset'
        }
    }
    Assert-PetOwnerMergeOctagramStateUnchanged $State
}

function Invoke-PetOwnerMergeOctagramPatch(
    [string]$ClientRoot,
    [string]$Mode,
    [string]$BackupRoot,
    [string]$RepositoryRoot,
    [ValidateSet(
        'None',
        'Apply.Asset',
        'Apply.XmlEn',
        'Apply.XmlZh',
        'Apply.Exe',
        'Revert.Exe',
        'Revert.XmlEn',
        'Revert.XmlZh',
        'Revert.Asset',
        'Upgrade.Asset')]
    [string]$TestFaultBoundary = 'None',
    [object]$TestTrace = $null,
    [bool]$TestFixtureMode = $false
) {
    if ($TestFixtureMode) {
        $fixture = [IO.Path]::GetFullPath($ClientRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $fixture.StartsWith(
                $temp, [StringComparison]::OrdinalIgnoreCase) -or
            $fixture.Length -le $temp.Length + 10) {
            throw 'Test fixture mode is restricted to a unique temp directory.'
        }
    }
    elseif ($Mode -ne 'Status') {
        $preflightExe = Join-Path ([IO.Path]::GetFullPath($ClientRoot)) `
            'Origin.exe'
        if (Test-Path -LiteralPath $preflightExe -PathType Leaf) {
            Assert-PetOwnerMergeOctagramClientClosed $preflightExe
        }
    }
    $state = Get-PetOwnerMergeOctagramPatchState `
        $ClientRoot $RepositoryRoot
    $status = if ($state.Octagram) { 'Applied' } else { 'Reverted' }
    if ($Mode -eq 'Status') {
        return [pscustomobject]@{
            Mode = $Mode
            Status = $status
            PetOwnerMergeOctagram = $status
            ExeSha256 = $state.ExeState.Hash
            PetXmlSha256 = $state.XmlStates[0].Hash
            Effect0004Sha256 = if ($state.Octagram) {
                $state.InstalledAssetHash
            }
            else { $null }
            AssetPackage = $state.AssetVariant
            AssetUpgradeRequired =
                $state.AssetVariant -eq 'LegacyCrossScanline'
            ManualRealmSelection = $state.ExeState.Manual
            CharacterBackGuard = $state.ExeState.Back
            Effect0002Palette = $state.Effect0002Palette
            Selector = 'quality=16 && completedRebirths>=90 => 90'
            Scale = '<30:1.00; 30-59:1.25; 60-89:1.50; >=90:2.00'
        }
    }
    $targetOctagram = $Mode -eq 'Apply'
    if ($state.Octagram -eq $targetOctagram) {
        if ($targetOctagram -and
            $state.AssetVariant -eq 'LegacyCrossScanline') {
            return Invoke-PetOwnerMergeOctagramAssetUpgrade `
                $state $BackupRoot $RepositoryRoot $TestFaultBoundary `
                $TestTrace $TestFixtureMode
        }
        return [pscustomobject]@{
            Mode = $Mode
            Status = "Already $status"
            PetOwnerMergeOctagram = $status
            ExeSha256 = $state.ExeState.Hash
            Effect0004Sha256 = $state.InstalledAssetHash
            AssetPackage = $state.AssetVariant
            AssetUpgradeRequired = $false
        }
    }

    if (-not $TestFixtureMode) {
        Assert-PetOwnerMergeOctagramClientClosed $state.ExePath
    }
    [byte[]]$targetExe = Convert-PetOwnerMergeOctagramExe `
        $state.Exe $state.BinarySpec $targetOctagram
    $targetXml = @($state.XmlBytes | ForEach-Object {
        ,(Convert-PetOwnerMergeOctagramXml $_ $targetOctagram)
    })
    $targetExeHash = $state.ExeState.PeerHash
    $targetXmlHash = if ($targetOctagram) {
        $state.XmlStates[0].NewHash
    }
    else { $state.XmlStates[0].OldHash }
    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $BackupRoot = Join-Path $RepositoryRoot 'backups'
    }
    $backupDirectory = New-PetOwnerMergeOctagramBackup `
        $state ([IO.Path]::GetFullPath($BackupRoot)) $Mode

    $operationId = [guid]::NewGuid().ToString('N')
    $stagePaths = @(
        "$($state.ExePath).$operationId.stage",
        "$($state.XmlPaths[0]).$operationId.stage",
        "$($state.XmlPaths[1]).$operationId.stage"
    )
    $replacePaths = @($stagePaths | ForEach-Object { "$_.replaced" })
    $assetStage = "$($state.AssetPath).$operationId.stage"
    $journal = [pscustomobject]@{
        AssetInstalled = $false
        AssetRemoved = $false
        AssetRecoveryPath = ''
        ExeCommitted = $false
        XmlCommitted = [bool[]]@($false, $false)
        XmlLocales = @('en_us', 'zh_cn')
        TargetExeHash = $targetExeHash
        TargetXmlHash = $targetXmlHash
    }
    try {
        Write-PetOwnerMergeOctagramVerifiedStage `
            $stagePaths[0] $targetExe $targetExeHash
        Write-PetOwnerMergeOctagramVerifiedStage `
            $stagePaths[1] ([byte[]]$targetXml[0]) $targetXmlHash
        Write-PetOwnerMergeOctagramVerifiedStage `
            $stagePaths[2] ([byte[]]$targetXml[1]) $targetXmlHash
        if ($targetOctagram) {
            Write-PetOwnerMergeOctagramVerifiedStage `
                $assetStage $state.CanonicalAsset $state.AssetSpec.Hash
        }

        if (-not $TestFixtureMode) {
            Assert-PetOwnerMergeOctagramClientClosed $state.ExePath
        }
        Assert-PetOwnerMergeOctagramStateUnchanged $state
        if ($targetOctagram) {
            if (Test-Path -LiteralPath $state.AssetPath) {
                throw 'Effect 0004 appeared before octagram asset commit.'
            }
            [IO.File]::Move($assetStage, $state.AssetPath)
            $journal.AssetInstalled = $true
            Assert-PetOwnerMergeOctagramFileHash `
                $state.AssetPath $state.AssetSpec.Hash 'Installed effect 0004'
            Invoke-PetOwnerMergeOctagramTestBoundary `
                'Apply.Asset' $TestFaultBoundary $TestTrace

            for ($index = 0; $index -lt 2; $index++) {
                Install-PetOwnerMergeOctagramStagedReplacement `
                    $stagePaths[$index + 1] $state.XmlPaths[$index] `
                    $replacePaths[$index + 1] $state.XmlStates[$index].Hash `
                    $targetXmlHash $journal 'Xml' $index
                Invoke-PetOwnerMergeOctagramTestBoundary `
                    $(if ($index -eq 0) {
                        'Apply.XmlEn'
                    }
                    else { 'Apply.XmlZh' }) `
                    $TestFaultBoundary $TestTrace
            }
            Install-PetOwnerMergeOctagramStagedReplacement `
                $stagePaths[0] $state.ExePath $replacePaths[0] `
                $state.ExeState.Hash $targetExeHash $journal 'Exe' -1
            Invoke-PetOwnerMergeOctagramTestBoundary `
                'Apply.Exe' $TestFaultBoundary $TestTrace
        }
        else {
            Install-PetOwnerMergeOctagramStagedReplacement `
                $stagePaths[0] $state.ExePath $replacePaths[0] `
                $state.ExeState.Hash $targetExeHash $journal 'Exe' -1
            Invoke-PetOwnerMergeOctagramTestBoundary `
                'Revert.Exe' $TestFaultBoundary $TestTrace
            for ($index = 0; $index -lt 2; $index++) {
                Install-PetOwnerMergeOctagramStagedReplacement `
                    $stagePaths[$index + 1] $state.XmlPaths[$index] `
                    $replacePaths[$index + 1] $state.XmlStates[$index].Hash `
                    $targetXmlHash $journal 'Xml' $index
                Invoke-PetOwnerMergeOctagramTestBoundary `
                    $(if ($index -eq 0) {
                        'Revert.XmlEn'
                    }
                    else { 'Revert.XmlZh' }) `
                    $TestFaultBoundary $TestTrace
            }
            $journal.AssetRecoveryPath = Join-Path $backupDirectory `
                ('removed-e_he_0004_all-' + $operationId + '.gwm')
            $journal.AssetRemoved =
                Move-PetOwnerMergeOctagramExactAssetToRecovery `
                    $state.AssetPath $journal.AssetRecoveryPath `
                    $state.InstalledAssetHash $false
            Invoke-PetOwnerMergeOctagramTestBoundary `
                'Revert.Asset' $TestFaultBoundary $TestTrace
        }

        if ((Get-FileHash -LiteralPath $state.ExePath -Algorithm SHA256).Hash -ne
                $targetExeHash -or
            @($state.XmlPaths | Where-Object {
                (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash -ne
                    $targetXmlHash
            }).Count -ne 0 -or
            (Test-Path -LiteralPath $state.AssetPath -PathType Leaf) -ne
                $targetOctagram) {
            throw 'Installed owner-Merge octagram files failed verification.'
        }
        if ($targetOctagram) {
            Assert-PetOwnerMergeOctagramAsset `
                ([IO.File]::ReadAllBytes($state.AssetPath)) `
                $state.AssetSpec 'Installed asset'
        }
        $palette = Assert-PetOwnerMergeOctagramPinnedAssets `
            $state.Root $state.AssetSpec
        if ($palette -ne $state.Effect0002Palette) {
            throw 'Effect 0002 palette changed during octagram installation.'
        }
    }
    catch {
        $failure = $_
        try {
            Restore-PetOwnerMergeOctagramState `
                $state $journal $backupDirectory $TestTrace $TestFixtureMode
        }
        catch {
            throw "Owner-Merge octagram write and automatic restore failed. Backup: $backupDirectory"
        }
        throw $failure
    }
    finally {
        Remove-PetOwnerMergeOctagramKnownTemporary `
            $stagePaths[0] $targetExeHash
        Remove-PetOwnerMergeOctagramKnownTemporary `
            $replacePaths[0] $state.ExeState.Hash
        for ($index = 1; $index -lt 3; $index++) {
            Remove-PetOwnerMergeOctagramKnownTemporary `
                $stagePaths[$index] $targetXmlHash
            Remove-PetOwnerMergeOctagramKnownTemporary `
                $replacePaths[$index] $state.XmlStates[$index - 1].Hash
        }
        Remove-PetOwnerMergeOctagramKnownTemporary `
            $assetStage $state.AssetSpec.Hash
    }

    return [pscustomobject]@{
        Mode = $Mode
        Status = if ($targetOctagram) { 'Applied' } else { 'Reverted' }
        PetOwnerMergeOctagram = if ($targetOctagram) { 'Applied' } else { 'Reverted' }
        BeforeExeSha256 = $state.ExeState.Hash
        AfterExeSha256 = $targetExeHash
        PetXmlSha256 = $targetXmlHash
        Effect0004Sha256 = if ($targetOctagram) {
            $state.AssetSpec.Hash
        }
        else { $null }
        AssetPackage = if ($targetOctagram) { 'Fixed' } else { 'Absent' }
        AssetUpgradeRequired = $false
        BackupDirectory = $backupDirectory
        ChangedExeBytes = 139
        ClonedPetRows = if ($targetOctagram) { 90 } else { 0 }
        ManualRealmSelectionPreserved = $state.ExeState.Manual
        CharacterBackGuardPreserved = $state.ExeState.Back
        Effect0002Palette = $state.Effect0002Palette
    }
}
