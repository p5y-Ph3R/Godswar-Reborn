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

function Get-PersonalInfoXmlState([string]$Text) {
    $original =
        $Text.Contains('Rectangle="100,100,363,626"') -and
        -not $Text.Contains('<SpeedBack ') -and
        $Text -match '<Recommend\s+Template="T_Button2_S"[^>]*Visible="0"' -and
        $Text -match '<spouse\s+[^>]*Visible="0"' -and
        $Text -match '<spouseText\s+[^>]*Visible="0"'
    $v1 =
        $Text.Contains('Rectangle="100,100,363,626"') -and
        $Text.Contains('<SpeedBack Template="T_BgWindow" ID="-1" Rectangle="19,513,243,576" />') -and
        $Text -match '<Recommend\s+Template="T_Money"[^>]*Visible="1"' -and
        $Text -match '<spouse\s+[^>]*Visible="1"' -and
        $Text -match '<spouse\s+[^>]*Text="[^"]+"' -and
        $Text -match '<spouseText\s+[^>]*Visible="1"' -and
        $Text.Contains('<MovementSpeedPercent ') -and
        $Text -match '<RidingSpeed\s+[^>]*Text="[^"]+"' -and
        $Text.Contains('<RidingSpeedPercent ')
    $v2 =
        $Text.Contains('Rectangle="100,100,363,692"') -and
        $Text.Contains('<SpeedBack Template="T_BgWindow" ID="-1" Rectangle="19,513,243,576" />') -and
        $Text -match '<Recommend\s+Template="T_Money"[^>]*Rectangle="183,548,221,564"[^>]*Visible="1"' -and
        $Text -match '<spouse\s+[^>]*Rectangle="29,522,152,538"[^>]*Text="[^"]+"[^>]*Visible="1"[^>]*CanHovered="1"[^>]*OnHovered="RebornPersonalInfoMovementSpeedHovered\(\)"' -and
        $Text -match '<spouseText\s+[^>]*Rectangle="183,522,221,538"[^>]*Visible="1"' -and
        $Text.Contains('<MovementSpeedPercent ') -and
        $Text -match '<RidingSpeed\s+[^>]*Rectangle="29,548,152,564"[^>]*Text="[^"]+"[^>]*CanHovered="1"[^>]*OnHovered="RebornPersonalInfoRidingSpeedHovered\(\)"' -and
        $Text.Contains('<RidingSpeedPercent ') -and
        $Text -match '<Script\s+File="\./Localization/(?:en_us|zh_cn)/UI/XML/PersonalInfoSpeedStats\.lua"\s+Help=""\s*/>'
    $v3 =
        $Text.Contains('Rectangle="100,100,363,652"') -and
        -not $Text.Contains('<SpeedBack ') -and
        $Text.Contains('<BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,536" />') -and
        $Text.Contains('<FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,536" />') -and
        $Text -match '<Recommend\s+Template="T_Money"[^>]*Rectangle="210,517,234,533"[^>]*Visible="1"' -and
        $Text -match '<spouse\s+[^>]*Rectangle="24,517,78,533"[^>]*Text="[^"]+"[^>]*Visible="1"[^>]*CanHovered="1"[^>]*OnHovered="RebornPersonalInfoMovementSpeedHovered\(\)"' -and
        $Text -match '<spouseText\s+[^>]*Rectangle="85,517,111,533"[^>]*Visible="1"' -and
        $Text -match '<MovementSpeedPercent\s+[^>]*Rectangle="113,517,125,533"' -and
        $Text -match '<RidingSpeed\s+[^>]*Rectangle="137,517,200,533"[^>]*Text="[^"]+"[^>]*CanHovered="1"[^>]*OnHovered="RebornPersonalInfoRidingSpeedHovered\(\)"' -and
        $Text -match '<RidingSpeedPercent\s+[^>]*Rectangle="236,517,246,533"' -and
        $Text -match '<Script\s+File="\./Localization/(?:en_us|zh_cn)/UI/XML/PersonalInfoSpeedStats\.lua"\s+Help=""\s*/>'
    if ($original -and -not $v1 -and -not $v2 -and -not $v3) {
        return 'Original'
    }
    if ($v1 -and -not $original -and -not $v2 -and -not $v3) {
        return 'PatchedV1'
    }
    if ($v2 -and -not $original -and -not $v1 -and -not $v3) {
        return 'PatchedV2'
    }
    if ($v3 -and -not $original -and -not $v1 -and -not $v2) {
        return 'PatchedV3'
    }
    throw 'PersonalInfoUI.xml has an unknown or partially applied speed-stat layout.'
}

function Get-SpeedFullLabel([string]$Locale, [bool]$Movement) {
    if ($Locale -eq 'zh_cn') {
        if ($Movement) {
            return -join @(
                [char]0x79FB, [char]0x52A8, [char]0x901F, [char]0x5EA6)
        }
        return -join @(
            [char]0x9A91, [char]0x4E58, [char]0x901F, [char]0x5EA6)
    }
    if ($Movement) { return 'Movement Speed' }
    return 'Riding Speed'
}

function Get-SpeedCompactLabel([string]$Locale, [bool]$Movement) {
    if ($Locale -eq 'zh_cn') {
        if ($Movement) { return -join @([char]0x79FB, [char]0x901F) }
        return -join @([char]0x9A91, [char]0x901F)
    }
    if ($Movement) { return 'M.Speed' }
    return 'R.Speed'
}

function Convert-OriginalToSpeedV1(
    [string]$Text,
    [string]$MovementLabel,
    [string]$RidingLabel,
    [string]$NewLine
) {
    $fight = '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,510" />'
    $speedBack = '    <SpeedBack Template="T_BgWindow" ID="-1" Rectangle="19,513,243,576" />'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*\r?$') (
        $fight + $NewLine + $speedBack) 'FightBack anchor'
    $recommend = '    <Recommend Template="T_Money" ID="281026" Rectangle="183,548,221,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*\r?$') (
        $recommend) 'Recommend value control'
    $rows = @(
        "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$MovementLabel`" Visible=`"1`" />",
        '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
        '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"29,548,152,564`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$RidingLabel`" />",
        '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
    ) -join $NewLine
    return Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?$') (
        $rows) 'spouse speed-row controls'
}

function Convert-SpeedV1ToV2(
    [string]$Text,
    [string]$Locale,
    [string]$MovementLabel,
    [string]$RidingLabel,
    [string]$NewLine
) {
    $Text = Replace-RegexOnce $Text (
        'Rectangle="100,100,363,626"') (
        'Rectangle="100,100,363,692"') 'PersonalInfo extended bounds'
    $rows = @(
        "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$MovementLabel`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
        '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"29,548,152,564`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$RidingLabel`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoRidingSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
    ) -join $NewLine
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<MovementSpeedPercent\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeed\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeedPercent\b[^\r\n]*/>[ \t]*\r?$') (
        $rows) 'spouse speed-row controls'
    $script = "  <Script File=`"./Localization/$Locale/UI/XML/PersonalInfoSpeedStats.lua`" Help=`"`"/>"
    return Replace-RegexOnce $Text '(?m)^</UIConfig>[ \t]*\r?$' (
        $script + $NewLine + '</UIConfig>') 'PersonalInfo script anchor'
}

function Convert-SpeedV2ToV3(
    [string]$Text,
    [string]$MovementLabel,
    [string]$RidingLabel,
    [string]$NewLine
) {
    $Text = Replace-RegexOnce $Text (
        'Rectangle="100,100,363,692"') (
        'Rectangle="100,100,363,652"') 'PersonalInfo V3 bounds'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<SpeedBack\b[^\r\n]*/>[ \t]*\r?\n?') '' (
        'SpeedBack removal')
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*\r?$') (
        '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,536" />') 'BaseBack extension'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*\r?$') (
        '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,536" />') 'FightBack extension'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*\r?$') (
        '    <Recommend Template="T_Money" ID="281026" Rectangle="210,517,234,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />') 'Riding speed value'
    $rows = @(
        "    <spouse Template=`"T_Money`" Rectangle=`"24,517,78,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$MovementLabel`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <spouseText Template="T_Money" Rectangle="85,517,111,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
        '    <MovementSpeedPercent Template="T_Money" Rectangle="113,517,125,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"137,517,200,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$RidingLabel`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoRidingSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <RidingSpeedPercent Template="T_Money" Rectangle="236,517,246,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
    ) -join $NewLine
    return Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<MovementSpeedPercent\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeed\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeedPercent\b[^\r\n]*/>[ \t]*\r?$') (
        $rows) 'V3 speed-row controls'
}

function Convert-SpeedV3ToV2(
    [string]$Text,
    [string]$MovementLabel,
    [string]$RidingLabel,
    [string]$NewLine
) {
    $Text = Replace-RegexOnce $Text (
        'Rectangle="100,100,363,652"') (
        'Rectangle="100,100,363,692"') 'PersonalInfo V2 bounds'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*\r?$') (
        '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,510" />') 'BaseBack restoration'
    $fight = '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,510" />'
    $speed = '    <SpeedBack Template="T_BgWindow" ID="-1" Rectangle="19,513,243,576" />'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*\r?$') (
        $fight + $NewLine + $speed) 'V2 background restoration'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*\r?$') (
        '    <Recommend Template="T_Money" ID="281026" Rectangle="183,548,221,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />') 'V2 riding speed value'
    $rows = @(
        "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$MovementLabel`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
        '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"29,548,152,564`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$RidingLabel`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoRidingSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
    ) -join $NewLine
    return Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<MovementSpeedPercent\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeed\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeedPercent\b[^\r\n]*/>[ \t]*\r?$') (
        $rows) 'V2 speed-row controls'
}

function Convert-SpeedV2ToV1(
    [string]$Text,
    [string]$MovementLabel,
    [string]$RidingLabel,
    [string]$NewLine
) {
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Script\s+File="\./Localization/(?:en_us|zh_cn)/UI/XML/PersonalInfoSpeedStats\.lua"\s+Help=""\s*/>[ \t]*\r?\n?') '' (
        'PersonalInfo speed script')
    $Text = Replace-RegexOnce $Text (
        'Rectangle="100,100,363,692"') (
        'Rectangle="100,100,363,626"') 'PersonalInfo original bounds'
    $rows = @(
        "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$MovementLabel`" Visible=`"1`" />",
        '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
        '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"29,548,152,564`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$RidingLabel`" />",
        '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
    ) -join $NewLine
    return Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<MovementSpeedPercent\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeed\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeedPercent\b[^\r\n]*/>[ \t]*\r?$') (
        $rows) 'speed-row controls'
}

function Convert-SpeedV1ToOriginal([string]$Text, [string]$NewLine) {
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<SpeedBack\b[^\r\n]*/>[ \t]*\r?\n?') '' (
        'SpeedBack control')
    $originalRecommend = '    <Recommend      Template="T_Button2_S" ID="281026" Rectangle="186,123,236,145" Font="MainMap" Format="4"  FontColor="BASE_HEADCOLOR"  SText="PI_X0_30" Visible="0" />'
    $Text = Replace-RegexOnce $Text (
        '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*\r?$') (
        $originalRecommend) 'Recommend value control'
    $spouseLabel = -join @([char]0x914D, [char]0x5076)
    $originalRows = @(
        "    <spouse       Template=`"T_Money`"  Rectangle=`"24,522,78,542`" Font=`"MainMap`" TextFormat=`"5`"  FontColor=`"BASE_HEADCOLOR`"  Text=`"$spouseLabel`" Visible=`"0`" />",
        '    <spouseText   Template="T_Money"   Rectangle="85,522,180,542"   TextFormat="5"  FontColor="ORDINARY_INFOCOLOR"  Text="" Visible="0"/>'
    ) -join $NewLine
    return Replace-RegexOnce $Text (
        '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<MovementSpeedPercent\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeed\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<RidingSpeedPercent\b[^\r\n]*/>[ \t]*\r?$') (
        $originalRows) 'speed-row controls'
}

function Convert-PersonalInfoXml(
    [string]$Text,
    [string]$Locale,
    [bool]$ToPatched
) {
    $newLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $movement = Get-SpeedFullLabel $Locale $true
    $riding = Get-SpeedFullLabel $Locale $false
    $movementCompact = Get-SpeedCompactLabel $Locale $true
    $ridingCompact = Get-SpeedCompactLabel $Locale $false
    $state = Get-PersonalInfoXmlState $Text
    if ($ToPatched) {
        if ($state -eq 'PatchedV3') { return $Text }
        if ($state -eq 'Original') {
            $Text = Convert-OriginalToSpeedV1 $Text $movement $riding $newLine
            $state = 'PatchedV1'
        }
        if ($state -eq 'PatchedV1') {
            $Text = Convert-SpeedV1ToV2 $Text $Locale $movementCompact (
                $ridingCompact) $newLine
        }
        return Convert-SpeedV2ToV3 $Text $movementCompact (
            $ridingCompact) $newLine
    }
    if ($state -eq 'Original') { return $Text }
    if ($state -eq 'PatchedV3') {
        $Text = Convert-SpeedV3ToV2 $Text $movementCompact (
            $ridingCompact) $newLine
        $state = 'PatchedV2'
    }
    if ($state -eq 'PatchedV2') {
        $Text = Convert-SpeedV2ToV1 $Text $movement $riding $newLine
    }
    return Convert-SpeedV1ToOriginal $Text $newLine
}

function Get-PersonalInfoSpeedLua([string]$Locale) {
    $movement = Get-SpeedFullLabel $Locale $true
    $riding = Get-SpeedFullLabel $Locale $false
    return (@(
        'local uiapi=UIAPI',
        '',
        'function RebornPersonalInfoMovementSpeedHovered()',
        "    local s = `"$movement`";",
        '    uiapi:Helper(false,s,this:Instance());',
        'end',
        '',
        'function RebornPersonalInfoRidingSpeedHovered()',
        "    local s = `"$riding`";",
        '    uiapi:Helper(false,s,this:Instance());',
        'end',
        '',
        'function RebornPersonalInfoSpeedLeft()',
        '    uiapi:Helper();',
        'end',
        '') -join "`r`n")
}
