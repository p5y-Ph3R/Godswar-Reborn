Set-StrictMode -Version Latest

function Invoke-CharacterStatsXmlSecurityTests {
    foreach ($variant in 'original', 'patched') {
        $commentRoot = New-ClientFixture ("commented-baseback-$variant")
        $commentBackups = Join-Path $script:testRoot (
            "backups-commented-baseback-$variant")
        Normalize-Original $commentRoot $commentBackups
        if ($variant -eq 'patched') {
            & $script:speedPatcher -ClientRoot $commentRoot -Mode Apply `
                -BackupRoot $commentBackups | Out-Null
        }
        $commentPath = Join-Path $commentRoot (
            'Localization\en_us\UI\XML\PersonalInfoUI.xml')
        $commentText = [IO.File]::ReadAllText($commentPath, $script:encoding)
        $base = (Get-RebornXmlElementLines $commentText 'BaseBack')[0]
        $newLine = if ($commentText.Contains("`r`n")) { "`r`n" } else { "`n" }
        $commentText = $commentText.Remove($base.Index, $base.Length).Insert(
            $base.Index, '<!--' + $newLine + $base.Value + $newLine + '-->')
        Write-FixtureUtf8PreservingBom $commentPath $commentText
        Assert-Throws {
            & $script:speedPatcher -ClientRoot $commentRoot -Mode Status |
                Out-Null
        } 'unknown or partially applied' (
            "Commented live BaseBack rejection in $variant layout")
    }

    $commentDecoy = New-ClientFixture 'comment-decoy-live-control'
    $commentDecoyBackups = Join-Path $script:testRoot 'backups-comment-decoy'
    Normalize-Original $commentDecoy $commentDecoyBackups
    $commentDecoyPath = Join-Path $commentDecoy (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $commentDecoyText = [IO.File]::ReadAllText(
        $commentDecoyPath, $script:encoding)
    $decoyBase = (Get-RebornXmlElementLines $commentDecoyText 'BaseBack')[0]
    $decoyNewLine = if ($commentDecoyText.Contains("`r`n")) {
        "`r`n"
    } else { "`n" }
    $decoyReplacement = '<!--' + $decoyNewLine + $decoyBase.Value +
        $decoyNewLine + '-->' + $decoyNewLine +
        '    <BaseBack' + $decoyNewLine +
        '        Template="WrongOwner" ID="-1" Rectangle="1,1,2,2" />'
    $commentDecoyText = $commentDecoyText.Remove(
        $decoyBase.Index, $decoyBase.Length).Insert(
        $decoyBase.Index, $decoyReplacement)
    Write-FixtureUtf8PreservingBom $commentDecoyPath $commentDecoyText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $commentDecoy -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Commented canonical line cannot mask a live multiline decoy')

    $commentRootDecoy = New-ClientFixture 'comment-decoy-live-root'
    $commentRootBackups = Join-Path $script:testRoot 'backups-comment-root'
    Normalize-Original $commentRootDecoy $commentRootBackups
    $commentRootPath = Join-Path $commentRootDecoy (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $commentRootText = [IO.File]::ReadAllText(
        $commentRootPath, $script:encoding)
    $decoyRoot = (Get-RebornXmlElementLines $commentRootText 'PersonalInfo')[0]
    $rootNewLine = if ($commentRootText.Contains("`r`n")) {
        "`r`n"
    } else { "`n" }
    $rootReplacement = '<!--' + $rootNewLine + $decoyRoot.Value +
        $rootNewLine + '-->' + $rootNewLine +
        '  <PersonalInfo' + $rootNewLine +
        '      Template="WrongOwner" Rectangle="1,1,2,2" Visible="0">'
    $commentRootText = $commentRootText.Remove(
        $decoyRoot.Index, $decoyRoot.Length).Insert(
        $decoyRoot.Index, $rootReplacement)
    Write-FixtureUtf8PreservingBom $commentRootPath $commentRootText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $commentRootDecoy -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Commented canonical root cannot mask a live multiline root')

    $processingDecoy = New-ClientFixture 'processing-instruction-decoy'
    $processingBackups = Join-Path $script:testRoot 'backups-processing-decoy'
    Normalize-Original $processingDecoy $processingBackups
    $processingPath = Join-Path $processingDecoy (
        'Localization\zh_cn\UI\XML\PersonalInfoUI.xml')
    $processingText = [IO.File]::ReadAllText(
        $processingPath, $script:encoding)
    $processingBase = (Get-RebornXmlElementLines $processingText 'BaseBack')[0]
    $processingNewLine = if ($processingText.Contains("`r`n")) {
        "`r`n"
    } else { "`n" }
    $processingReplacement = '<?owner' + $processingNewLine +
        $processingBase.Value + $processingNewLine + '?>' +
        $processingNewLine + '    <BaseBack' + $processingNewLine +
        '        Template="WrongOwner" ID="-1" Rectangle="1,1,2,2" />'
    $processingText = $processingText.Remove(
        $processingBase.Index, $processingBase.Length).Insert(
        $processingBase.Index, $processingReplacement)
    Write-FixtureUtf8PreservingBom $processingPath $processingText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $processingDecoy -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Processing-instruction canonical decoy rejection')

    $malformed = New-ClientFixture 'malformed-personal-info-xml'
    $malformedBackups = Join-Path $script:testRoot 'backups-malformed-xml'
    Normalize-Original $malformed $malformedBackups
    $malformedPath = Join-Path $malformed (
        'Localization\zh_cn\UI\XML\PersonalInfoUI.xml')
    $malformedText = [IO.File]::ReadAllText($malformedPath, $script:encoding)
    $malformedText = Replace-RegexOnce $malformedText (
        '(?m)^[ \t]*</PersonalInfo>[ \t]*\r?\n') '' (
        'malformed fixture closing PersonalInfo')
    Write-FixtureUtf8PreservingBom $malformedPath $malformedText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $malformed -Mode Status | Out-Null
    } 'well-formed XML' 'Missing PersonalInfo close-tag rejection'

    $caseUpdater = New-ClientFixture 'case-variant-updater-tag'
    $caseUpdaterBackups = Join-Path $script:testRoot 'backups-case-updater'
    Normalize-Original $caseUpdater $caseUpdaterBackups
    & $script:speedPatcher -ClientRoot $caseUpdater -Mode Apply `
        -BackupRoot $caseUpdaterBackups | Out-Null
    $caseUpdaterPath = Join-Path $caseUpdater (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $caseUpdaterText = [IO.File]::ReadAllText(
        $caseUpdaterPath, $script:encoding)
    $updater = (Get-RebornXmlElementLines $caseUpdaterText (
            'RebornPersonalInfoStatsUpdater'))[0]
    $caseVariant = $updater.Value.Replace(
        '<RebornPersonalInfoStatsUpdater',
        '<rebornPersonalInfoStatsUpdater')
    $caseUpdaterText = $caseUpdaterText.Insert(
        $updater.Index, $caseVariant + "`r`n")
    Write-FixtureUtf8PreservingBom $caseUpdaterPath $caseUpdaterText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $caseUpdater -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Case-variant owned updater cardinality rejection')

    $caseCallback = New-ClientFixture 'case-variant-owned-callback'
    $caseCallbackBackups = Join-Path $script:testRoot 'backups-case-callback'
    Normalize-Original $caseCallback $caseCallbackBackups
    $caseCallbackPath = Join-Path $caseCallback (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $caseCallbackText = [IO.File]::ReadAllText(
        $caseCallbackPath, $script:encoding)
    $callbackBase = (Get-RebornXmlElementLines $caseCallbackText 'BaseBack')[0]
    $callbackElement =
        '    <OtherOwner OnUpdate="rebornPersonalInfoStatsUpdate()" />' +
        "`r`n" + $callbackBase.Value
    $caseCallbackText = $caseCallbackText.Remove(
        $callbackBase.Index, $callbackBase.Length).Insert(
        $callbackBase.Index, $callbackElement)
    Write-FixtureUtf8PreservingBom $caseCallbackPath $caseCallbackText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $caseCallback -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Case-variant owned callback on unrelated tag rejection')

    $spacedCallback = New-ClientFixture 'spaced-owned-callback'
    $spacedCallbackBackups = Join-Path $script:testRoot 'backups-spaced-callback'
    Normalize-Original $spacedCallback $spacedCallbackBackups
    $spacedCallbackPath = Join-Path $spacedCallback (
        'Localization\zh_cn\UI\XML\PersonalInfoUI.xml')
    $spacedCallbackText = [IO.File]::ReadAllText(
        $spacedCallbackPath, $script:encoding)
    $spacedBase = (Get-RebornXmlElementLines $spacedCallbackText 'BaseBack')[0]
    $spacedElement =
        '    <OtherOwner OnUpdate="RebornPersonalInfoStatsUpdate ( )" />' +
        "`r`n" + $spacedBase.Value
    $spacedCallbackText = $spacedCallbackText.Remove(
        $spacedBase.Index, $spacedBase.Length).Insert(
        $spacedBase.Index, $spacedElement)
    Write-FixtureUtf8PreservingBom $spacedCallbackPath $spacedCallbackText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $spacedCallback -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Whitespace-obfuscated owned callback identifier rejection')

    $argumentCallback = New-ClientFixture 'argument-owned-callback'
    $argumentBackups = Join-Path $script:testRoot 'backups-argument-callback'
    Normalize-Original $argumentCallback $argumentBackups
    $argumentPath = Join-Path $argumentCallback (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $argumentText = [IO.File]::ReadAllText($argumentPath, $script:encoding)
    $argumentBase = (Get-RebornXmlElementLines $argumentText 'BaseBack')[0]
    $argumentElement =
        '    <OtherOwner OnUpdate="RebornPersonalInfoStatsUpdate(1)" />' +
        "`r`n" + $argumentBase.Value
    $argumentText = $argumentText.Remove(
        $argumentBase.Index, $argumentBase.Length).Insert(
        $argumentBase.Index, $argumentElement)
    Write-FixtureUtf8PreservingBom $argumentPath $argumentText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $argumentCallback -Mode Status |
            Out-Null
    } 'unknown or partially applied' (
        'Argument-bearing owned callback identifier rejection')

    $bomHelper = New-ClientFixture 'bom-owned-helper'
    $bomHelperBackups = Join-Path $script:testRoot 'backups-bom-helper'
    Normalize-Original $bomHelper $bomHelperBackups
    & $script:speedPatcher -ClientRoot $bomHelper -Mode Apply `
        -BackupRoot $bomHelperBackups | Out-Null
    $bomHelperPath = Join-Path $bomHelper (
        'Localization\en_us\UI\XML\PersonalInfoSpeedStats.lua')
    [byte[]]$bomBody = [IO.File]::ReadAllBytes($bomHelperPath)
    [byte[]]$bomBytes = [byte[]]::new($bomBody.Length + 3)
    $bomBytes[0] = 0xEF
    $bomBytes[1] = 0xBB
    $bomBytes[2] = 0xBF
    [Array]::Copy($bomBody, 0, $bomBytes, 3, $bomBody.Length)
    [IO.File]::WriteAllBytes($bomHelperPath, $bomBytes)
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $bomHelper -Mode Status | Out-Null
    } 'exact BOM-less UTF-8' 'BOM-bearing owned helper rejection'

    $utf16Helper = New-ClientFixture 'utf16-owned-helper'
    $utf16HelperBackups = Join-Path $script:testRoot 'backups-utf16-helper'
    Normalize-Original $utf16Helper $utf16HelperBackups
    & $script:speedPatcher -ClientRoot $utf16Helper -Mode Apply `
        -BackupRoot $utf16HelperBackups | Out-Null
    $utf16HelperPath = Join-Path $utf16Helper (
        'Localization\zh_cn\UI\XML\PersonalInfoSpeedStats.lua')
    $utf16Text = [IO.File]::ReadAllText($utf16HelperPath, $script:encoding)
    [byte[]]$utf16Body = [Text.Encoding]::Unicode.GetBytes($utf16Text)
    [byte[]]$utf16Bytes = [byte[]]::new($utf16Body.Length + 2)
    $utf16Bytes[0] = 0xFF
    $utf16Bytes[1] = 0xFE
    [Array]::Copy($utf16Body, 0, $utf16Bytes, 2, $utf16Body.Length)
    [IO.File]::WriteAllBytes($utf16HelperPath, $utf16Bytes)
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $utf16Helper -Mode Status | Out-Null
    } 'exact BOM-less UTF-8' 'UTF-16 owned helper rejection'
}
