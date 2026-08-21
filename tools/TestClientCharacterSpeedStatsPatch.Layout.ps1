Set-StrictMode -Version Latest

function Convert-ToSid200V1FixtureXml(
    [string]$Text,
    [string]$Locale
) {
    $newLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Text = Convert-PersonalInfoXml $Text $Locale $true
    $Text = Convert-RebornPersonalInfoRectangles $Text $false
    $Text = Update-RebornPersonalInfoRectangle $Text (
        'Rectangle="100,100,454,652"') (
        'Rectangle="100,100,363,652"') 'SID200 v1 fixture bounds'
    $Text = Update-RebornPersonalInfoRectangle $Text (
        'BtnRect="287,13,324,50"') (
        'BtnRect="196,13,233,50"') 'SID200 v1 fixture close button'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,536" />') (
        'SID200 v1 fixture BaseBack')
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,536" />') (
        'SID200 v1 fixture FightBack')
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <Recommend Template="T_Money" ID="281026" Rectangle="210,517,246,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1" />') (
        'SID200 v1 fixture Penetration value')
    $labels = Get-CharacterStatsUiText $Locale
    $rows = @(
        "    <spouse Template=`"T_Money`" Rectangle=`"24,517,78,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$($labels.Speed)`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoSpeedHovered()`" OnLeft=`"RebornPersonalInfoStatsLeft()`" />",
        '    <spouseText Template="T_Money" Rectangle="85,517,125,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1"/>',
        "    <Penetration Template=`"T_Money`" Rectangle=`"137,517,200,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$($labels.Penetration)`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoPenetrationHovered()`" OnLeft=`"RebornPersonalInfoStatsLeft()`" />",
        '    <RebornPersonalInfoStatsUpdater Type="Text" ID="-1" Rectangle="1,1,2,2" Text="" Visible="1" OnUpdate="RebornPersonalInfoStatsUpdate()" />'
    ) -join $newLine
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<Penetration\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RebornPersonalInfoStatsUpdater\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        $rows) 'SID200 v1 fixture rows'
    return $Text
}

function Set-Sid200V1Fixture([string]$Root) {
    Assert-Equal (& $script:speedPatcher -ClientRoot $Root -Mode Status).State (
        'Original') 'SID200 v1 fixture source is stock'
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $Root "Localization\$locale\UI\XML"
        $xmlPath = Join-Path $directory 'PersonalInfoUI.xml'
        $xml = Convert-ToSid200V1FixtureXml ([IO.File]::ReadAllText(
                $xmlPath, $script:encoding)) $locale
        Assert-Equal (Get-PersonalInfoXmlState $xml) 'PatchedSid200V1' (
            "$locale synthetic SID200 v1 XML")
        Write-FixtureUtf8PreservingBom $xmlPath $xml

        $constellationPath = Join-Path $directory 'Constellation.lua'
        $constellation = Convert-ConstellationStatsLua (
            [IO.File]::ReadAllText($constellationPath, $script:encoding)) $true
        Write-FixtureUtf8PreservingBom $constellationPath $constellation
        [byte[]]$oldHelperBytes = Get-Utf8Bytes (
            Get-PersonalInfoStatsLua $locale $false) $false
        $oldHelperContract = if ($locale -eq 'en_us') {
            @(
                5168,
                'A69648BE0DC227D72631571B4614A93029603F6019FD7FF5CBB97F70197D8462')
        } else {
            @(
                5135,
                'C1B27DC9D257B5D9D8F53FD54AB02C373C8A64CACA5890144EC309F2A2727BB2')
        }
        Assert-Equal $oldHelperBytes.Length $oldHelperContract[0] (
            "$locale pinned SID200 v1 helper length")
        Assert-Equal (Get-BytesSha256 $oldHelperBytes) $oldHelperContract[1] (
            "$locale pinned SID200 v1 helper SHA-256")
        [IO.File]::WriteAllBytes((Join-Path $directory (
                    'PersonalInfoSpeedStats.lua')), $oldHelperBytes)
    }
    $status = & $script:speedPatcher -ClientRoot $Root -Mode Status
    Assert-Equal $status.State 'PatchedSid200V1' (
        'Synthetic SID200 v1 combined state')
    Assert-True $status.NpcInteractionSafe (
        'Synthetic SID200 v1 keeps the stock NPC-safe binary')
}

function Format-FixtureFixedBasisPoints([int]$Value) {
    [int]$whole = [math]::Floor($Value / 100)
    [int]$fraction = $Value - $whole * 100
    return '{0}.{1:D2}%' -f $whole, $fraction
}

function Assert-ExplicitWidenedLayout([string]$Root) {
    $path = Join-Path $Root (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $xml = [IO.File]::ReadAllText($path, $script:encoding)
    $document = (Get-RebornPersonalInfoXmlValidation $xml).Document
    $expected = [ordered]@{
        StatusBack = '19,78,334,173'
        UnionBack = '19,176,334,250'
        LevelBack = '19,253,334,327'
        BaseBack = '19,330,166,536'
        FightBack = '168,330,334,536'
        RoleNameText = '5,51,334,67'
        HP = '24,334,80,350'; HPText = '84,334,160,350'
        MP = '24,360,80,376'; MPText = '84,360,160,376'
        Attack = '24,386,80,402'; AttackText = '84,386,160,402'
        Defend = '24,412,80,428'; DefendText = '84,412,160,428'
        MagicAttack = '24,438,80,454'
        MagicAttackText = '84,438,160,454'
        MagicDefend = '24,464,80,480'
        MagicDefendText = '84,464,160,480'
        Cure = '24,491,80,507'; CureText = '84,491,160,507'
        Hit = '173,334,253,350'; HitText = '257,334,333,350'
        Dodge = '173,360,253,376'; DodgeText = '257,360,333,376'
        CritAppend = '173,386,253,402'
        CritAppendText = '257,386,333,402'
        CritDefend = '173,412,253,428'
        CritDefendText = '257,412,333,428'
        PhyDamageAppend = '173,438,253,454'
        PhyDamageAppendText = '257,438,333,454'
        MagicDamageAppend = '173,464,253,480'
        MagicDamageAppendText = '257,464,333,480'
        DamageSorb = '173,491,253,507'
        DamageSorbText = '257,491,333,507'
        spouse = '24,517,80,533'; spouseText = '84,517,160,533'
        Penetration = '173,517,253,533'
        Recommend = '257,517,333,533'
    }
    foreach ($entry in $expected.GetEnumerator()) {
        $nodes = @($document.SelectNodes('//*') | Where-Object {
                $_.Name -ceq $entry.Key
            })
        Assert-Equal $nodes.Count 1 "Explicit layout one $($entry.Key)"
        Assert-Equal $nodes[0].GetAttribute('Rectangle') $entry.Value (
            "Explicit layout $($entry.Key) rectangle")
    }
    $rootNode = @($document.SelectNodes('/UIConfig/PersonalInfo'))
    Assert-Equal $rootNode.Count 1 'Explicit layout one PersonalInfo root'
    Assert-Equal $rootNode[0].GetAttribute('Rectangle') '100,100,454,652' (
        'Explicit widened PersonalInfo bounds')
    Assert-Equal $rootNode[0].GetAttribute('BtnRect') '287,13,324,50' (
        'Explicit widened close-button alignment')
}

function Invoke-CharacterStatsLayoutTests {
    $migration = New-ClientFixture 'sid200-v1-layout-migration'
    $migrationBackups = Join-Path $script:testRoot 'backups-sid200-v1-layout'
    Normalize-Original $migration $migrationBackups
    $original = Get-CharacterStatsTrackedSnapshot $migration
    $originalExeHash = Get-FileSha256 (Join-Path $migration 'Origin.exe')
    Set-Sid200V1Fixture $migration
    & $script:speedPatcher -ClientRoot $migration -Mode Apply `
        -BackupRoot $migrationBackups | Out-Null
    Assert-Sid200Patched $migration
    Assert-ExplicitWidenedLayout $migration
    Assert-Equal (Get-FileSha256 (Join-Path $migration 'Origin.exe')) (
        $originalExeHash) 'SID200 v1 migration never changes Origin.exe'
    & $script:speedPatcher -ClientRoot $migration -Mode Revert `
        -BackupRoot $migrationBackups | Out-Null
    Assert-CharacterStatsTrackedSnapshot $migration $original (
        'SID200 v1 migration Apply/Revert is byte-exact')

    foreach ($attribute in 'Rectangle', 'Template') {
        $duplicate = New-ClientFixture (
            'layout-case-duplicate-' + $attribute.ToLowerInvariant())
        $duplicateBackups = Join-Path $script:testRoot (
            'backups-layout-duplicate-' + $attribute.ToLowerInvariant())
        Normalize-Original $duplicate $duplicateBackups
        $path = Join-Path $duplicate (
            'Localization\en_us\UI\XML\PersonalInfoUI.xml')
        $text = [IO.File]::ReadAllText($path, $script:encoding)
        $text = Update-RegexOnce $text (
            '(?m)^[ \t]*<Defend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') {
            param($line)
            $extra = if ($attribute -eq 'Rectangle') {
                ' rectangle="1,1,2,2"'
            } else { ' template="WrongOwner"' }
            $line.Replace('/>', "$extra />")
        } "case-duplicate $attribute fixture"
        Write-FixtureUtf8PreservingBom $path $text
        Assert-FixtureApplyRejected $duplicate $duplicateBackups (
            "Case-duplicate $attribute attribute fails closed")
    }

    $prefixed = New-ClientFixture 'layout-prefixed-control'
    $prefixedBackups = Join-Path $script:testRoot 'backups-layout-prefixed'
    Normalize-Original $prefixed $prefixedBackups
    $prefixedPath = Join-Path $prefixed (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $prefixedText = [IO.File]::ReadAllText($prefixedPath, $script:encoding)
    $prefixedText = $prefixedText.Replace(
        '<PersonalInfo Template=',
        '<PersonalInfo xmlns:x="urn:reborn-layout-test" Template=').Replace(
        '<Defend       Template=', '<x:Defend       Template=')
    Write-FixtureUtf8PreservingBom $prefixedPath $prefixedText
    Assert-FixtureApplyRejected $prefixed $prefixedBackups (
        'Namespace-prefixed mapped control fails closed')

    $multiline = New-ClientFixture 'layout-comment-multiline-decoy'
    $multilineBackups = Join-Path $script:testRoot 'backups-layout-multiline'
    Normalize-Original $multiline $multilineBackups
    $multilinePath = Join-Path $multiline (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $multilineText = [IO.File]::ReadAllText(
        $multilinePath, $script:encoding)
    $newLine = if ($multilineText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $defendLine = (Get-RebornXmlElementLines $multilineText 'Defend')[0].Value
    $multilineDefend = $defendLine.Replace(
        ' Template=', $newLine + '        Template=')
    $decoyAndLive = '    <!--' + $newLine + $defendLine + $newLine +
        '    -->' + $newLine + $multilineDefend
    $multilineText = Replace-RegexOnce $multilineText (
        [regex]::Escape($defendLine)) $decoyAndLive (
        'commented canonical and multiline live Defend fixture')
    Write-FixtureUtf8PreservingBom $multilinePath $multilineText
    Assert-FixtureApplyRejected $multiline $multilineBackups (
        'Commented canonical plus multiline live control fails closed')

    $commented = New-ClientFixture 'layout-commented-raw-decoy'
    $commentedBackups = Join-Path $script:testRoot 'backups-layout-commented'
    Normalize-Original $commented $commentedBackups
    $commentedPath = Join-Path $commented (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $commentedText = [IO.File]::ReadAllText(
        $commentedPath, $script:encoding)
    $commentedNewLine = if ($commentedText.Contains("`r`n")) {
        "`r`n"
    } else { "`n" }
    $liveDefend = (Get-RebornXmlElementLines $commentedText 'Defend')[0].Value
    $rawDecoy = @(
        '    <!--',
        '    <Defend Type="Text" Rectangle="1,1,2,2" />',
        '    -->',
        $liveDefend) -join $commentedNewLine
    $commentedText = Replace-RegexOnce $commentedText (
        [regex]::Escape($liveDefend)) $rawDecoy (
        'noncanonical commented Defend decoy fixture')
    Write-FixtureUtf8PreservingBom $commentedPath $commentedText
    Assert-FixtureApplyRejected $commented $commentedBackups (
        'Noncanonical commented mapped-control decoy fails closed')

    $owner = New-ClientFixture 'layout-attribute-order-owner'
    $ownerBackups = Join-Path $script:testRoot 'backups-layout-owner'
    Normalize-Original $owner $ownerBackups
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $owner (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $text = [IO.File]::ReadAllText($path, $script:encoding)
        $text = Update-RegexOnce $text (
            '(?m)^[ \t]*<Defend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') {
            param($line)
            $line.Replace('<Defend', '<Defend CustomOwner="keep"')
        } 'mapped-row attribute-order owner'
        Write-FixtureUtf8PreservingBom $path $text
    }
    $ownerOriginal = Get-CharacterStatsTrackedSnapshot $owner
    Assert-Equal (& $script:speedPatcher -ClientRoot $owner -Mode Status).State (
        'Original') 'Mapped-row unrelated attribute is accepted'
    & $script:speedPatcher -ClientRoot $owner -Mode Apply `
        -BackupRoot $ownerBackups | Out-Null
    Assert-Sid200Patched $owner
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $owner (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        Assert-True ([IO.File]::ReadAllText($path,
                $script:encoding).Contains('CustomOwner="keep"')) (
            "$locale mapped-row owner survives Apply")
    }
    & $script:speedPatcher -ClientRoot $owner -Mode Revert `
        -BackupRoot $ownerBackups | Out-Null
    Assert-CharacterStatsTrackedSnapshot $owner $ownerOriginal (
        'Mapped-row owner Apply/Revert is byte-exact')

    $rootAndRows = New-ClientFixture 'root-tail-row-owner'
    $rootAndRowsBackups = Join-Path $script:testRoot 'backups-root-tail-row'
    Normalize-Original $rootAndRows $rootAndRowsBackups
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $rootAndRows (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $text = [IO.File]::ReadAllText($path, $script:encoding)
        $lineBreak = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $text = Update-RegexOnce $text (
            '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*>[ \t]*(?=\r?\n|\z)') {
            param($line)
            $line.Replace(' Visible="0">',
                ' Visible="0" CustomTailOwner="keep">')
        } 'trailing root owner fixture'
        $spouseLine = (Get-RebornXmlElementLines $text 'spouse')[0].Value
        $otherRow = '    <OtherOwner Type="Text" ID="-1" />'
        $text = Replace-RegexOnce $text ([regex]::Escape($spouseLine)) (
            $spouseLine + $lineBreak + $otherRow) (
            'interleaved row owner fixture')
        $text = Update-RegexOnce $text (
            '(?m)^</UIConfig>[ \t]*(?=\r?\n|\z)') {
            param($line)
            '  ' + $line
        } 'indented closing UIConfig fixture'
        Write-FixtureUtf8PreservingBom $path $text
    }
    $rootAndRowsOriginal = Get-CharacterStatsTrackedSnapshot $rootAndRows
    Assert-Equal (& $script:speedPatcher -ClientRoot $rootAndRows `
        -Mode Status).State 'Original' (
        'Trailing root/row owners remain a compatible stock state')
    & $script:speedPatcher -ClientRoot $rootAndRows -Mode Apply `
        -BackupRoot $rootAndRowsBackups | Out-Null
    Assert-Sid200Patched $rootAndRows
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $rootAndRows (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $text = [IO.File]::ReadAllText($path, $script:encoding)
        foreach ($fragment in 'CustomTailOwner="keep"', '<OtherOwner ',
            '  </UIConfig>') {
            Assert-True $text.Contains($fragment) (
                "$locale compatible root/row owner $fragment survives Apply")
        }
    }
    & $script:speedPatcher -ClientRoot $rootAndRows -Mode Revert `
        -BackupRoot $rootAndRowsBackups | Out-Null
    Assert-CharacterStatsTrackedSnapshot $rootAndRows $rootAndRowsOriginal (
        'Trailing root/row owner Apply/Revert is byte-exact')

    foreach ($variant in 'wrong', 'duplicate') {
        $visible = New-ClientFixture ('root-visible-' + $variant)
        $visibleBackups = Join-Path $script:testRoot (
            'backups-root-visible-' + $variant)
        Normalize-Original $visible $visibleBackups
        $visiblePath = Join-Path $visible (
            'Localization\en_us\UI\XML\PersonalInfoUI.xml')
        $visibleText = [IO.File]::ReadAllText(
            $visiblePath, $script:encoding)
        $replacement = if ($variant -eq 'wrong') {
            ' Visible="1"'
        } else { ' Visible="0" visible="0"' }
        $visibleText = Update-RegexOnce $visibleText (
            '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*>[ \t]*(?=\r?\n|\z)') {
            param($line)
            $line.Replace(' Visible="0"', $replacement)
        } "root Visible $variant fixture"
        Write-FixtureUtf8PreservingBom $visiblePath $visibleText
        Assert-FixtureApplyRejected $visible $visibleBackups (
            "Root Visible $variant form fails closed")
    }

    $badButton = New-ClientFixture 'wide-close-button-corruption'
    $badButtonBackups = Join-Path $script:testRoot 'backups-wide-button'
    Normalize-Original $badButton $badButtonBackups
    & $script:speedPatcher -ClientRoot $badButton -Mode Apply `
        -BackupRoot $badButtonBackups | Out-Null
    $badButtonPath = Join-Path $badButton (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $badButtonText = [IO.File]::ReadAllText(
        $badButtonPath, $script:encoding).Replace(
        'BtnRect="287,13,324,50"', 'BtnRect="196,13,233,50"')
    Write-FixtureUtf8PreservingBom $badButtonPath $badButtonText
    Assert-Throws {
        & $script:speedPatcher -ClientRoot $badButton -Mode Status | Out-Null
    } 'unknown or partially applied' 'Misaligned wide close button rejection'

    $newLua = Get-PersonalInfoStatsLua 'en_us'
    $oldLua = Get-PersonalInfoStatsLua 'en_us' $false
    $fixedBlock = @(
        'local function RebornPersonalInfoFormatFixedBasisPoints(value)',
        '    local basisPoints=math.floor(value)',
        '    local whole=math.floor(basisPoints / 100)',
        '    local fraction=basisPoints - whole * 100',
        '    if fraction < 10 then',
        '        return whole..".0"..fraction.."%"',
        '    end',
        '    return whole.."."..fraction.."%"',
        'end') -join "`r`n"
    Assert-True (-not $oldLua.Contains('FormatFixedBasisPoints')) (
        'SID200 v1 helper remains byte-distinct and trimmed')
    Assert-True $newLua.Contains($fixedBlock) (
        'Fixed Penetration formatter has the exact two-decimal body')
    foreach ($sample in @(
        @(0, '0.00%'), @(10, '0.10%'), @(333, '3.33%'),
        @(350, '3.50%'), @(8000, '80.00%'))) {
        Assert-Equal (Format-FixtureFixedBasisPoints $sample[0]) $sample[1] (
            "Fixed Penetration format $($sample[0])bp")
    }
}
