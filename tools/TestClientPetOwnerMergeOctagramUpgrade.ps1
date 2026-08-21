$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Binary.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Xml.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Asset.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Transaction.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Upgrade.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Patch.ps1')

$installed = 'C:\Godswar Origin'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'reborn-octagram-upgrade-' + [guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $testRoot 'backups'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Label" }
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    $script:assertions++
    if ($Actual -ne $Expected) {
        throw "Assertion failed: $Label; expected=$Expected actual=$Actual"
    }
}

function Assert-BytesEqual(
    [byte[]]$Actual,
    [byte[]]$Expected,
    [string]$Label
) {
    Assert-True ([Linq.Enumerable]::SequenceEqual($Actual, $Expected)) $Label
}

function Assert-Rejected([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action }
    catch { $rejected = $true }
    Assert-True $rejected $Label
}

function Copy-UpgradeFixtureFile([string]$RelativePath) {
    $source = Join-Path $installed $RelativePath
    $target = Join-Path $testRoot $RelativePath
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) |
        Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}

function Invoke-UpgradeFixturePatch(
    [string]$Mode,
    [string]$FaultBoundary = 'None',
    [object]$Trace = $null
) {
    Invoke-PetOwnerMergeOctagramPatch `
        -ClientRoot $testRoot -Mode $Mode -BackupRoot $backupRoot `
        -RepositoryRoot $repositoryRoot -TestFaultBoundary $FaultBoundary `
        -TestTrace $Trace -TestFixtureMode $true
}

try {
    $livePaths = @(
        (Join-Path $installed 'Origin.exe'),
        (Join-Path $installed 'Localization\en_us\Settings\Sys\Pet.xml'),
        (Join-Path $installed 'Localization\zh_cn\Settings\Sys\Pet.xml'),
        (Join-Path $installed `
            'Characters\PetUniteEffect\e_he_0002_all.gwm'),
        (Join-Path $installed `
            'Characters\PetUniteEffect\e_he_0004_all.gwm'))
    $liveBefore = @($livePaths | ForEach-Object {
        [pscustomobject]@{
            Exists = Test-Path -LiteralPath $_ -PathType Leaf
            Data = if (Test-Path -LiteralPath $_ -PathType Leaf) {
                [IO.File]::ReadAllBytes($_)
            }
            else { $null }
        }
    })
    foreach ($relative in @(
        'Origin.exe',
        'Localization\en_us\Settings\Sys\Pet.xml',
        'Localization\zh_cn\Settings\Sys\Pet.xml',
        'Characters\PetUniteEffect\e_he_0001_all.gwm',
        'Characters\PetUniteEffect\e_he_0002_all.gwm',
        'Characters\PetUniteEffect\e_he_0003_all.gwm')) {
        Copy-UpgradeFixtureFile $relative
    }
    $binarySpec = Get-PetOwnerMergeOctagramBinarySpec
    $assetSpec = Get-PetOwnerMergeOctagramAssetSpec $repositoryRoot
    $fixtureExe = Join-Path $testRoot 'Origin.exe'
    $fixtureXml = @('en_us', 'zh_cn') | ForEach-Object {
        Join-Path $testRoot "Localization\$_\Settings\Sys\Pet.xml"
    }
    $fixture0002 = Join-Path $testRoot `
        'Characters\PetUniteEffect\e_he_0002_all.gwm'
    $fixture0004 = Join-Path $testRoot `
        'Characters\PetUniteEffect\e_he_0004_all.gwm'
    [byte[]]$revertedExe = Convert-PetOwnerMergeOctagramExe `
        ([IO.File]::ReadAllBytes($fixtureExe)) $binarySpec $false
    [byte[]]$appliedExe = Convert-PetOwnerMergeOctagramExe `
        $revertedExe $binarySpec $true
    $revertedXml = @($fixtureXml | ForEach-Object {
        ,(Convert-PetOwnerMergeOctagramXml `
            ([IO.File]::ReadAllBytes($_)) $false)
    })
    $appliedXml = @($revertedXml | ForEach-Object {
        ,(Convert-PetOwnerMergeOctagramXml $_ $true)
    })

    $legacyGenerator = Join-Path $PSScriptRoot `
        'pet_owner_merge_effect\legacy_fixture.py'
    $legacyPath = Join-Path $testRoot 'legacy-fixture.gwm'
    & python $legacyGenerator --canonical $assetSpec.CanonicalPath `
        --output $legacyPath
    Assert-Equal $LASTEXITCODE 0 'legacy fixture generator succeeds'
    [byte[]]$legacyAsset = [IO.File]::ReadAllBytes($legacyPath)
    Assert-Equal $legacyAsset.Length $assetSpec.LegacyLength `
        'legacy fixture length'
    Assert-Equal (Get-PetOwnerMergeOctagramSha256 $legacyAsset) `
        $assetSpec.LegacyHash 'legacy fixture hash'
    Assert-Equal (Get-PetOwnerMergeOctagramInstalledAssetState `
        $legacyAsset $assetSpec 'Generated legacy fixture') `
        'LegacyCrossScanline' 'legacy fixture classification'
    Remove-Item -LiteralPath $legacyPath -Force

    [IO.File]::WriteAllBytes($fixtureExe, $appliedExe)
    for ($index = 0; $index -lt 2; $index++) {
        [IO.File]::WriteAllBytes($fixtureXml[$index], $appliedXml[$index])
    }
    [IO.File]::WriteAllBytes($fixture0004, $legacyAsset)
    [byte[]]$preservedExe = [IO.File]::ReadAllBytes($fixtureExe)
    $preservedXml = @($fixtureXml | ForEach-Object {
        ,([IO.File]::ReadAllBytes($_))
    })
    [byte[]]$preserved0002 = [IO.File]::ReadAllBytes($fixture0002)

    $legacyStatus = Invoke-UpgradeFixturePatch Status
    Assert-Equal $legacyStatus.PetOwnerMergeOctagram 'Applied' `
        'legacy state remains logically applied'
    Assert-Equal $legacyStatus.AssetPackage 'LegacyCrossScanline' `
        'Status names rejected legacy package'
    Assert-True $legacyStatus.AssetUpgradeRequired `
        'Status requires scanline-safe asset upgrade'
    Assert-Equal $legacyStatus.Effect0004Sha256 $assetSpec.LegacyHash `
        'Status reports exact legacy package hash'

    $upgraded = Invoke-UpgradeFixturePatch Apply
    Assert-Equal $upgraded.PetOwnerMergeOctagram 'Applied' `
        'asset-only upgrade remains Applied'
    Assert-Equal $upgraded.AssetPackage 'Fixed' `
        'asset-only upgrade reports Fixed package'
    Assert-True (-not $upgraded.AssetUpgradeRequired) `
        'asset-only upgrade clears required flag'
    Assert-Equal $upgraded.BeforeEffect0004Sha256 $assetSpec.LegacyHash `
        'upgrade records legacy source hash'
    Assert-Equal $upgraded.Effect0004Sha256 $assetSpec.Hash `
        'upgrade records fixed target hash'
    Assert-Equal (Get-FileHash -LiteralPath $fixture0004 `
        -Algorithm SHA256).Hash $assetSpec.Hash `
        'upgrade installs exact scanline-safe package'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureExe)) $preservedExe `
        'upgrade does not rewrite executable'
    for ($index = 0; $index -lt 2; $index++) {
        Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureXml[$index])) `
            $preservedXml[$index] "upgrade does not rewrite XML $index"
    }
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixture0002)) `
        $preserved0002 'upgrade preserves effect 0002 palette'
    $upgradeBackupAsset = Join-Path $upgraded.BackupDirectory `
        'e_he_0004_all.gwm'
    Assert-Equal (Get-FileHash -LiteralPath $upgradeBackupAsset `
        -Algorithm SHA256).Hash $assetSpec.LegacyHash `
        'verified backup retains exact legacy package'
    $recoveryAssets = @(Get-ChildItem -LiteralPath $upgraded.BackupDirectory `
        -Filter 'replaced-legacy-e_he_0004_all-*.gwm')
    Assert-Equal $recoveryAssets.Count 1 `
        'displaced legacy package remains recoverable'
    Assert-Equal (Get-FileHash -LiteralPath $recoveryAssets[0].FullName `
        -Algorithm SHA256).Hash $assetSpec.LegacyHash `
        'recoverable displaced legacy package is exact'
    Assert-Equal (Invoke-UpgradeFixturePatch Apply).Status 'Already Applied' `
        'fixed package Apply is idempotent'

    [IO.File]::WriteAllBytes($fixture0004, $legacyAsset)
    $trace = [Collections.Generic.List[string]]::new()
    Assert-Rejected {
        Invoke-UpgradeFixturePatch Apply 'Upgrade.Asset' $trace | Out-Null
    } 'upgrade commit fault is surfaced'
    Assert-Equal ($trace -join '|') `
        'commit:Upgrade.Asset|fault:Upgrade.Asset|rollback:asset' `
        'upgrade rollback order'
    Assert-Equal (Get-FileHash -LiteralPath $fixture0004 `
        -Algorithm SHA256).Hash $assetSpec.LegacyHash `
        'upgrade fault atomically restores legacy package'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureExe)) $preservedExe `
        'upgrade fault preserves executable'
    for ($index = 0; $index -lt 2; $index++) {
        Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureXml[$index])) `
            $preservedXml[$index] "upgrade fault preserves XML $index"
    }
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixture0002)) `
        $preserved0002 'upgrade fault preserves effect 0002'

    $peerRaceCases = @(
        [pscustomobject]@{
            Label = 'executable'
            Path = $fixtureExe
            Bytes = $preservedExe
        },
        [pscustomobject]@{
            Label = 'en_us XML'
            Path = $fixtureXml[0]
            Bytes = $preservedXml[0]
        },
        [pscustomobject]@{
            Label = 'zh_cn XML'
            Path = $fixtureXml[1]
            Bytes = $preservedXml[1]
        },
        [pscustomobject]@{
            Label = 'effect 0002 palette'
            Path = $fixture0002
            Bytes = $preserved0002
        })
    foreach ($raceCase in $peerRaceCases) {
        [byte[]]$raceBytes = $raceCase.Bytes.Clone()
        $last = $raceBytes.Length - 1
        $raceBytes[$last] = $raceBytes[$last] -bxor 1
        $raceState = Get-PetOwnerMergeOctagramPatchState `
            $testRoot $repositoryRoot
        $racePath = $raceCase.Path
        $raceMessage = ''
        try {
            Invoke-PetOwnerMergeOctagramAssetUpgrade `
                -State $raceState -BackupRoot $backupRoot `
                -RepositoryRoot $repositoryRoot -TestFaultBoundary None `
                -TestTrace $null -TestFixtureMode $true `
                -TestAfterAssetCommit {
                    [IO.File]::WriteAllBytes($racePath, $raceBytes)
                } | Out-Null
        }
        catch { $raceMessage = $_.Exception.Message }
        Assert-Equal $raceMessage `
            'Octagram asset upgrade rolled back, but a peer changed concurrently.' `
            "peer race is reported: $($raceCase.Label)"
        Assert-Equal (Get-FileHash -LiteralPath $fixture0004 `
            -Algorithm SHA256).Hash $assetSpec.LegacyHash `
            "peer race restores legacy package: $($raceCase.Label)"
        Assert-BytesEqual ([IO.File]::ReadAllBytes($racePath)) $raceBytes `
            "peer race never clobbers peer: $($raceCase.Label)"
        [IO.File]::WriteAllBytes($racePath, [byte[]]$raceCase.Bytes)
    }
    Assert-Equal (Invoke-UpgradeFixturePatch Status).AssetPackage `
        'LegacyCrossScanline' 'all peer-race fixtures restore coherent input'

    $revertTrace = [Collections.Generic.List[string]]::new()
    Assert-Rejected {
        Invoke-UpgradeFixturePatch Revert 'Revert.Asset' $revertTrace |
            Out-Null
    } 'legacy Revert asset fault is surfaced'
    Assert-Equal ($revertTrace -join '|') (
        'commit:Revert.Exe|commit:Revert.XmlEn|commit:Revert.XmlZh|' +
        'commit:Revert.Asset|fault:Revert.Asset|rollback:asset|' +
        'rollback:xml:zh_cn|rollback:xml:en_us|rollback:exe') `
        'legacy Revert rollback order'
    Assert-Equal (Get-FileHash -LiteralPath $fixture0004 `
        -Algorithm SHA256).Hash $assetSpec.LegacyHash `
        'legacy Revert rollback restores exact package'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureExe)) $preservedExe `
        'legacy Revert rollback restores executable'
    for ($index = 0; $index -lt 2; $index++) {
        Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureXml[$index])) `
            $preservedXml[$index] "legacy Revert rollback restores XML $index"
    }

    $reverted = Invoke-UpgradeFixturePatch Revert
    Assert-Equal $reverted.PetOwnerMergeOctagram 'Reverted' `
        'legacy package can be safely reverted'
    Assert-True (-not (Test-Path -LiteralPath $fixture0004)) `
        'legacy Revert moves package out of client tree'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureExe)) $revertedExe `
        'legacy Revert restores selector-off executable'
    for ($index = 0; $index -lt 2; $index++) {
        Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureXml[$index])) `
            $revertedXml[$index] "legacy Revert restores XML $index"
    }

    Assert-True (@(Get-ChildItem -LiteralPath $testRoot -Recurse `
        -File | Where-Object Name -like '*.stage*').Count -eq 0) `
        'migration paths leave no temporary stages'
    for ($index = 0; $index -lt $livePaths.Count; $index++) {
        $exists = Test-Path -LiteralPath $livePaths[$index] -PathType Leaf
        Assert-Equal $exists $liveBefore[$index].Exists `
            "live existence is unchanged $index"
        if ($exists) {
            Assert-BytesEqual ([IO.File]::ReadAllBytes($livePaths[$index])) `
                $liveBefore[$index].Data "live bytes are unchanged $index"
        }
    }
    foreach ($path in @(
        $PSCommandPath,
        $legacyGenerator,
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Asset.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Upgrade.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Patch.ps1'))) {
        Assert-True ((Get-Item -LiteralPath $path).Length -lt 20KB) `
            "maintainability size cap: $([IO.Path]::GetFileName($path))"
    }

    Write-Host "Owner-Merge octagram asset upgrade passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith(
                $temp, [StringComparison]::OrdinalIgnoreCase) -or
            $resolved.Length -le $temp.Length + 10) {
            throw "Refusing to remove unexpected test path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
