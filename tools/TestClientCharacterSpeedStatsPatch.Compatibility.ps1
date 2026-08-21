Set-StrictMode -Version Latest

function Invoke-CharacterStatsCompatibilityTests {
    foreach ($variant in 'spaced', 'commented', 'same-line') {
        $root = New-ClientFixture ('personalinfo-close-' + $variant)
        $backups = Join-Path $script:testRoot (
            'backups-personalinfo-close-' + $variant)
        Normalize-Original $root $backups
        $path = Join-Path $root (
            'Localization\en_us\UI\XML\PersonalInfoUI.xml')
        $text = [IO.File]::ReadAllText($path, $script:encoding)
        $newLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $replacement = switch ($variant) {
            'spaced' { '</PersonalInfo   >' }
            'commented' { '</PersonalInfo><!-- harmless -->' }
            default { '</PersonalInfo></UIConfig>' }
        }
        if ($variant -eq 'same-line') {
            $text = $text.Replace(
                '</PersonalInfo>' + $newLine + '</UIConfig>', $replacement)
        } else {
            $text = $text.Replace('</PersonalInfo>', $replacement)
        }
        Write-FixtureUtf8PreservingBom $path $text
        Assert-FixtureApplyRejected $root $backups (
            "Noncanonical PersonalInfo closing tag $variant fails closed")
    }

    $constellationEof = New-ClientFixture 'constellation-smsg-at-eof'
    $constellationEofBackups = Join-Path $script:testRoot (
        'backups-constellation-smsg-at-eof')
    Normalize-Original $constellationEof $constellationEofBackups
    $constellationEofPath = Join-Path $constellationEof (
        'Localization\en_us\UI\XML\Constellation.lua')
    [IO.File]::WriteAllBytes($constellationEofPath, (Get-Utf8Bytes (
                'function SMsg(sid,v1,v2,v3)') (
                Test-Utf8Bom $constellationEofPath)))
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $constellationEof -Mode Status |
            Out-Null
    } 'canonical SMsg definition' (
        'Constellation SMsg-at-EOF state fails closed before conversion')

    $constellationSpaces = New-ClientFixture 'constellation-smsg-spaces'
    $constellationSpacesBackups = Join-Path $script:testRoot (
        'backups-constellation-smsg-spaces')
    Normalize-Original $constellationSpaces $constellationSpacesBackups
    $constellationSpacesPath = Join-Path $constellationSpaces (
        'Localization\en_us\UI\XML\Constellation.lua')
    $constellationSpacesText = [IO.File]::ReadAllText(
        $constellationSpacesPath, $script:encoding).Replace(
        'function SMsg(sid,v1,v2,v3)',
        'function SMsg(sid,v1,v2,v3)   ')
    Write-FixtureUtf8PreservingBom $constellationSpacesPath (
        $constellationSpacesText)
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $constellationSpaces -Mode Status |
            Out-Null
    } 'canonical SMsg definition' (
        'Constellation trailing-space SMsg fails closed without byte loss')

    $constellationBreak = New-ClientFixture 'constellation-smsg-line-break'
    $constellationBreakBackups = Join-Path $script:testRoot (
        'backups-constellation-smsg-line-break')
    Normalize-Original $constellationBreak $constellationBreakBackups
    $constellationBreakPath = Join-Path $constellationBreak (
        'Localization\en_us\UI\XML\Constellation.lua')
    $constellationBreakText = [IO.File]::ReadAllText(
        $constellationBreakPath, $script:encoding).Replace(
        "function SMsg(sid,v1,v2,v3)`r`n",
        "function SMsg(sid,v1,v2,v3)`n")
    Write-FixtureUtf8PreservingBom $constellationBreakPath (
        $constellationBreakText)
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $constellationBreak -Mode Status |
            Out-Null
    } 'canonical SMsg definition' (
        'Constellation mismatched SMsg line break fails closed')

    $xmlBreak = New-ClientFixture 'xml-owned-row-line-breaks'
    $xmlBreakBackups = Join-Path $script:testRoot (
        'backups-xml-owned-row-line-breaks')
    Normalize-Original $xmlBreak $xmlBreakBackups
    $xmlBreakPath = Join-Path $xmlBreak (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $xmlBreakText = [IO.File]::ReadAllText($xmlBreakPath, $script:encoding)
    foreach ($pattern in @(
        '(?m)^[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n',
        '(?m)^[ \t]*</PersonalInfo>[ \t]*\r?\n')) {
        $xmlBreakText = Update-RegexOnce $xmlBreakText $pattern {
            param($lineAndBreak)
            if ($lineAndBreak.EndsWith("`r`n")) {
                return $lineAndBreak.Substring(0, $lineAndBreak.Length - 2) +
                    "`n"
            }
            return $lineAndBreak.Substring(0, $lineAndBreak.Length - 1) +
                "`r`n"
        } 'owned XML anchor line-break fixture'
    }
    Write-FixtureUtf8PreservingBom $xmlBreakPath $xmlBreakText
    $xmlBreakOriginal = Get-CharacterStatsTrackedSnapshot $xmlBreak
    Assert-Equal (& $script:speedPatcher -ClientRoot $xmlBreak `
        -Mode Status).State 'Original' (
        'Per-anchor XML line breaks remain a compatible stock state')
    & $script:speedPatcher -ClientRoot $xmlBreak -Mode Apply `
        -BackupRoot $xmlBreakBackups | Out-Null
    Assert-Sid200Patched $xmlBreak
    & $script:speedPatcher -ClientRoot $xmlBreak -Mode Revert `
        -BackupRoot $xmlBreakBackups | Out-Null
    Assert-CharacterStatsTrackedSnapshot $xmlBreak $xmlBreakOriginal (
        'Owned XML anchor line breaks survive byte-exact Apply/Revert')
}
