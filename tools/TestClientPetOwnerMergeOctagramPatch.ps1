$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetOwnerMergeOctagram.ps1'
$colorPatcher = Join-Path $PSScriptRoot `
    'PatchClientPetOwnerMergeEffectColor.ps1'
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
    'reborn-owner-merge-octagram-' + [guid]::NewGuid().ToString('N'))
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

function Assert-BytesEqual([byte[]]$Actual, [byte[]]$Expected, [string]$Label) {
    Assert-True ([Linq.Enumerable]::SequenceEqual($Actual, $Expected)) $Label
}

function Assert-Rejected([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action }
    catch { $rejected = $true }
    Assert-True $rejected $Label
}

function Copy-FixtureFile([string]$RelativePath) {
    $source = Join-Path $installed $RelativePath
    $target = Join-Path $testRoot $RelativePath
    [IO.Directory]::CreateDirectory(
        (Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}

function Set-PeerState(
    [byte[]]$Source,
    [object]$Spec,
    [bool]$Manual,
    [bool]$Back
) {
    [byte[]]$result = $Source.Clone()
    Copy-Bytes $(if ($Manual) {
            $Spec.ManualPatched
        }
        else { $Spec.ManualOriginal }) $result $Spec.ManualOffset
    Copy-Bytes $(if ($Back) {
            $Spec.BackPatchedHook
        }
        else { $Spec.BackOriginalHook }) $result $Spec.BackHookOffset
    Copy-Bytes $(if ($Back) {
            $Spec.BackPatchedCave
        }
        else { $Spec.BackOriginalCave }) $result $Spec.BackCaveOffset
    return ,$result
}

function Get-RowCount([byte[]]$Data, [int]$Samsara, [string]$Effect) {
    $text = [Text.Encoding]::UTF8.GetString($Data)
    return [regex]::Matches(
        $text,
        '<PetModel Samsara="' + $Samsara + '"[^>]*' +
            'unitefile="\\\\Characters\\\\PetUniteEffect\\\\' +
            [regex]::Escape($Effect) + '"').Count
}

function Assert-FixtureOctagramState([bool]$Applied, [string]$Label) {
    if ($Applied) {
        Assert-Equal (Get-FileHash -LiteralPath $fixtureExe `
            -Algorithm SHA256).Hash `
            'FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5' `
            "$Label executable"
        foreach ($path in $fixtureXml) {
            Assert-Equal (Get-FileHash -LiteralPath $path `
                -Algorithm SHA256).Hash `
                'A6BBB855D8DC1092B867A9DED096C42348C991D847AB0EBB93C3127D9A8A96BE' `
                "$Label XML"
        }
        Assert-True (Test-Path -LiteralPath $fixture0004 -PathType Leaf) `
            "$Label effect 0004 exists"
        Assert-Equal (Get-FileHash -LiteralPath $fixture0004 `
            -Algorithm SHA256).Hash $assetSpec.Hash `
            "$Label effect 0004"
    }
    else {
        Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureExe)) $sourceExe `
            "$Label executable"
        for ($index = 0; $index -lt 2; $index++) {
            Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureXml[$index])) `
                $sourceXml[$index] "$Label XML $index"
        }
        Assert-True (-not (Test-Path -LiteralPath $fixture0004)) `
            "$Label effect 0004 absent"
    }
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixture0002)) $source0002 `
        "$Label effect 0002 preserved"
}

function Invoke-OctagramFixturePatch(
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
    $binarySpec = Get-PetOwnerMergeOctagramBinarySpec
    $assetSpec = Get-PetOwnerMergeOctagramAssetSpec $repositoryRoot
    [byte[]]$canonicalAsset = Get-PetOwnerMergeOctagramCanonicalAsset $assetSpec
    Assert-Equal $canonicalAsset.Length 20816 'canonical GWM length'
    Assert-Equal (Get-PetOwnerMergeOctagramSha256 $canonicalAsset) `
        $assetSpec.Hash 'canonical GWM hash'

    foreach ($quality in 1..16) {
        foreach ($rebirths in @(0, 29, 30, 59, 60, 89, 90, 100)) {
            $expected = if ($quality -eq 16 -and $rebirths -ge 90) { 90 }
                elseif ($quality -lt 12) { 0 }
                elseif ($quality -lt 14) { 8 }
                else { 20 }
            Assert-Equal (Get-PetOwnerMergeOctagramSelector `
                $quality $rebirths) $expected `
                "selector quality=$quality rebirths=$rebirths"
        }
    }
    Assert-Equal (Get-PetOwnerMergeOctagramSelector 15 100) 20 `
        'Celestial 100 remains effect 0003'
    Assert-Equal (Get-PetOwnerMergeOctagramSelector 16 89) 20 `
        'Transcendent 89 remains effect 0003'
    Assert-Equal (Get-PetOwnerMergeOctagramSelector 16 90) 90 `
        'Transcendent 90 selects effect 0004'

    [byte[]]$installedExe = Convert-PetOwnerMergeOctagramExe `
        ([IO.File]::ReadAllBytes((Join-Path $installed 'Origin.exe'))) `
        $binarySpec $false
    foreach ($peer in @(
        @($false, $false,
          '74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C',
          '8D15E202D8178927E69F06909659EA14DD7FD0EE8BE853BD3394E5EEE684D31F'),
        @($true, $false,
          '9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA',
          '4EF7A3A5F62BB739081CD76425D4AF14BEFDB03D1F36DABECF66624B1C4BA2DB'),
        @($false, $true,
          'C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9',
          'FE01690D51B5A6C1FAEE48627372F35FFE9E110966E01F7D1EA96163EE8DEF61'),
        @($true, $true,
          '318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF',
          'FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5')
    )) {
        [byte[]]$old = Set-PeerState `
            $installedExe $binarySpec $peer[0] $peer[1]
        Assert-Equal (Get-PetOwnerMergeOctagramSha256 $old) $peer[2] `
            "old peer matrix $($peer[0])/$($peer[1])"
        [byte[]]$new = Convert-PetOwnerMergeOctagramExe `
            $old $binarySpec $true
        Assert-Equal (Get-PetOwnerMergeOctagramSha256 $new) $peer[3] `
            "new peer matrix $($peer[0])/$($peer[1])"
        Assert-Equal (Measure-ByteDifference $old $new) 139 `
            'executable mutation count'
        Assert-BytesEqual `
            $old[$binarySpec.ManualOffset..(
                $binarySpec.ManualOffset + 4)] `
            $new[$binarySpec.ManualOffset..(
                $binarySpec.ManualOffset + 4)] `
            'manual selection peer bytes preserved'
        Assert-BytesEqual `
            $old[$binarySpec.BackCaveOffset..(
                $binarySpec.BackCaveOffset + 111)] `
            $new[$binarySpec.BackCaveOffset..(
                $binarySpec.BackCaveOffset + 111)] `
            'character Back peer cave preserved'
        [byte[]]$roundTrip = Convert-PetOwnerMergeOctagramExe `
            $new $binarySpec $false
        Assert-BytesEqual $roundTrip $old 'binary exact round trip'
    }

    [byte[]]$oldXml = Convert-PetOwnerMergeOctagramXml `
        ([IO.File]::ReadAllBytes((Join-Path $installed `
            'Localization\en_us\Settings\Sys\Pet.xml'))) $false
    [byte[]]$newXml = Convert-PetOwnerMergeOctagramXml $oldXml $true
    Assert-Equal $newXml.Length 164158 'octagram Pet.xml length'
    Assert-Equal (Get-PetOwnerMergeOctagramSha256 $newXml) `
        'A6BBB855D8DC1092B867A9DED096C42348C991D847AB0EBB93C3127D9A8A96BE' `
        'octagram Pet.xml hash'
    Assert-Equal (Get-RowCount $newXml 0 'e_he_0001_all.gwm') 90 `
        'effect 0001 row count'
    Assert-Equal (Get-RowCount $newXml 8 'e_he_0002_all.gwm') 90 `
        'effect 0002 row count'
    Assert-Equal (Get-RowCount $newXml 20 'e_he_0003_all.gwm') 90 `
        'effect 0003 row count'
    Assert-Equal (Get-RowCount $newXml 90 'e_he_0004_all.gwm') 90 `
        'effect 0004 row count'
    Assert-BytesEqual (Convert-PetOwnerMergeOctagramXml $newXml $false) `
        $oldXml 'Pet.xml exact round trip'

    foreach ($relative in @(
        'Origin.exe',
        'Localization\en_us\Settings\Sys\Pet.xml',
        'Localization\zh_cn\Settings\Sys\Pet.xml',
        'Characters\PetUniteEffect\e_he_0001_all.gwm',
        'Characters\PetUniteEffect\e_he_0002_all.gwm',
        'Characters\PetUniteEffect\e_he_0003_all.gwm')) {
        Copy-FixtureFile $relative
    }
    $fixtureExe = Join-Path $testRoot 'Origin.exe'
    $fixtureXml = @('en_us', 'zh_cn') | ForEach-Object {
        Join-Path $testRoot "Localization\$_\Settings\Sys\Pet.xml"
    }
    $fixture0002 = Join-Path $testRoot `
        'Characters\PetUniteEffect\e_he_0002_all.gwm'
    $fixture0004 = Join-Path $testRoot `
        'Characters\PetUniteEffect\e_he_0004_all.gwm'
    [IO.File]::WriteAllBytes($fixtureExe, $installedExe)
    foreach ($path in $fixtureXml) {
        [IO.File]::WriteAllBytes($path, $oldXml)
    }
    $sourceExe = [IO.File]::ReadAllBytes($fixtureExe)
    $sourceXml = @($fixtureXml | ForEach-Object {
        ,([IO.File]::ReadAllBytes($_))
    })
    $source0002 = [IO.File]::ReadAllBytes($fixture0002)

    $status = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $status.PetOwnerMergeOctagram 'Reverted' `
        'fixture starts reverted'
    Assert-Equal $status.Effect0002Palette 'Purple' `
        'purple effect 0002 is accepted'
    $applied = Invoke-OctagramFixturePatch Apply
    Assert-Equal $applied.PetOwnerMergeOctagram 'Applied' `
        'Apply reports Applied'
    Assert-Equal $applied.AfterExeSha256 `
        'FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5' `
        'Apply preserves current manual/Back composition'
    Assert-True (Test-Path -LiteralPath $fixture0004 -PathType Leaf) `
        'Apply creates effect 0004'
    Assert-Equal (Get-FileHash -LiteralPath $fixture0004 `
        -Algorithm SHA256).Hash $assetSpec.Hash 'installed effect 0004 hash'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixture0002)) $source0002 `
        'Apply preserves purple effect 0002 bytes'
    Assert-Equal (Invoke-OctagramFixturePatch Apply).Status 'Already Applied' `
        'Apply is idempotent'

    $reverted = Invoke-OctagramFixturePatch Revert
    Assert-Equal $reverted.PetOwnerMergeOctagram 'Reverted' `
        'Revert reports Reverted'
    Assert-True (-not (Test-Path -LiteralPath $fixture0004)) `
        'Revert removes exact effect 0004'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureExe)) $sourceExe `
        'Revert restores exact executable'
    for ($index = 0; $index -lt 2; $index++) {
        Assert-BytesEqual ([IO.File]::ReadAllBytes($fixtureXml[$index])) `
            $sourceXml[$index] "Revert restores locale $index Pet.xml"
    }
    Assert-Equal (Invoke-OctagramFixturePatch Revert).Status 'Already Reverted' `
        'Revert is idempotent'

    $applyFaultCases = @(
        @('Apply.Asset',
          'commit:Apply.Asset|fault:Apply.Asset|rollback:asset'),
        @('Apply.XmlEn', (
          'commit:Apply.Asset|commit:Apply.XmlEn|fault:Apply.XmlEn|' +
          'rollback:xml:en_us|rollback:asset')),
        @('Apply.XmlZh', (
          'commit:Apply.Asset|commit:Apply.XmlEn|commit:Apply.XmlZh|' +
          'fault:Apply.XmlZh|rollback:xml:zh_cn|rollback:xml:en_us|' +
          'rollback:asset')),
        @('Apply.Exe', (
          'commit:Apply.Asset|commit:Apply.XmlEn|commit:Apply.XmlZh|' +
          'commit:Apply.Exe|fault:Apply.Exe|rollback:exe|' +
          'rollback:xml:zh_cn|rollback:xml:en_us|rollback:asset'))
    )
    foreach ($faultCase in $applyFaultCases) {
        $trace = [Collections.Generic.List[string]]::new()
        Assert-Rejected {
            Invoke-OctagramFixturePatch Apply $faultCase[0] $trace |
                Out-Null
        } "Apply fault $($faultCase[0]) is surfaced"
        Assert-Equal ($trace -join '|') $faultCase[1] `
            "Apply rollback order $($faultCase[0])"
        Assert-FixtureOctagramState $false `
            "Apply rollback bytes $($faultCase[0])"
    }

    $revertFaultCases = @(
        @('Revert.Exe', (
          'commit:Revert.Exe|fault:Revert.Exe|rollback:asset-ready|' +
          'rollback:exe')),
        @('Revert.XmlEn', (
          'commit:Revert.Exe|commit:Revert.XmlEn|fault:Revert.XmlEn|' +
          'rollback:asset-ready|rollback:xml:en_us|rollback:exe')),
        @('Revert.XmlZh', (
          'commit:Revert.Exe|commit:Revert.XmlEn|commit:Revert.XmlZh|' +
          'fault:Revert.XmlZh|rollback:asset-ready|' +
          'rollback:xml:zh_cn|rollback:xml:en_us|rollback:exe')),
        @('Revert.Asset', (
          'commit:Revert.Exe|commit:Revert.XmlEn|commit:Revert.XmlZh|' +
          'commit:Revert.Asset|fault:Revert.Asset|rollback:asset|' +
          'rollback:xml:zh_cn|rollback:xml:en_us|rollback:exe'))
    )
    foreach ($faultCase in $revertFaultCases) {
        Invoke-OctagramFixturePatch Apply | Out-Null
        $trace = [Collections.Generic.List[string]]::new()
        Assert-Rejected {
            Invoke-OctagramFixturePatch Revert $faultCase[0] $trace |
                Out-Null
        } "Revert fault $($faultCase[0]) is surfaced"
        Assert-Equal ($trace -join '|') $faultCase[1] `
            "Revert rollback order $($faultCase[0])"
        Assert-FixtureOctagramState $true `
            "Revert rollback bytes $($faultCase[0])"
        Invoke-OctagramFixturePatch Revert | Out-Null
    }

    $raceRecovery = Join-Path $backupRoot 'race-recovery.gwm'
    $heldCanonical = Join-Path $backupRoot 'race-held-canonical.gwm'
    $foreignStage = Join-Path $backupRoot 'race-foreign.gwm'
    [byte[]]$foreignAsset = [Text.Encoding]::ASCII.GetBytes(
        'foreign effect 0004 must survive the simulated path race')
    [IO.File]::WriteAllBytes($fixture0004, $canonicalAsset)
    [IO.File]::WriteAllBytes($foreignStage, $foreignAsset)
    Assert-Rejected {
        Move-PetOwnerMergeOctagramExactAssetToRecovery `
            $fixture0004 $raceRecovery $assetSpec.Hash $false {
                [IO.File]::Move($fixture0004, $heldCanonical)
                [IO.File]::Move($foreignStage, $fixture0004)
            } | Out-Null
    } 'foreign asset path race is rejected'
    Assert-BytesEqual ([IO.File]::ReadAllBytes($fixture0004)) $foreignAsset `
        'foreign raced asset is restored, not deleted'
    Assert-True (-not (Test-Path -LiteralPath $raceRecovery)) `
        'foreign raced asset does not remain at recovery path'
    Remove-Item -LiteralPath $fixture0004 -Force
    Remove-Item -LiteralPath $heldCanonical -Force

    $guardRoot = Join-Path $testRoot 'process-guard'
    [IO.Directory]::CreateDirectory($guardRoot) | Out-Null
    $guardExe = Join-Path $guardRoot 'Origin.exe'
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\ping.exe') `
        -Destination $guardExe
    $guardProcess = $null
    try {
        $guardProcess = Start-Process -FilePath $guardExe `
            -ArgumentList @('-n', '30', '127.0.0.1') -PassThru `
            -WindowStyle Hidden
        $guardCandidate = Get-Process -Id $guardProcess.Id
        Assert-Equal ([IO.Path]::GetFullPath($guardCandidate.Path)) `
            ([IO.Path]::GetFullPath($guardExe)) 'guard process exact path'
        $guardMessage = ''
        try {
            Assert-PetOwnerMergeOctagramClientClosed `
                $guardExe @($guardCandidate)
        }
        catch { $guardMessage = $_.Exception.Message }
        Assert-Equal $guardMessage `
            'Close Origin.exe before changing its Merge octagram visual.' `
            'exact fixture Origin process blocks mutation'

        $unknownMessage = ''
        try {
            Assert-PetOwnerMergeOctagramClientClosed $guardExe `
                @([pscustomobject]@{ Path = $null })
        }
        catch { $unknownMessage = $_.Exception.Message }
        Assert-Equal $unknownMessage `
            'A running Origin process has no inspectable path; refusing client mutation.' `
            'uninspectable Origin process fails closed'
    }
    finally {
        if ($null -ne $guardProcess) {
            if (-not $guardProcess.HasExited) {
                Stop-Process -Id $guardProcess.Id -Force
                $guardProcess.WaitForExit()
            }
            $guardProcess.Dispose()
        }
    }

    & $colorPatcher -Mode Revert -ClientRoot $testRoot `
        -BackupRoot $backupRoot | Out-Null
    $stockHash = $assetSpec.Effect0002Hashes.Stock
    Assert-Equal (Get-FileHash -LiteralPath $fixture0002 `
        -Algorithm SHA256).Hash $stockHash 'color fixture is stock'
    $stockStatus = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $stockStatus.Effect0002Palette 'Stock' `
        'stock effect 0002 is accepted'
    Invoke-OctagramFixturePatch Apply | Out-Null
    Assert-Equal (Get-FileHash -LiteralPath $fixture0002 `
        -Algorithm SHA256).Hash $stockHash 'Apply preserves stock effect 0002'
    Invoke-OctagramFixturePatch Revert | Out-Null
    Assert-Equal (Get-FileHash -LiteralPath $fixture0002 `
        -Algorithm SHA256).Hash $stockHash 'Revert preserves stock effect 0002'

    Copy-Item -LiteralPath $assetSpec.CanonicalPath -Destination $fixture0004
    Assert-Rejected {
        & $patcher -Mode Status -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    } 'mixed exact asset state is rejected'
    Remove-Item -LiteralPath $fixture0004 -Force

    [byte[]]$partial = [IO.File]::ReadAllBytes($fixtureExe)
    $partial[$binarySpec.Hook2Offset] = 0x90
    [IO.File]::WriteAllBytes($fixtureExe, $partial)
    Assert-Rejected {
        & $patcher -Mode Status -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    } 'partial executable state is rejected'
    [IO.File]::WriteAllBytes($fixtureExe, $sourceExe)

    $applyBackups = @(Get-ChildItem $backupRoot -Directory |
        Where-Object Name -like '*-apply-*')
    $revertBackups = @(Get-ChildItem $backupRoot -Directory |
        Where-Object Name -like '*-revert-*')
    Assert-True ($applyBackups.Count -ge 2) 'Apply creates verified backups'
    Assert-True ($revertBackups.Count -ge 2) 'Revert creates verified backups'

    foreach ($path in @(
        $patcher,
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Binary.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Xml.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Asset.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Transaction.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Upgrade.ps1'),
        (Join-Path $helperRoot 'PetOwnerMergeOctagram.Patch.ps1'),
        $PSCommandPath)) {
        Assert-True ((Get-Item -LiteralPath $path).Length -lt 20KB) `
            "maintainability size cap: $([IO.Path]::GetFileName($path))"
    }

    Write-Host "Owner-Merge octagram patch passed: $assertions assertions."
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
