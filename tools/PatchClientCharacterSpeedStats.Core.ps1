Set-StrictMode -Version Latest

function Replace-RegexOnce(
    [string]$Text,
    [string]$Pattern,
    [string]$Replacement,
    [string]$Label
) {
    $regex = [regex]::new($Pattern)
    $matches = $regex.Matches($Text)
    if ($matches.Count -ne 1) {
        throw "$Label expected exactly one match, found $($matches.Count)."
    }
    $match = $matches[0]
    return $Text.Remove($match.Index, $match.Length).Insert(
        $match.Index, $Replacement)
}

function Update-RegexOnce(
    [string]$Text,
    [string]$Pattern,
    [scriptblock]$Transform,
    [string]$Label
) {
    $regex = [regex]::new($Pattern)
    $matches = $regex.Matches($Text)
    if ($matches.Count -ne 1) {
        throw "$Label expected exactly one match, found $($matches.Count)."
    }
    $match = $matches[0]
    $replacement = & $Transform $match.Value
    return $Text.Remove($match.Index, $match.Length).Insert(
        $match.Index, $replacement)
}

function Remove-RegexAtMostOnce(
    [string]$Text,
    [string]$Pattern,
    [string]$Label
) {
    $regex = [regex]::new($Pattern)
    $matches = $regex.Matches($Text)
    if ($matches.Count -gt 1) {
        throw "$Label expected at most one match, found $($matches.Count)."
    }
    if ($matches.Count -eq 0) { return $Text }
    $match = $matches[0]
    return $Text.Remove($match.Index, $match.Length)
}

function Get-OriginalPersonalInfoRows([string]$NewLine) {
    $spouseLabel = -join @([char]0x914D, [char]0x5076)
    return @(
        "    <spouse       Template=`"T_Money`"  Rectangle=`"24,522,78,542`" Font=`"MainMap`" TextFormat=`"5`"  FontColor=`"BASE_HEADCOLOR`"  Text=`"$spouseLabel`" Visible=`"0`" />",
        '    <spouseText   Template="T_Money"   Rectangle="85,522,180,542"   TextFormat="5"  FontColor="ORDINARY_INFOCOLOR"  Text="" Visible="0"/>'
    ) -join $NewLine
}

function Convert-LegacyPersonalInfoToOriginal(
    [string]$Text,
    [string]$NewLine
) {
    $Text = Update-RebornPersonalInfoRectangle $Text (
        'Rectangle="100,100,363,(?:626|652|692)"') (
        'Rectangle="100,100,363,626"') 'legacy PersonalInfo bounds'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,510" />') 'BaseBack restoration'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,510" />') 'FightBack restoration'
    $Text = Remove-RegexAtMostOnce $Text (
        '(?m)^[ \t]*<SpeedBack\b[^\r\n]*/>[ \t]*\r?\n?') 'legacy SpeedBack'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <Recommend      Template="T_Button2_S" ID="281026" Rectangle="186,123,236,145" Font="MainMap" Format="4"  FontColor="BASE_HEADCOLOR"  SText="PI_X0_30" Visible="0" />') 'Recommend restoration'
    $originalRows = (Get-OriginalPersonalInfoRows $NewLine).Split(
        @($NewLine), [StringSplitOptions]::None)
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        $originalRows[0]) 'legacy spouse row'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouseText\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        $originalRows[1]) 'legacy spouse value row'
    foreach ($name in 'MovementSpeedPercent', 'RidingSpeed',
        'RidingSpeedPercent') {
        $Text = Remove-RegexAtMostOnce $Text (
            "(?m)^[ \t]*<$name\b[^\r\n]*/>[ \t]*(?:\r?\n|\z)") (
            "legacy $name row")
    }
    return Remove-RegexAtMostOnce $Text (
        '(?m)^[ \t]*<Script\s+File="\./Localization/(?:en_us|zh_cn)/UI/XML/PersonalInfoSpeedStats\.lua"\s+Help=""\s*/>[ \t]*\r?\n?') 'legacy PersonalInfo script'
}

function Convert-OriginalPersonalInfoToSid200(
    [string]$Text,
    [string]$Locale,
    [string]$NewLine
) {
    $labels = Get-CharacterStatsUiText $Locale
    $Text = Update-RebornPersonalInfoRectangle $Text (
        'Rectangle="100,100,363,626"') (
        'Rectangle="100,100,454,652"') 'PersonalInfo SID200 bounds'
    $Text = Update-RebornPersonalInfoRectangle $Text (
        'BtnRect="196,13,233,50"') (
        'BtnRect="287,13,324,50"') 'PersonalInfo close button'
    $Text = Convert-RebornPersonalInfoRectangles $Text $true
    $Text = Update-RegexOnce $Text (
        '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*>[ \t]*(?=\r?\n|\z)') {
        param($line)
        $line.Replace(' Visible="0"', (
            ' Visible="0" OnLoad="RebornPersonalInfoStatsLoad()"' +
            ' OnClose="RebornPersonalInfoStatsClose()"'))
    } 'PersonalInfo callbacks'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,166,536" />') 'BaseBack extension'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="168,330,334,536" />') 'FightBack extension'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <Recommend Template="T_Money" ID="281026" Rectangle="257,517,333,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1" />') 'penetration value control'
    $spouse = "    <spouse Template=`"T_Money`" Rectangle=`"24,517,80,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$($labels.Speed)`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoSpeedHovered()`" OnLeft=`"RebornPersonalInfoStatsLeft()`" />"
    $spouseText = '    <spouseText Template="T_Money" Rectangle="84,517,160,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1"/>'
    $penetration = "    <Penetration Template=`"T_Money`" Rectangle=`"173,517,253,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$($labels.Penetration)`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoPenetrationHovered()`" OnLeft=`"RebornPersonalInfoStatsLeft()`" />"
    $updater = '    <RebornPersonalInfoStatsUpdater Type="Text" ID="-1" Rectangle="1,1,2,2" Text="" Visible="1" OnUpdate="RebornPersonalInfoStatsUpdate()" />'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        $spouse) 'SID200 spouse row'
    $Text = Update-RegexOnce $Text (
        '(?m)^[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n') {
        param($lineAndBreak)
        $lineBreak = if ($lineAndBreak.EndsWith("`r`n")) {
            "`r`n"
        } else { "`n" }
        $spouseText + $lineBreak + $penetration + $lineBreak +
            $updater + $lineBreak
    } 'SID200 value rows'
    $script = "  <Script File=`"./Localization/$Locale/UI/XML/PersonalInfoSpeedStats.lua`" Help=`"`"/>"
    return Update-RegexOnce $Text (
        '(?m)^[ \t]*</PersonalInfo>[ \t]*\r?\n') {
        param($lineAndBreak)
        $lineBreak = if ($lineAndBreak.EndsWith("`r`n")) {
            "`r`n"
        } else { "`n" }
        $line = $lineAndBreak.Substring(
            0, $lineAndBreak.Length - $lineBreak.Length)
        $line + $lineBreak + $script + $lineBreak
    } 'PersonalInfo script anchor'
}

function Convert-Sid200PersonalInfoToOriginal(
    [string]$Text,
    [string]$NewLine,
    [bool]$Wide
) {
    if ($Wide) {
        $Text = Convert-RebornPersonalInfoRectangles $Text $false
    }
    $sourceBounds = if ($Wide) {
        'Rectangle="100,100,(?:440|454),652"'
    } else { 'Rectangle="100,100,363,652"' }
    $Text = Update-RebornPersonalInfoRectangle $Text (
        $sourceBounds) (
        'Rectangle="100,100,363,626"') 'PersonalInfo original bounds'
    if ($Wide) {
        $Text = Update-RebornPersonalInfoRectangle $Text (
            'BtnRect="(?:273,13,310,50|287,13,324,50)"') (
            'BtnRect="196,13,233,50"') 'PersonalInfo close button'
    }
    $Text = Update-RegexOnce $Text (
        '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*>[ \t]*(?=\r?\n|\z)') {
        param($line)
        $line.Replace(
            ' OnLoad="RebornPersonalInfoStatsLoad()"', '').Replace(
            ' OnClose="RebornPersonalInfoStatsClose()"', '')
    } 'PersonalInfo callback restoration'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,510" />') 'BaseBack restoration'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,510" />') 'FightBack restoration'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        '    <Recommend      Template="T_Button2_S" ID="281026" Rectangle="186,123,236,145" Font="MainMap" Format="4"  FontColor="BASE_HEADCOLOR"  SText="PI_X0_30" Visible="0" />') 'Recommend restoration'
    $originalRows = (Get-OriginalPersonalInfoRows $NewLine).Split(
        @($NewLine), [StringSplitOptions]::None)
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        $originalRows[0]) 'SID200 spouse restoration'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouseText\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
        $originalRows[1]) 'SID200 spouse value restoration'
    foreach ($name in 'Penetration', 'RebornPersonalInfoStatsUpdater') {
        $Text = Remove-RegexAtMostOnce $Text (
            "(?m)^[ \t]*<$name\b[^\r\n]*/>[ \t]*(?:\r?\n|\z)") (
            "SID200 $name row")
    }
    return Remove-RegexAtMostOnce $Text (
        '(?m)^[ \t]*<Script\s+File="\./Localization/(?:en_us|zh_cn)/UI/XML/PersonalInfoSpeedStats\.lua"\s+Help=""\s*/>[ \t]*\r?\n?') 'PersonalInfo script'
}

function Convert-PersonalInfoXml(
    [string]$Text,
    [string]$Locale,
    [bool]$ToPatched
) {
    $newLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $state = Get-PersonalInfoXmlState $Text
    if ($ToPatched) {
        if ($state -eq 'PatchedSid200') { return $Text }
        if ($state -in 'PatchedSid200V1', 'PatchedSid200FrameV1') {
            $Text = Convert-Sid200PersonalInfoToOriginal $Text $newLine (
                $state -eq 'PatchedSid200FrameV1')
        }
        if ($state -in 'PatchedV1', 'PatchedV2', 'PatchedV3') {
            $Text = Convert-LegacyPersonalInfoToOriginal $Text $newLine
        }
        return Convert-OriginalPersonalInfoToSid200 $Text $Locale $newLine
    }
    if ($state -eq 'Original') { return $Text }
    if ($state -in 'PatchedSid200', 'PatchedSid200V1',
        'PatchedSid200FrameV1') {
        return Convert-Sid200PersonalInfoToOriginal $Text $newLine (
            $state -ne 'PatchedSid200V1')
    }
    return Convert-LegacyPersonalInfoToOriginal $Text $newLine
}
