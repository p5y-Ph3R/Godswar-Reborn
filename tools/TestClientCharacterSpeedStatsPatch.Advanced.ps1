Set-StrictMode -Version Latest

function Get-CharacterStatsTrackedFiles([string]$Root) {
    $paths = [Collections.Generic.List[string]]::new()
    $paths.Add((Join-Path $Root 'Origin.exe'))
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $Root "Localization\$locale\UI\XML"
        foreach ($name in 'PersonalInfoUI.xml', 'Constellation.lua',
            'PersonalInfoSpeedStats.lua') {
            $paths.Add((Join-Path $directory $name))
        }
    }
    return $paths.ToArray()
}

function Get-CharacterStatsTrackedSnapshot([string]$Root) {
    $snapshot = @{}
    foreach ($path in Get-CharacterStatsTrackedFiles $Root) {
        $snapshot[$path] = if (Test-Path -LiteralPath $path -PathType Leaf) {
            'present:' + (Get-FileSha256 $path)
        } else { 'absent' }
    }
    return $snapshot
}

function Assert-CharacterStatsTrackedSnapshot(
    [string]$Root,
    [hashtable]$Expected,
    [string]$Label
) {
    foreach ($path in Get-CharacterStatsTrackedFiles $Root) {
        $actual = if (Test-Path -LiteralPath $path -PathType Leaf) {
            'present:' + (Get-FileSha256 $path)
        } else { 'absent' }
        Assert-Equal $actual $Expected[$path] (
            "$Label $(Split-Path $path -Leaf)")
    }
}

function Write-FixtureUtf8PreservingBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllBytes($Path, (Get-Utf8Bytes $Text (Test-Utf8Bom $Path)))
}

function Set-FixturePersonalInfoRootAttributes(
    [string]$Path,
    [string]$Attributes
) {
    $text = [IO.File]::ReadAllText($Path, $script:encoding)
    $text = Update-RegexOnce $text (
        '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*Visible="0">[ \t]*(?=\r?\n|\z)') {
        param($line)
        $line.Replace(' Visible="0">', " $Attributes Visible=`"0`">")
    } 'fixture PersonalInfo root attributes'
    Write-FixtureUtf8PreservingBom $Path $text
}

function Assert-FixtureApplyRejected(
    [string]$Root,
    [string]$BackupRoot,
    [string]$Label
) {
    $before = Get-CharacterStatsTrackedSnapshot $Root
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $Root -Mode Apply `
            -BackupRoot $BackupRoot | Out-Null
    } 'unknown or partially applied' $Label
    Assert-CharacterStatsTrackedSnapshot $Root $before (
        "$Label leaves all targets unchanged")
}

function Invoke-CharacterStatsAdvancedTests {
    $rootCallbacks = New-ClientFixture 'root-callback-rejection'
    $rootCallbackBackups = Join-Path $script:testRoot 'backups-root-callback'
    Normalize-Original $rootCallbacks $rootCallbackBackups
    Set-FixturePersonalInfoRootAttributes (Join-Path $rootCallbacks (
            'Localization\en_us\UI\XML\PersonalInfoUI.xml')) (
        'ONLOAD = "OtherOwnerLoad()" onclose = "OtherOwnerClose()"')
    Assert-FixtureApplyRejected $rootCallbacks $rootCallbackBackups (
        'Whitespace/case root callbacks fail closed')

    foreach ($control in 'BaseBack', 'Recommend') {
        $sentinelRoot = New-ClientFixture (
            $control.ToLowerInvariant() + '-owner-sentinel')
        $sentinelBackups = Join-Path $script:testRoot (
            'backups-' + $control.ToLowerInvariant() + '-sentinel')
        Normalize-Original $sentinelRoot $sentinelBackups
        $path = Join-Path $sentinelRoot (
            'Localization\en_us\UI\XML\PersonalInfoUI.xml')
        $text = [IO.File]::ReadAllText($path, $script:encoding)
        $matches = Get-RebornXmlElementLines $text $control
        Assert-Equal $matches.Count 1 "$control sentinel source cardinality"
        $changed = $matches[0].Value.Replace(' />',
            ' CustomOwner="keep" />')
        $text = $text.Remove($matches[0].Index, $matches[0].Length).Insert(
            $matches[0].Index, $changed)
        Write-FixtureUtf8PreservingBom $path $text
        Assert-FixtureApplyRejected $sentinelRoot $sentinelBackups (
            "$control unrelated attribute fails closed")
    }

    $badBounds = New-ClientFixture 'adversarial-root-bounds'
    $badBoundsBackups = Join-Path $script:testRoot 'backups-bad-bounds'
    Normalize-Original $badBounds $badBoundsBackups
    $badBoundsPath = Join-Path $badBounds (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $badBoundsText = [IO.File]::ReadAllText($badBoundsPath, $script:encoding)
    $badBoundsText = Update-RegexOnce $badBoundsText (
        '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*>[ \t]*(?=\r?\n|\z)') {
        param($line)
        $line.Replace('Rectangle="100,100,363,626"',
            'Rectangle="100,100,363,625"')
    } 'adversarial root bounds'
    $baseLine = (Get-RebornXmlElementLines $badBoundsText 'BaseBack')[0]
    $unrelated = '    <UnrelatedOwner Rectangle="100,100,363,626" />' +
        "`r`n" + $baseLine.Value
    $badBoundsText = $badBoundsText.Remove(
        $baseLine.Index, $baseLine.Length).Insert($baseLine.Index, $unrelated)
    Write-FixtureUtf8PreservingBom $badBoundsPath $badBoundsText
    Assert-FixtureApplyRejected $badBounds $badBoundsBackups (
        'Unrelated stock rectangle cannot mask a bad PersonalInfo root')

    $duplicateScript = New-ClientFixture 'duplicate-owned-script'
    $duplicateScriptBackups = Join-Path $script:testRoot 'backups-duplicate-script'
    Normalize-Original $duplicateScript $duplicateScriptBackups
    & $script:speedPatcher -ClientRoot $duplicateScript -Mode Apply `
        -BackupRoot $duplicateScriptBackups | Out-Null
    $duplicateScriptPath = Join-Path $duplicateScript (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $duplicateScriptText = [IO.File]::ReadAllText(
        $duplicateScriptPath, $script:encoding)
    $ownedScript = [regex]::Match($duplicateScriptText,
        '(?m)^[ \t]*<Script\b[^\r\n]*PersonalInfoSpeedStats\.lua[^\r\n]*/>')
    $duplicateScriptText = $duplicateScriptText.Insert(
        $ownedScript.Index, $ownedScript.Value + "`r`n")
    Write-FixtureUtf8PreservingBom $duplicateScriptPath $duplicateScriptText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $duplicateScript -Mode Status |
            Out-Null
    } 'unknown or partially applied' 'Duplicate owned Script rejection'

    $duplicateControl = New-ClientFixture 'duplicate-owned-control'
    $duplicateControlBackups = Join-Path $script:testRoot 'backups-duplicate-control'
    Normalize-Original $duplicateControl $duplicateControlBackups
    & $script:speedPatcher -ClientRoot $duplicateControl -Mode Apply `
        -BackupRoot $duplicateControlBackups | Out-Null
    $duplicateControlPath = Join-Path $duplicateControl (
        'Localization\zh_cn\UI\XML\PersonalInfoUI.xml')
    $duplicateControlText = [IO.File]::ReadAllText(
        $duplicateControlPath, $script:encoding)
    $ownedControl = (Get-RebornXmlElementLines $duplicateControlText (
            'RebornPersonalInfoStatsUpdater'))[0]
    $duplicateControlText = $duplicateControlText.Insert(
        $ownedControl.Index, $ownedControl.Value + "`r`n")
    Write-FixtureUtf8PreservingBom $duplicateControlPath $duplicateControlText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $duplicateControl -Mode Status |
            Out-Null
    } 'unknown or partially applied' 'Duplicate owned control rejection'

    $wrongLocale = New-ClientFixture 'wrong-owned-locale-content'
    $wrongLocaleBackups = Join-Path $script:testRoot 'backups-wrong-locale'
    Normalize-Original $wrongLocale $wrongLocaleBackups
    & $script:speedPatcher -ClientRoot $wrongLocale -Mode Apply `
        -BackupRoot $wrongLocaleBackups | Out-Null
    $wrongLocalePath = Join-Path $wrongLocale (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $wrongLocaleText = [IO.File]::ReadAllText(
        $wrongLocalePath, $script:encoding)
    $wrongLocaleText = $wrongLocaleText.Replace(
        'Text="Speed" Visible="1" CanHovered=',
        'Text="Wrong Speed" Visible="1" CanHovered=').Replace(
        'Text="Pen." CanHovered=', 'Text="Wrong Pen." CanHovered=').Replace(
        './Localization/en_us/UI/XML/PersonalInfoSpeedStats.lua',
        './Localization/zh_cn/UI/XML/PersonalInfoSpeedStats.lua')
    $wrongBase = (Get-RebornXmlElementLines $wrongLocaleText 'BaseBack')[0]
    $unrelatedLabels = '    <UnrelatedOwner Text="Speed Pen. ./Localization/en_us/UI/XML/PersonalInfoSpeedStats.lua" />' +
        "`r`n" + $wrongBase.Value
    $wrongLocaleText = $wrongLocaleText.Remove(
        $wrongBase.Index, $wrongBase.Length).Insert(
        $wrongBase.Index, $unrelatedLabels)
    Write-FixtureUtf8PreservingBom $wrongLocalePath $wrongLocaleText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $wrongLocale -Mode Status |
            Out-Null
    } 'wrong SID200 labels' (
        'Unrelated labels cannot mask wrong owned locale content')

    $relocatedBranch = New-ClientFixture 'relocated-constellation-branch'
    $relocatedBackups = Join-Path $script:testRoot 'backups-relocated-branch'
    Normalize-Original $relocatedBranch $relocatedBackups
    & $script:speedPatcher -ClientRoot $relocatedBranch -Mode Apply `
        -BackupRoot $relocatedBackups | Out-Null
    $relocatedPath = Join-Path $relocatedBranch (
        'Localization\en_us\UI\XML\Constellation.lua')
    $relocatedText = [IO.File]::ReadAllText($relocatedPath, $script:encoding)
    $relocatedNewLine = if ($relocatedText.Contains("`r`n")) {
        "`r`n"
    } else { "`n" }
    $relocatedOwnedBranch = Get-ConstellationStatsBranch $relocatedNewLine
    $relocatedText = Replace-RegexOnce $relocatedText (
        [regex]::Escape($relocatedOwnedBranch + $relocatedNewLine)) '' (
        'relocated Constellation branch source')
    $relocatedText += $relocatedNewLine + $relocatedOwnedBranch +
        $relocatedNewLine
    Write-FixtureUtf8PreservingBom $relocatedPath $relocatedText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $relocatedBranch -Mode Status |
            Out-Null
    } 'unknown or partially applied SID200' (
        'Relocated Constellation branch rejection')

    $caseMarkers = New-ClientFixture 'case-only-constellation-markers'
    $caseMarkerBackups = Join-Path $script:testRoot 'backups-case-markers'
    Normalize-Original $caseMarkers $caseMarkerBackups
    $caseMarkerPath = Join-Path $caseMarkers (
        'Localization\zh_cn\UI\XML\Constellation.lua')
    $caseMarkerText = Convert-ConstellationStatsLua (
        [IO.File]::ReadAllText($caseMarkerPath, $script:encoding)) $true
    foreach ($marker in @(
        'REBORN_PERSONAL_INFO_STATS_PRELUDE_BEGIN',
        'REBORN_PERSONAL_INFO_STATS_PRELUDE_END',
        'REBORN_PERSONAL_INFO_STATS_BRANCH_BEGIN',
        'REBORN_PERSONAL_INFO_STATS_BRANCH_END')) {
        $caseMarkerText = $caseMarkerText.Replace(
            $marker, $marker.ToLowerInvariant())
    }
    Write-FixtureUtf8PreservingBom $caseMarkerPath $caseMarkerText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $caseMarkers -Mode Status |
            Out-Null
    } 'unknown or partially applied SID200' (
        'Case-only Constellation marker rejection')

    $caseXml = New-ClientFixture 'case-only-xml-corruption'
    $caseXmlBackups = Join-Path $script:testRoot 'backups-case-xml'
    Normalize-Original $caseXml $caseXmlBackups
    & $script:speedPatcher -ClientRoot $caseXml -Mode Apply `
        -BackupRoot $caseXmlBackups | Out-Null
    $caseXmlPath = Join-Path $caseXml (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $caseXmlText = [IO.File]::ReadAllText($caseXmlPath, $script:encoding)
    $caseXmlText = $caseXmlText.Replace('RebornPersonalInfoStatsUpdate()',
        'rebornPersonalInfoStatsUpdate()')
    Write-FixtureUtf8PreservingBom $caseXmlPath $caseXmlText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $caseXml -Mode Status | Out-Null
    } 'unknown or partially applied' 'Case-only XML callback rejection'

    $caseLua = New-ClientFixture 'case-only-lua-corruption'
    $caseLuaBackups = Join-Path $script:testRoot 'backups-case-lua'
    Normalize-Original $caseLua $caseLuaBackups
    & $script:speedPatcher -ClientRoot $caseLua -Mode Apply `
        -BackupRoot $caseLuaBackups | Out-Null
    $caseLuaPath = Join-Path $caseLua (
        'Localization\zh_cn\UI\XML\PersonalInfoSpeedStats.lua')
    $caseLuaText = [IO.File]::ReadAllText($caseLuaPath, $script:encoding)
    $caseLuaText = $caseLuaText.Replace('GameAPI:ConsEventRequest',
        'GameAPI:consEventRequest')
    Write-FixtureUtf8PreservingBom $caseLuaPath $caseLuaText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $caseLua -Mode Status | Out-Null
    } 'unknown content' 'Case-only GameAPI request rejection'

    $rollback = New-ClientFixture 'transaction-rollback'
    $rollbackBackups = Join-Path $script:testRoot 'backups-rollback'
    Normalize-Original $rollback $rollbackBackups
    $rollbackBefore = Get-CharacterStatsTrackedSnapshot $rollback
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $rollback -Mode Apply `
            -BackupRoot $rollbackBackups -InternalTestFailAfterWrite 3 |
            Out-Null
    } 'Injected character-stat transaction failure after write 3' (
        'Injected mid-transaction failure')
    Assert-CharacterStatsTrackedSnapshot $rollback $rollbackBefore (
        'Rollback restores verified byte-exact pre-state')
    Assert-Equal (& $script:speedPatcher -ClientRoot $rollback `
        -Mode Status).State 'Original' 'Rollback state is Original'

    $fashionFirst = New-ClientFixture 'fashion-before-legacy-speed'
    $fashionFirstBackups = Join-Path $script:testRoot 'backups-fashion-first'
    Normalize-Original $fashionFirst $fashionFirstBackups
    Set-FashionFixture $fashionFirst
    Set-LegacyPartialFixture $fashionFirst
    & $script:speedPatcher -ClientRoot $fashionFirst -Mode Apply `
        -BackupRoot $fashionFirstBackups | Out-Null
    Assert-FashionFixture $fashionFirst 'SID200 Apply preserves'
    & $script:speedPatcher -ClientRoot $fashionFirst -Mode Revert `
        -BackupRoot $fashionFirstBackups | Out-Null
    Assert-FashionFixture $fashionFirst 'SID200 Revert preserves'

    $concurrent = New-ClientFixture 'compatible-concurrent-owner'
    $concurrentBackups = Join-Path $script:testRoot 'backups-concurrent-owner'
    Normalize-Original $concurrent $concurrentBackups
    Set-LegacyPartialFixture $concurrent
    $concurrentTargets = @{}
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $concurrent "Localization\$locale\UI\XML"
        $xmlPath = Join-Path $directory 'PersonalInfoUI.xml'
        $xml = [IO.File]::ReadAllText($xmlPath, $script:encoding)
        $xml = Update-RegexOnce $xml (
            '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*Visible="0">[ \t]*(?=\r?\n|\z)') {
            param($line)
            $line.Replace(' Visible="0">',
                ' ConcurrentOwner="keep" Visible="0">')
        } 'concurrent fixture root attribute'
        $concurrentTargets[$xmlPath] = Get-Utf8Bytes $xml (
            Test-Utf8Bom $xmlPath)
        $constellationPath = Join-Path $directory 'Constellation.lua'
        $constellation = [IO.File]::ReadAllText(
            $constellationPath, $script:encoding)
        $concurrentTargets[$constellationPath] = Get-Utf8Bytes (
            $constellation + "`r`n-- concurrent owner sentinel`r`n") (
            Test-Utf8Bom $constellationPath)
    }
    $concurrentFashion = Get-FashionPatchProfile
    $concurrentCallback = {
        $exe = Join-Path $concurrent 'Origin.exe'
        [byte[]]$bytes = [IO.File]::ReadAllBytes($exe)
        [Array]::Copy($concurrentFashion.Hook, 0, $bytes,
            $concurrentFashion.HookOffset, $concurrentFashion.Hook.Length)
        [Array]::Copy($concurrentFashion.Cave, 0, $bytes,
            $concurrentFashion.CaveOffset, $concurrentFashion.Cave.Length)
        [IO.File]::WriteAllBytes($exe, $bytes)
        foreach ($path in $concurrentTargets.Keys) {
            [IO.File]::WriteAllBytes($path, $concurrentTargets[$path])
        }
    }.GetNewClosure()
    & $script:speedPatcher -ClientRoot $concurrent -Mode Apply `
        -BackupRoot $concurrentBackups `
        -InternalTestBeforeBackup $concurrentCallback | Out-Null
    Assert-Sid200Patched $concurrent
    Assert-FashionFixture $concurrent 'Concurrent Apply preserves'
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $concurrent "Localization\$locale\UI\XML"
        Assert-True ([IO.File]::ReadAllText((Join-Path $directory (
                        'PersonalInfoUI.xml')), $script:encoding).Contains(
                'ConcurrentOwner="keep"')) (
            "$locale compatible XML edit is regenerated from backup")
        Assert-True ([IO.File]::ReadAllText((Join-Path $directory (
                        'Constellation.lua')), $script:encoding).Contains(
                '-- concurrent owner sentinel')) (
            "$locale compatible Constellation edit is regenerated from backup")
    }
    & $script:speedPatcher -ClientRoot $concurrent -Mode Revert `
        -BackupRoot $concurrentBackups | Out-Null
    Assert-FashionFixture $concurrent 'Concurrent Revert preserves'
    Assert-Equal (& $script:speedPatcher -ClientRoot $concurrent `
        -Mode Status).State 'Original' 'Concurrent-owner round trip is Original'

    $mainSource = [IO.File]::ReadAllText($script:speedPatcher)
    Assert-Equal ([regex]::Matches($mainSource,
            'Test-TargetClientRunning \$clientExe').Count) 2 (
        'Running-client guard is checked before staging and before writes')
    Assert-True $mainSource.Contains(
        'one executable path could not be verified') (
        'Inaccessible Origin process path fails closed')
}
