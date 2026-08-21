Set-StrictMode -Version Latest

function Convert-ToSid200FrameV1FixtureXml(
    [string]$Text,
    [string]$Locale
) {
    $Text = Convert-PersonalInfoXml $Text $Locale $true
    $Text = Update-RebornPersonalInfoRectangle $Text (
        'Rectangle="100,100,454,652"') (
        'Rectangle="100,100,440,652"') 'frame-v1 fixture bounds'
    return Update-RebornPersonalInfoRectangle $Text (
        'BtnRect="287,13,324,50"') (
        'BtnRect="273,13,310,50"') 'frame-v1 fixture close button'
}

function Set-Sid200FrameV1Fixture([string]$Root) {
    Assert-Equal (& $script:speedPatcher -ClientRoot $Root -Mode Status).State (
        'Original') 'Frame-v1 fixture source is stock'
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $Root "Localization\$locale\UI\XML"
        $xmlPath = Join-Path $directory 'PersonalInfoUI.xml'
        $xml = Convert-ToSid200FrameV1FixtureXml (
            [IO.File]::ReadAllText($xmlPath, $script:encoding)) $locale
        Assert-Equal (Get-PersonalInfoXmlState $xml) (
            'PatchedSid200FrameV1') "$locale synthetic frame-v1 XML"
        Assert-True (-not (Test-RebornPersonalInfoFrameInsets (
                    (Get-RebornPersonalInfoXmlValidation $xml).Document))) (
            "$locale frame-v1 exposes the six-pixel inset regression")
        Write-FixtureUtf8PreservingBom $xmlPath $xml

        $constellationPath = Join-Path $directory 'Constellation.lua'
        $constellation = Convert-ConstellationStatsLua (
            [IO.File]::ReadAllText($constellationPath, $script:encoding)) $true
        Write-FixtureUtf8PreservingBom $constellationPath $constellation
        [IO.File]::WriteAllBytes((Join-Path $directory (
                    'PersonalInfoSpeedStats.lua')), (Get-Utf8Bytes (
                    Get-PersonalInfoStatsLua $locale) $false))
    }
}

function Invoke-CharacterStatsFrameTests {
    $fixture = New-ClientFixture 'sid200-frame-v1-migration'
    $backups = Join-Path $script:testRoot 'backups-sid200-frame-v1'
    Normalize-Original $fixture $backups
    $original = Get-CharacterStatsTrackedSnapshot $fixture
    Set-Sid200FrameV1Fixture $fixture

    $predecessor = & $script:speedPatcher -ClientRoot $fixture -Mode Status
    Assert-Equal $predecessor.State 'PatchedSid200FrameV1' (
        'Deployed frame predecessor is a recognized combined state')
    Assert-Equal $predecessor.WindowRectangle (
        '100,100,440,652 (frame v1)') 'Frame predecessor status bounds'

    & $script:speedPatcher -ClientRoot $fixture -Mode Apply `
        -BackupRoot $backups | Out-Null
    Assert-Sid200Patched $fixture
    Assert-ExplicitWidenedLayout $fixture
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $fixture (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $xml = [IO.File]::ReadAllText($path, $script:encoding)
        $document = (Get-RebornPersonalInfoXmlValidation $xml).Document
        Assert-True (Test-RebornPersonalInfoFrameInsets $document) (
            "$locale canonical 20/30/16 frame inset invariant")
    }

    & $script:speedPatcher -ClientRoot $fixture -Mode Revert `
        -BackupRoot $backups | Out-Null
    Assert-CharacterStatsTrackedSnapshot $fixture $original (
        'Frame-v1 migration and Revert restore stock bytes')
}
