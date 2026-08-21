$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetOwnerMergeVisual.ps1'
$colorPatcher = Join-Path $PSScriptRoot `
    'PatchClientPetOwnerMergeEffectColor.ps1'
$installed = 'C:\Godswar Origin'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'reborn-owner-merge-visual-' + [guid]::NewGuid().ToString('N'))
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

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    if ($Offset + $Expected.Length -gt $Data.Length) { return $false }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Hex([string]$Value) {
    $compact = $Value -replace '\s', ''
    [byte[]]$result = for ($index = 0; $index -lt $compact.Length;
        $index += 2) {
        [Convert]::ToByte($compact.Substring($index, 2), 16)
    }
    return ,$result
}

function Convert-HexBytes([string]$Value) { return ,(Hex $Value) }

function Copy-Bytes([byte[]]$Source, [byte[]]$Destination, [int]$Offset) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-EffectCounts([string]$Path) {
    $text = [Text.Encoding]::UTF8.GetString(
        [IO.File]::ReadAllBytes($Path))
    $result = @{}
    foreach ($samsara in @(0, 8, 20)) {
        $matches = [regex]::Matches(
            $text,
            '<PetModel Samsara="' + $samsara +
                '"[^>]*unitefile="\\\\Characters\\\\PetUniteEffect\\\\' +
                'e_he_(?<tier>\d{4})_all\.gwm"')
        $result[$samsara] = @($matches | ForEach-Object {
            $_.Groups['tier'].Value
        })
    }
    return $result
}

$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'RealmComposite.States.ps1')
. (Join-Path $helperRoot 'RealmComposite.TestFixtures.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Binary.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Xml.ps1')

try {
    $handlerStack = 0
    $selectorStoredOffset = $handlerStack - 4 + 0x1A
    $scalerReadOffset = $handlerStack - 4 - 4 - 32 + 0x3E
    $betweenHookStackDelta = -4 + 4 - 8 + 8
    Assert-Equal $selectorStoredOffset 0x16 `
        'selector writes rebirth at handler stack S+0x16'
    Assert-Equal $scalerReadOffset $selectorStoredOffset `
        'scaler reads the same rebirth byte after CALL, pushfd, and pushad'
    Assert-Equal $betweenHookStackDelta 0 `
        'intervening stdcall argument pushes are callee-balanced'

    foreach ($relative in @(
            'Localization\en_us\Settings\Sys',
            'Localization\zh_cn\Settings\Sys',
            'Characters\PetUniteEffect')) {
        [IO.Directory]::CreateDirectory(
            (Join-Path $testRoot $relative)) | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $installed 'Origin.exe') `
        -Destination (Join-Path $testRoot 'Origin.exe')
    foreach ($locale in @('en_us', 'zh_cn')) {
        Copy-Item -LiteralPath (Join-Path $installed (
                "Localization\$locale\Settings\Sys\Pet.xml")) `
            -Destination (Join-Path $testRoot (
                "Localization\$locale\Settings\Sys\Pet.xml"))
    }
    foreach ($asset in @(
            'e_he_0001_all.gwm',
            'e_he_0002_all.gwm',
            'e_he_0003_all.gwm')) {
        Copy-Item -LiteralPath (Join-Path $installed (
                "Characters\PetUniteEffect\$asset")) `
            -Destination (Join-Path $testRoot (
                "Characters\PetUniteEffect\$asset"))
    }

    $exePath = Join-Path $testRoot 'Origin.exe'
    [byte[]]$installedExe = [IO.File]::ReadAllBytes($exePath)
    $compositeStates = Get-RealmCompositeStateMap
    $installedHash = Get-PetOwnerMergeOctagramSha256 $installedExe
    if ($compositeStates.ContainsKey($installedHash)) {
        $installedState = $compositeStates[$installedHash]
        [IO.File]::WriteAllBytes(
            $exePath,
            (New-RealmCompositeFixture $installedExe `
                $false $false $false))
        if ($installedState.OctagramPatched) {
            foreach ($locale in @('en_us', 'zh_cn')) {
                $path = Join-Path $testRoot `
                    "Localization\$locale\Settings\Sys\Pet.xml"
                [IO.File]::WriteAllBytes(
                    $path,
                    (Convert-PetOwnerMergeOctagramXml `
                        ([IO.File]::ReadAllBytes($path)) $false))
            }
        }
    }

    $initial = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    if ($initial.Status -eq 'Patched') {
        & $patcher -Mode Revert -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    }
    $sourceStatus = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $sourceStatus.Status 'Source' 'fixture starts at source'

    $xmlPaths = @('en_us', 'zh_cn') | ForEach-Object {
        Join-Path $testRoot "Localization\$_\Settings\Sys\Pet.xml"
    }
    [byte[]]$beforeExe = [IO.File]::ReadAllBytes($exePath)
    $beforeXml = @($xmlPaths | ForEach-Object {
        ,([IO.File]::ReadAllBytes($_))
    })
    $assetHashes = @{}
    foreach ($asset in @(
            'e_he_0001_all.gwm',
            'e_he_0002_all.gwm',
            'e_he_0003_all.gwm')) {
        $assetHashes[$asset] = (Get-FileHash -LiteralPath (
                Join-Path $testRoot "Characters\PetUniteEffect\$asset") `
            -Algorithm SHA256).Hash
    }

    $applied = & $patcher -Mode Apply -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $applied.Status 'Patched' 'Apply reports Patched'
    $patchedStatus = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $patchedStatus.Status 'Patched' 'Status sees Patched'
    Assert-True $patchedStatus.AssetsPreserved 'Status preserves assets'

    [byte[]]$afterExe = [IO.File]::ReadAllBytes($exePath)
    $hook1 = Hex (
        'E8 52 CE 29 00 ' + ('90 ' * 26))
    $hook2 = Hex 'E8 3B CE 29 00 90 90 90 90'
    Assert-True (Test-Bytes $afterExe 0x2A1729 $hook1) `
        'quality selector hook is exact'
    Assert-True (Test-Bytes $afterExe 0x2A1780 $hook2) `
        'effect scaler hook is exact'
    Assert-True (Test-Bytes $afterExe 0x2A1748 (Hex (
            '0FB64C24148BD00FB6C3508A442413E87487000085C07430' +
            '83C07C742B8378140074258378181072058B4004EB0383C0' +
            '045057E8C0FFDEFF'))) `
        'between-hook instructions have only balanced stdcall pushes'
    Assert-True (Test-Bytes $afterExe 0x2A9FF1 (Hex 'C20400')) `
        'profile lookup pops its one argument'
    Assert-True (Test-Bytes $afterExe 0x9198E (Hex 'C20800')) `
        'effect creation pops its two arguments'
    Assert-True (Test-Bytes $afterExe 0x53E580 (Hex (
            '8B4C24188A59088A51098854241A8A883C060000' +
            '8A9068060000884C24138854241880FB0C720D80FB0E7204' +
            'B314EB06B308EB0230DBE80503B6FFC3'))) `
        'quality selector helper is exact'
    Assert-True (Test-Bytes $afterExe 0x53E5C0 (Hex (
            '9C600FB644243EBA0000803F83F81E7219BA0000A03F' +
            '83F83C720FBA0000C03F83F85A7205BA00000040' +
            '8BB72C06000085F67408525252E81480D4FF619D' +
            '8B4E1C51E879E8D5FFC3'))) `
        'rebirth scaler helper is exact'

    for ($offset = 0; $offset -lt $beforeExe.Length; $offset++) {
        if ($beforeExe[$offset] -eq $afterExe[$offset]) { continue }
        $allowed = ($offset -ge 0x2A1729 -and $offset -lt 0x2A1748) -or
            ($offset -ge 0x2A1780 -and $offset -lt 0x2A1789) -or
            ($offset -ge 0x53E580 -and $offset -lt 0x53E650)
        Assert-True $allowed (
            'binary mutation remains in owned ranges at 0x{0:X}' -f $offset)
    }

    foreach ($xmlPath in $xmlPaths) {
        $counts = Get-EffectCounts $xmlPath
        Assert-Equal $counts[0].Count 90 'Samsara 0 row count'
        Assert-Equal $counts[8].Count 90 'Samsara 8 row count'
        Assert-Equal $counts[20].Count 90 'Samsara 20 row count'
        Assert-Equal @($counts[0] | Where-Object { $_ -ne '0001' }).Count 0 `
            'all Samsara 0 rows use effect 0001'
        Assert-Equal @($counts[8] | Where-Object { $_ -ne '0002' }).Count 0 `
            'all Samsara 8 rows use effect 0002'
        Assert-Equal @($counts[20] | Where-Object { $_ -ne '0003' }).Count 0 `
            'all Samsara 20 rows use effect 0003'
    }
    foreach ($asset in $assetHashes.Keys) {
        Assert-Equal (Get-FileHash -LiteralPath (Join-Path $testRoot (
                    "Characters\PetUniteEffect\$asset")) `
                -Algorithm SHA256).Hash $assetHashes[$asset] `
            "$asset asset hash"
    }

    $basePatchedXml = @($xmlPaths | ForEach-Object {
        ,([IO.File]::ReadAllBytes($_))
    })
    $octagramXml = @($basePatchedXml | ForEach-Object {
        ,(Convert-PetOwnerMergeOctagramXml $_ $true)
    })
    $octagramAsset = Join-Path $testRoot `
        'Characters\PetUniteEffect\e_he_0004_all.gwm'
    $canonicalOctagramAsset = Join-Path (Split-Path -Parent $PSScriptRoot) `
        'assets\pet-owner-merge\e_he_0004_all.gwm'
    $revertDependencyMessage =
        'Cannot revert the base owner-Merge visual from a composite state. ' +
        'Revert the octagram first, then Character Back and manual realm ' +
        'selection in either order, then revert the base visual.'
    foreach ($composite in $compositeStates.Values) {
        [byte[]]$fixtureExe = New-RealmCompositeFixture `
            $afterExe $composite.ManualPatched $composite.GuardPatched `
            $composite.OctagramPatched
        Assert-Equal (Get-PetOwnerMergeOctagramSha256 $fixtureExe) `
            $composite.Hash "composite fixture $($composite.Name)"
        [IO.File]::WriteAllBytes($exePath, $fixtureExe)
        for ($index = 0; $index -lt 2; $index++) {
            [IO.File]::WriteAllBytes(
                $xmlPaths[$index],
                [byte[]]$(if ($composite.OctagramPatched) {
                    $octagramXml[$index]
                }
                else { $basePatchedXml[$index] }))
        }
        if ($composite.OctagramPatched) {
            Copy-Item -LiteralPath $canonicalOctagramAsset `
                -Destination $octagramAsset
        }
        elseif (Test-Path -LiteralPath $octagramAsset) {
            Remove-Item -LiteralPath $octagramAsset -Force
        }

        $compositeStatus = & $patcher -Mode Status `
            -ClientRoot $testRoot -BackupRoot $backupRoot
        Assert-Equal $compositeStatus.Status 'Patched' `
            "Status recognizes $($composite.Name)"
        Assert-Equal $compositeStatus.ManualRealmSelection `
            $composite.ManualPatched "manual flag $($composite.Name)"
        Assert-Equal $compositeStatus.CharacterBackGuard `
            $composite.GuardPatched "Back flag $($composite.Name)"
        Assert-Equal $compositeStatus.PetOwnerMergeOctagram `
            $(if ($composite.OctagramPatched) {
                'Applied'
            }
            else { 'Reverted' }) "octagram flag $($composite.Name)"
        $already = & $patcher -Mode Apply -ClientRoot $testRoot `
            -BackupRoot $backupRoot
        Assert-Equal $already.Status 'Already Patched' `
            "Apply is read-only for $($composite.Name)"
        Assert-Equal $already.PetOwnerMergeOctagram `
            $compositeStatus.PetOwnerMergeOctagram `
            "Apply reports octagram $($composite.Name)"

        if ($composite.Hash -ne
            '74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C') {
            $message = ''
            try {
                & $patcher -Mode Revert -ClientRoot $testRoot `
                    -BackupRoot $backupRoot | Out-Null
            }
            catch { $message = $_.Exception.Message }
            Assert-Equal $message $revertDependencyMessage `
                "Revert dependency order $($composite.Name)"
            Assert-Equal (Get-FileHash -LiteralPath $exePath `
                -Algorithm SHA256).Hash $composite.Hash `
                "rejected Revert preserves $($composite.Name)"
        }
    }
    [IO.File]::WriteAllBytes($exePath, $afterExe)
    for ($index = 0; $index -lt 2; $index++) {
        [IO.File]::WriteAllBytes(
            $xmlPaths[$index], [byte[]]$basePatchedXml[$index])
    }
    if (Test-Path -LiteralPath $octagramAsset) {
        Remove-Item -LiteralPath $octagramAsset -Force
    }

    $reverted = & $patcher -Mode Revert -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $reverted.Status 'Source' 'Revert reports Source'
    [byte[]]$revertedExe = [IO.File]::ReadAllBytes($exePath)
    Assert-True ([Linq.Enumerable]::SequenceEqual(
            [byte[]]$beforeExe, [byte[]]$revertedExe)) `
        'Revert restores exact executable'
    for ($index = 0; $index -lt $xmlPaths.Count; $index++) {
        Assert-True ([Linq.Enumerable]::SequenceEqual(
                [byte[]]$beforeXml[$index],
                [byte[]][IO.File]::ReadAllBytes($xmlPaths[$index]))) `
            "Revert restores exact $($xmlPaths[$index])"
    }

    $initialPalette = (& $colorPatcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot).Status
    $paletteMode = if ($initialPalette -eq 'Stock') { 'Apply' } else { 'Revert' }
    $alternatePalette = if ($initialPalette -eq 'Stock') { 'Purple' } else { 'Stock' }
    & $colorPatcher -Mode $paletteMode -ClientRoot $testRoot `
        -BackupRoot $backupRoot | Out-Null
    $alternateVisual = & $patcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot
    Assert-Equal $alternateVisual.Status 'Source' `
        'visual patch accepts the alternate audited 0002 palette'
    Assert-Equal $alternateVisual.Effect0002Palette $alternatePalette `
        'visual status reports the alternate 0002 palette'
    $restoreMode = if ($initialPalette -eq 'Stock') { 'Revert' } else { 'Apply' }
    & $colorPatcher -Mode $restoreMode -ClientRoot $testRoot `
        -BackupRoot $backupRoot | Out-Null
    Assert-Equal (& $colorPatcher -Mode Status -ClientRoot $testRoot `
        -BackupRoot $backupRoot).Status $initialPalette `
        'palette integration check restores its initial state'

    [byte[]]$partial = [IO.File]::ReadAllBytes($exePath)
    $partial[0x2A1729] = 0x90
    [IO.File]::WriteAllBytes($exePath, $partial)
    $rejected = $false
    try {
        & $patcher -Mode Status -ClientRoot $testRoot `
            -BackupRoot $backupRoot | Out-Null
    }
    catch { $rejected = $true }
    Assert-True $rejected 'partial executable state is rejected'

    Write-Host "Owner-Merge visual patch passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith(
                $temp,
                [StringComparison]::OrdinalIgnoreCase) -or
            $resolved.Length -le $temp.Length + 10) {
            throw "Refusing to remove unexpected test path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
