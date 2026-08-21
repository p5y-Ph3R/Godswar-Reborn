Set-StrictMode -Version Latest

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

function Get-CharacterStatsUiText([string]$Locale) {
    if ($Locale -eq 'zh_cn') {
        return [pscustomobject]@{
            Speed = -join @([char]0x901F, [char]0x5EA6)
            Penetration = -join @([char]0x7A7F, [char]0x900F)
            SpeedTooltip = -join @(
                [char]0x901F, [char]0x5EA6, [char]0xFF1A,
                [char]0x5F53, [char]0x524D, [char]0x6709, [char]0x6548,
                [char]0x79FB, [char]0x52A8, [char]0x901F, [char]0x5EA6,
                [char]0x3002)
            PhysicalTooltip = -join @(
                [char]0x5BF9, [char]0x76F8, [char]0x5E94, [char]0x7269,
                [char]0x7406, [char]0x4F24, [char]0x5BB3, [char]0xFF0C,
                [char]0x5FFD, [char]0x7565, [char]0x76EE, [char]0x6807,
                [char]0x6B64, [char]0x767E, [char]0x5206, [char]0x6BD4,
                [char]0x7684, [char]0x7269, [char]0x7406, [char]0x9632,
                [char]0x5FA1, [char]0x3002, "`n",
                [char]0x6709, [char]0x6548, [char]0x7A7F, [char]0x900F,
                [char]0x4E0A, [char]0x9650, [char]0xFF1A, '80%',
                [char]0x3002)
            MagicalTooltip = -join @(
                [char]0x5BF9, [char]0x76F8, [char]0x5E94, [char]0x9B54,
                [char]0x6CD5, [char]0x4F24, [char]0x5BB3, [char]0xFF0C,
                [char]0x5FFD, [char]0x7565, [char]0x76EE, [char]0x6807,
                [char]0x6B64, [char]0x767E, [char]0x5206, [char]0x6BD4,
                [char]0x7684, [char]0x9B54, [char]0x6CD5, [char]0x9632,
                [char]0x5FA1, [char]0x3002, "`n",
                [char]0x6709, [char]0x6548, [char]0x7A7F, [char]0x900F,
                [char]0x4E0A, [char]0x9650, [char]0xFF1A, '80%',
                [char]0x3002)
            UnknownTooltip = -join @(
                [char]0x65E0, [char]0x6CD5, [char]0x786E, [char]0x5B9A,
                [char]0x5F53, [char]0x524D, [char]0x804C, [char]0x4E1A,
                [char]0x7684, [char]0x7A7F, [char]0x900F, [char]0x7C7B,
                [char]0x578B, [char]0x3002)
        }
    }
    return [pscustomobject]@{
        Speed = 'Speed'
        Penetration = 'Pen.'
        SpeedTooltip = 'Speed: current effective movement speed.'
        PhysicalTooltip = "Ignores this percentage of the target's Physical Defense when dealing physical damage.`nEffective cap: 80%."
        MagicalTooltip = "Ignores this percentage of the target's Magical Defense when dealing magical damage.`nEffective cap: 80%."
        UnknownTooltip = 'Penetration is unavailable for the current class.'
    }
}

function Assert-PersonalInfoLocale(
    [string]$Text,
    [string]$Locale,
    [string]$State
) {
    if ($State -eq 'Original') { return }
    $spouse = Get-RebornXmlElementLines $Text 'spouse'
    $script = [regex]::Matches($Text,
        '(?m)^[ \t]*<Script\b[^\r\n]*(?i:PersonalInfoSpeedStats\.lua)[^\r\n]*/>[ \t]*(?=\r?\n|\z)')
    if ($State -in 'PatchedSid200', 'PatchedSid200V1',
        'PatchedSid200FrameV1') {
        $labels = Get-CharacterStatsUiText $Locale
        $penetration = Get-RebornXmlElementLines $Text 'Penetration'
        $speedRectangle = if ($State -ne 'PatchedSid200V1') {
            '24,517,80,533'
        } else { '24,517,78,533' }
        $penetrationRectangle = if ($State -ne 'PatchedSid200V1') {
            '173,517,253,533'
        } else { '137,517,200,533' }
        $expectedSpeed = "    <spouse Template=`"T_Money`" Rectangle=`"$speedRectangle`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$($labels.Speed)`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoSpeedHovered()`" OnLeft=`"RebornPersonalInfoStatsLeft()`" />"
        $expectedPenetration = "    <Penetration Template=`"T_Money`" Rectangle=`"$penetrationRectangle`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$($labels.Penetration)`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoPenetrationHovered()`" OnLeft=`"RebornPersonalInfoStatsLeft()`" />"
        $expectedScript = "  <Script File=`"./Localization/$Locale/UI/XML/PersonalInfoSpeedStats.lua`" Help=`"`"/>"
        if (-not (Test-RebornSingleExactLine $spouse $expectedSpeed) -or
            -not (Test-RebornSingleExactLine $penetration (
                    $expectedPenetration)) -or
            -not (Test-RebornSingleExactLine $script $expectedScript)) {
            throw "PersonalInfoUI.xml has the wrong SID200 labels for $Locale."
        }
        return
    }
    $riding = Get-RebornXmlElementLines $Text 'RidingSpeed'
    $movement = if ($State -eq 'PatchedV1') {
        Get-SpeedFullLabel $Locale $true
    } else { Get-SpeedCompactLabel $Locale $true }
    $ridingLabel = if ($State -eq 'PatchedV1') {
        Get-SpeedFullLabel $Locale $false
    } else { Get-SpeedCompactLabel $Locale $false }
    $expectedMovement = if ($State -eq 'PatchedV3') {
        "    <spouse Template=`"T_Money`" Rectangle=`"24,517,78,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$movement`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />"
    } elseif ($State -eq 'PatchedV2') {
        "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$movement`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />"
    } else {
        "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$movement`" Visible=`"1`" />"
    }
    $ridingRectangle = if ($State -eq 'PatchedV3') {
        '137,517,200,533'
    } else { '29,548,152,564' }
    $expectedRiding = if ($State -eq 'PatchedV1') {
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"$ridingRectangle`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$ridingLabel`" />"
    } else {
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"$ridingRectangle`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$ridingLabel`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoRidingSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />"
    }
    $scriptValid = if ($State -eq 'PatchedV1') {
        $script.Count -eq 0
    } else {
        Test-RebornSingleExactLine $script (
            "  <Script File=`"./Localization/$Locale/UI/XML/PersonalInfoSpeedStats.lua`" Help=`"`"/>")
    }
    if (-not (Test-RebornSingleExactLine $spouse $expectedMovement) -or
        -not (Test-RebornSingleExactLine $riding $expectedRiding) -or
        -not $scriptValid) {
        throw "PersonalInfoUI.xml has the wrong legacy labels for $Locale."
    }
}
