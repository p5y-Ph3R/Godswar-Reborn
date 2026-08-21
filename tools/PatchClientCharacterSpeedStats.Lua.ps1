Set-StrictMode -Version Latest

function Convert-ToRebornLuaString([string]$Value) {
    return $Value.Replace('\', '\\').Replace('"', '\"').Replace(
        "`r`n", '\n').Replace("`n", '\n').Replace("`r", '\n')
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

function Get-PersonalInfoStatsLua(
    [string]$Locale,
    [bool]$FixedPenetration = $true
) {
    $labels = Get-CharacterStatsUiText $Locale
    $speedTooltip = Convert-ToRebornLuaString $labels.SpeedTooltip
    $physicalTooltip = Convert-ToRebornLuaString $labels.PhysicalTooltip
    $magicalTooltip = Convert-ToRebornLuaString $labels.MagicalTooltip
    $unknownTooltip = Convert-ToRebornLuaString $labels.UnknownTooltip
    $lua = (@(
        'local uiapi=UIAPI',
        'local REQUEST_INTERVAL_TICKS=60',
        "local SPEED_TOOLTIP=`"$speedTooltip`"",
        "local PHYSICAL_TOOLTIP=`"$physicalTooltip`"",
        "local MAGICAL_TOOLTIP=`"$magicalTooltip`"",
        "local UNKNOWN_TOOLTIP=`"$unknownTooltip`"",
        'local requestTicks=REQUEST_INTERVAL_TICKS',
        'local unacknowledgedAttempts=0',
        'local shown=0',
        'local window=nil',
        'local speedValue=nil',
        'local penetrationValue=nil',
        '',
        'local function RebornPersonalInfoGetControls()',
        '    if window == nil then',
        '        window=uiapi:GetElement("PersonalInfo")',
        '        if window ~= nil then',
        '            speedValue=window:GetChild("spouseText")',
        '            penetrationValue=window:GetChild("Recommend")',
        '        end',
        '    end',
        '    return window ~= nil and speedValue ~= nil and penetrationValue ~= nil',
        'end',
        '',
        'local function RebornPersonalInfoGetClass()',
        '    local gamedata=PlayerAPI:GetGameData()',
        '    if gamedata == nil then',
        '        return nil',
        '    end',
        '    local class=gamedata:GetClass()',
        '    if class == 0 or class == 1 or class == 2 or class == 3 then',
        '        return class',
        '    end',
        '    return nil',
        'end',
        '',
        'local function RebornPersonalInfoFormatBasisPoints(value)',
        '    local basisPoints=math.floor(value)',
        '    local whole=math.floor(basisPoints / 100)',
        '    local fraction=basisPoints - whole * 100',
        '    if fraction == 0 then',
        '        return whole.."%"',
        '    elseif fraction < 10 then',
        '        return whole..".0"..fraction.."%"',
        '    elseif fraction - math.floor(fraction / 10) * 10 == 0 then',
        '        return whole.."."..math.floor(fraction / 10).."%"',
        '    end',
        '    return whole.."."..fraction.."%"',
        'end',
        '',
        'local function RebornPersonalInfoClearStats()',
        '    if RebornPersonalInfoGetControls() then',
        '        speedValue:SetText("--")',
        '        penetrationValue:SetText("--")',
        '    end',
        'end',
        '',
        'function RebornPersonalInfoRenderStats()',
        '    if not RebornPersonalInfoGetControls() then',
        '        return',
        '    end',
        '    if RebornPersonalInfoStatsAck ~= 1 then',
        '        RebornPersonalInfoClearStats()',
        '        return',
        '    end',
        '    if RebornPersonalInfoSpeedBasisPoints == nil then',
        '        speedValue:SetText("--")',
        '    else',
        '        speedValue:SetText(RebornPersonalInfoFormatBasisPoints(',
        '            RebornPersonalInfoSpeedBasisPoints))',
        '    end',
        '    local class=RebornPersonalInfoGetClass()',
        '    if class == 0 or class == 1 then',
        '        if RebornPersonalInfoPhysicalPenetrationBasisPoints == nil then',
        '            penetrationValue:SetText("--")',
        '        else',
        '            penetrationValue:SetText(RebornPersonalInfoFormatBasisPoints(',
        '                RebornPersonalInfoPhysicalPenetrationBasisPoints))',
        '        end',
        '    elseif class == 2 or class == 3 then',
        '        if RebornPersonalInfoMagicalPenetrationBasisPoints == nil then',
        '            penetrationValue:SetText("--")',
        '        else',
        '            penetrationValue:SetText(RebornPersonalInfoFormatBasisPoints(',
        '                RebornPersonalInfoMagicalPenetrationBasisPoints))',
        '        end',
        '    else',
        '        penetrationValue:SetText("--")',
        '    end',
        'end',
        '',
        'function RebornPersonalInfoStatsSessionReset()',
        '    RebornPersonalInfoStatsAck=0',
        '    RebornPersonalInfoSpeedBasisPoints=nil',
        '    RebornPersonalInfoPhysicalPenetrationBasisPoints=nil',
        '    RebornPersonalInfoMagicalPenetrationBasisPoints=nil',
        '    requestTicks=REQUEST_INTERVAL_TICKS',
        '    unacknowledgedAttempts=0',
        '    shown=0',
        '    RebornPersonalInfoClearStats()',
        'end',
        '',
        'function RebornPersonalInfoStatsLoad()',
        '    this:RegisterEvent(EUSER_EVENT_FIRSTENTERGAME,',
        '        "RebornPersonalInfoStatsSessionReset()")',
        '    RebornPersonalInfoStatsSessionReset()',
        'end',
        '',
        'function RebornPersonalInfoStatsClose()',
        '    RebornPersonalInfoStatsSessionReset()',
        'end',
        '',
        'function RebornPersonalInfoStatsUpdate()',
        '    if not RebornPersonalInfoGetControls() or not window:IsVisible() then',
        '        shown=0',
        '        return',
        '    end',
        '    if shown == 0 then',
        '        RebornPersonalInfoStatsAck=0',
        '        requestTicks=REQUEST_INTERVAL_TICKS',
        '        unacknowledgedAttempts=0',
        '        shown=1',
        '        RebornPersonalInfoClearStats()',
        '    end',
        '    if requestTicks >= REQUEST_INTERVAL_TICKS then',
        '        if RebornPersonalInfoStatsAck == 1 then',
        '            GameAPI:ConsEventRequest(200,200,1,0)',
        '        elseif unacknowledgedAttempts < 3 then',
        '            GameAPI:ConsEventRequest(200,200,1,0)',
        '            unacknowledgedAttempts=unacknowledgedAttempts + 1',
        '        end',
        '        requestTicks=0',
        '    else',
        '        requestTicks=requestTicks + 1',
        '    end',
        '    RebornPersonalInfoRenderStats()',
        'end',
        '',
        'function RebornPersonalInfoSpeedHovered()',
        '    uiapi:Helper(false,SPEED_TOOLTIP,this:Instance())',
        'end',
        '',
        'function RebornPersonalInfoPenetrationHovered()',
        '    local class=RebornPersonalInfoGetClass()',
        '    local tooltip=UNKNOWN_TOOLTIP',
        '    if class == 0 or class == 1 then',
        '        tooltip=PHYSICAL_TOOLTIP',
        '    elseif class == 2 or class == 3 then',
        '        tooltip=MAGICAL_TOOLTIP',
        '    end',
        '    uiapi:Helper(false,tooltip,this:Instance())',
        'end',
        '',
        'function RebornPersonalInfoStatsLeft()',
        '    uiapi:Helper()',
        'end',
        '') -join "`r`n")
    if (-not $FixedPenetration) { return $lua }
    $formatterAnchor = @(
        'local function RebornPersonalInfoFormatBasisPoints(value)',
        '    local basisPoints=math.floor(value)',
        '    local whole=math.floor(basisPoints / 100)',
        '    local fraction=basisPoints - whole * 100',
        '    if fraction == 0 then',
        '        return whole.."%"',
        '    elseif fraction < 10 then',
        '        return whole..".0"..fraction.."%"',
        '    elseif fraction - math.floor(fraction / 10) * 10 == 0 then',
        '        return whole.."."..math.floor(fraction / 10).."%"',
        '    end',
        '    return whole.."."..fraction.."%"',
        'end') -join "`r`n"
    $fixedFormatter = @(
        'local function RebornPersonalInfoFormatFixedBasisPoints(value)',
        '    local basisPoints=math.floor(value)',
        '    local whole=math.floor(basisPoints / 100)',
        '    local fraction=basisPoints - whole * 100',
        '    if fraction < 10 then',
        '        return whole..".0"..fraction.."%"',
        '    end',
        '    return whole.."."..fraction.."%"',
        'end') -join "`r`n"
    if ([regex]::Matches($lua,
            [regex]::Escape($formatterAnchor)).Count -ne 1) {
        throw 'PersonalInfo basis-point formatter anchor is not unique.'
    }
    $lua = $lua.Replace($formatterAnchor,
        $formatterAnchor + "`r`n`r`n" + $fixedFormatter)
    $oldCall = 'penetrationValue:SetText(RebornPersonalInfoFormatBasisPoints('
    if ([regex]::Matches($lua, [regex]::Escape($oldCall)).Count -ne 2) {
        throw 'PersonalInfo penetration formatter calls are not canonical.'
    }
    return $lua.Replace($oldCall,
        'penetrationValue:SetText(RebornPersonalInfoFormatFixedBasisPoints(')
}

function Get-ConstellationStatsPrelude([string]$NewLine) {
    return @(
        '-- REBORN_PERSONAL_INFO_STATS_PRELUDE_BEGIN',
        'local SID_REBORN_PERSONAL_INFO_STATS=200',
        'local REBORN_PERSONAL_INFO_LUA_TYPE=_G.type',
        'local function RebornPersonalInfoClampBasisPoints(value,minimum,maximum)',
        '    if REBORN_PERSONAL_INFO_LUA_TYPE(value) ~= "number" or value ~= value then',
        '        return nil',
        '    end',
        '    if value < minimum then',
        '        return minimum',
        '    elseif value > maximum then',
        '        return maximum',
        '    end',
        '    return math.floor(value + 0.5)',
        'end',
        'RebornPersonalInfoStatsAck=0',
        'RebornPersonalInfoSpeedBasisPoints=nil',
        'RebornPersonalInfoPhysicalPenetrationBasisPoints=nil',
        'RebornPersonalInfoMagicalPenetrationBasisPoints=nil',
        '-- REBORN_PERSONAL_INFO_STATS_PRELUDE_END'
    ) -join $NewLine
}

function Get-ConstellationStatsBranch([string]$NewLine) {
    return @(
        "`t-- REBORN_PERSONAL_INFO_STATS_BRANCH_BEGIN",
        "`tif sid == SID_REBORN_PERSONAL_INFO_STATS then",
        "`t`tRebornPersonalInfoSpeedBasisPoints=",
        "`t`t`tRebornPersonalInfoClampBasisPoints(v1,1000,100000)",
        "`t`tRebornPersonalInfoPhysicalPenetrationBasisPoints=",
        "`t`t`tRebornPersonalInfoClampBasisPoints(v2,0,8000)",
        "`t`tRebornPersonalInfoMagicalPenetrationBasisPoints=",
        "`t`t`tRebornPersonalInfoClampBasisPoints(v3,0,8000)",
        "`t`tRebornPersonalInfoStatsAck=1",
        "`t`tif RebornPersonalInfoRenderStats ~= nil then",
        "`t`t`tRebornPersonalInfoRenderStats()",
        "`t`tend",
        "`t`treturn",
        "`tend",
        "`t-- REBORN_PERSONAL_INFO_STATS_BRANCH_END"
    ) -join $NewLine
}

function Get-ConstellationStatsLuaState([string]$Text) {
    $newLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $prelude = Get-ConstellationStatsPrelude $newLine
    $branch = Get-ConstellationStatsBranch $newLine
    $ownedPattern = '(?i:SID_REBORN_PERSONAL_INFO_STATS|REBORN_PERSONAL_INFO_STATS_(?:PRELUDE|BRANCH)_(?:BEGIN|END))'
    $ownedCount = ([regex]::Matches($Text, $ownedPattern)).Count
    $expectedOwnedCount = ([regex]::Matches(
            $prelude + $newLine + $branch, $ownedPattern)).Count
    $preludeMarkers = ([regex]::Matches($Text,
            'REBORN_PERSONAL_INFO_STATS_PRELUDE_(?:BEGIN|END)')).Count
    $branchMarkers = ([regex]::Matches($Text,
            'REBORN_PERSONAL_INFO_STATS_BRANCH_(?:BEGIN|END)')).Count
    $smsgAnchor = 'function SMsg(sid,v1,v2,v3)' + $newLine
    $smsgCount = ([regex]::Matches(
            $Text, '(?m)^' + [regex]::Escape($smsgAnchor))).Count
    if ($smsgCount -ne 1) {
        throw 'Constellation.lua does not contain the canonical SMsg definition.'
    }
    if ($ownedCount -eq 0) {
        return 'Original'
    }
    $adjacent = $prelude + $newLine +
        'function SMsg(sid,v1,v2,v3)' + $newLine + $branch + $newLine
    if ($preludeMarkers -eq 2 -and $branchMarkers -eq 2 -and
        $ownedCount -eq $expectedOwnedCount -and $Text.Contains($adjacent)) {
        return 'Patched'
    }
    throw 'Constellation.lua has an unknown or partially applied SID200 hook.'
}

function Convert-ConstellationStatsLua(
    [string]$Text,
    [bool]$ToPatched
) {
    $newLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $state = Get-ConstellationStatsLuaState $Text
    if ($ToPatched -and $state -eq 'Patched') { return $Text }
    if (-not $ToPatched -and $state -eq 'Original') { return $Text }
    $prelude = Get-ConstellationStatsPrelude $newLine
    $branch = Get-ConstellationStatsBranch $newLine
    if ($ToPatched) {
        return Replace-RegexOnce $Text (
            '(?m)^' + [regex]::Escape(
                'function SMsg(sid,v1,v2,v3)' + $newLine)) (
            $prelude + $newLine + 'function SMsg(sid,v1,v2,v3)' +
            $newLine + $branch + $newLine) (
            'Constellation SMsg branch anchor')
    }
    $Text = Replace-RegexOnce $Text (
        [regex]::Escape($prelude + $newLine)) '' 'Constellation SID200 prelude'
    return Replace-RegexOnce $Text (
        [regex]::Escape($branch + $newLine)) '' 'Constellation SID200 branch'
}
