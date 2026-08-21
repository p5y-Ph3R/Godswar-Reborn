Set-StrictMode -Version Latest

function Get-RebornXmlElementLines([string]$Text, [string]$Name) {
    $escaped = [regex]::Escape($Name)
    return ,([regex]::Matches($Text,
        "(?m)^[ \t]*<$escaped\b[^\r\n]*(?:/>|>)[ \t]*(?=\r?\n|\z)"))
}

function Test-RebornSingleExactLine($Matches, [string]$Expected) {
    return $Matches.Count -eq 1 -and $Matches[0].Value -ceq $Expected
}

function Test-RebornSinglePatternLine($Matches, [string]$Pattern) {
    return $Matches.Count -eq 1 -and $Matches[0].Value -cmatch $Pattern
}

function Get-PersonalInfoXmlState([string]$Text) {
    $xmlValidation = Get-RebornPersonalInfoXmlValidation $Text
    $stockRectangles = Test-RebornPersonalInfoRectangles (
        $xmlValidation.Document) $false $Text
    $wideRectangles = Test-RebornPersonalInfoRectangles (
        $xmlValidation.Document) $true $Text
    $root = Get-RebornXmlElementLines $Text 'PersonalInfo'
    $rootLine = if ($root.Count -eq 1) { $root[0].Value } else { '' }
    $rootOnLoadCount = ([regex]::Matches(
            $rootLine, '(?i:\sOnLoad\s*=)')).Count
    $rootOnCloseCount = ([regex]::Matches(
            $rootLine, '(?i:\sOnClose\s*=)')).Count
    $rootRectangleCount = ([regex]::Matches(
            $rootLine, '(?i:\sRectangle\s*=)')).Count
    $rootButtonCount = ([regex]::Matches(
            $rootLine, '(?i:\sBtnRect\s*=)')).Count
    $rootVisibleCount = ([regex]::Matches(
            $rootLine, '(?i:\sVisible\s*=)')).Count
    $rootCloseCount = ([regex]::Matches($Text,
            '(?m)^[ \t]*</PersonalInfo>[ \t]*(?=\r?\n|\z)')).Count
    $validRoot = $root.Count -eq 1 -and $rootRectangleCount -eq 1 -and
        $rootButtonCount -eq 1 -and
        $rootVisibleCount -eq 1 -and
        $rootCloseCount -eq 1 -and
        $rootLine -cmatch '\sVisible="0"(?:\s|>)' -and
        $xmlValidation.StructureValid
    $stockRootButton = $rootLine -cmatch (
        '\sBtnRect="196,13,233,50"(?:\s|>)')
    $deployedWideRootButton = $rootLine -cmatch (
        '\sBtnRect="273,13,310,50"(?:\s|>)')
    $wideRootButton = $rootLine -cmatch (
        '\sBtnRect="287,13,324,50"(?:\s|>)')
    $ownedLoadCount = ([regex]::Matches(
            $Text, 'OnLoad="RebornPersonalInfoStatsLoad\(\)"')).Count
    $ownedCloseCount = ([regex]::Matches(
            $Text, 'OnClose="RebornPersonalInfoStatsClose\(\)"')).Count
    $base = Get-RebornXmlElementLines $Text 'BaseBack'
    $fight = Get-RebornXmlElementLines $Text 'FightBack'
    $recommend = Get-RebornXmlElementLines $Text 'Recommend'
    $spouse = Get-RebornXmlElementLines $Text 'spouse'
    $spouseText = Get-RebornXmlElementLines $Text 'spouseText'
    $speedBack = Get-RebornXmlElementLines $Text 'SpeedBack'
    $movePercent = Get-RebornXmlElementLines $Text 'MovementSpeedPercent'
    $riding = Get-RebornXmlElementLines $Text 'RidingSpeed'
    $ridingPercent = Get-RebornXmlElementLines $Text 'RidingSpeedPercent'
    $penetration = Get-RebornXmlElementLines $Text 'Penetration'
    $updater = Get-RebornXmlElementLines $Text 'RebornPersonalInfoStatsUpdater'
    $ownedScript = [regex]::Matches($Text,
        '(?m)^[ \t]*<Script\b[^\r\n]*(?i:PersonalInfoSpeedStats\.lua)[^\r\n]*/>[ \t]*(?=\r?\n|\z)')
    $scriptExact = Test-RebornSinglePatternLine $ownedScript (
        '^  <Script File="\./Localization/(?:en_us|zh_cn)/UI/XML/PersonalInfoSpeedStats\.lua" Help=""/>\z')
    $baseOriginal = '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,510" />'
    $fightOriginal = '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,510" />'
    $baseExtended = '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,536" />'
    $fightExtended = '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,536" />'
    $baseWide = '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,166,536" />'
    $fightWide = '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="168,330,334,536" />'
    $recommendOriginal = '    <Recommend      Template="T_Button2_S" ID="281026" Rectangle="186,123,236,145" Font="MainMap" Format="4"  FontColor="BASE_HEADCOLOR"  SText="PI_X0_30" Visible="0" />'
    $originalRows = (Get-OriginalPersonalInfoRows "`n").Split([char]10)
    $commonOriginal =
        (Test-RebornSingleExactLine $base $baseOriginal) -and
        (Test-RebornSingleExactLine $fight $fightOriginal)
    $commonExtended =
        (Test-RebornSingleExactLine $base $baseExtended) -and
        (Test-RebornSingleExactLine $fight $fightExtended)
    $commonWide =
        (Test-RebornSingleExactLine $base $baseWide) -and
        (Test-RebornSingleExactLine $fight $fightWide)
    $noOwnedCallbacks = $rootOnLoadCount -eq 0 -and
        $rootOnCloseCount -eq 0 -and $ownedLoadCount -eq 0 -and
        $ownedCloseCount -eq 0
    $noExtraRows = $speedBack.Count -eq 0 -and $movePercent.Count -eq 0 -and
        $riding.Count -eq 0 -and $ridingPercent.Count -eq 0 -and
        $penetration.Count -eq 0 -and $updater.Count -eq 0
    $original =
        $validRoot -and $stockRootButton -and $stockRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,363,626"(?:\s|>)' -and
        $noOwnedCallbacks -and $xmlValidation.NoOwnedCallbacks -and
        $ownedScript.Count -eq 0 -and $noExtraRows -and
        $commonOriginal -and
        (Test-RebornSingleExactLine $recommend $recommendOriginal) -and
        (Test-RebornSingleExactLine $spouse $originalRows[0]) -and
        (Test-RebornSingleExactLine $spouseText $originalRows[1])
    $legacyBack = '    <SpeedBack Template="T_BgWindow" ID="-1" Rectangle="19,513,243,576" />'
    $legacyRecommend = '    <Recommend Template="T_Money" ID="281026" Rectangle="183,548,221,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />'
    $legacyTallValues =
        (Test-RebornSingleExactLine $spouseText '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>') -and
        (Test-RebornSingleExactLine $movePercent '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />') -and
        (Test-RebornSingleExactLine $ridingPercent '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />')
    $v1 =
        $validRoot -and $stockRootButton -and $stockRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,363,626"(?:\s|>)' -and
        $noOwnedCallbacks -and $ownedScript.Count -eq 0 -and
        $xmlValidation.NoOwnedCallbacks -and
        $penetration.Count -eq 0 -and $updater.Count -eq 0 -and
        $commonOriginal -and
        (Test-RebornSingleExactLine $speedBack $legacyBack) -and
        (Test-RebornSingleExactLine $recommend $legacyRecommend) -and
        $legacyTallValues -and
        (Test-RebornSinglePatternLine $spouse '^    <spouse Template="T_Money" Rectangle="29,522,152,538" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" Visible="1" />\z') -and
        (Test-RebornSinglePatternLine $riding '^    <RidingSpeed Template="T_Money" Rectangle="29,548,152,564" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" />\z')
    $v2 =
        $validRoot -and $stockRootButton -and $stockRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,363,692"(?:\s|>)' -and
        $noOwnedCallbacks -and $scriptExact -and
        $xmlValidation.LegacyCallbacks -and
        $penetration.Count -eq 0 -and $updater.Count -eq 0 -and
        $commonOriginal -and
        (Test-RebornSingleExactLine $speedBack $legacyBack) -and
        (Test-RebornSingleExactLine $recommend $legacyRecommend) -and
        $legacyTallValues -and
        (Test-RebornSinglePatternLine $spouse '^    <spouse Template="T_Money" Rectangle="29,522,152,538" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" Visible="1" CanHovered="1" OnHovered="RebornPersonalInfoMovementSpeedHovered\(\)" OnLeft="RebornPersonalInfoSpeedLeft\(\)" />\z') -and
        (Test-RebornSinglePatternLine $riding '^    <RidingSpeed Template="T_Money" Rectangle="29,548,152,564" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" CanHovered="1" OnHovered="RebornPersonalInfoRidingSpeedHovered\(\)" OnLeft="RebornPersonalInfoSpeedLeft\(\)" />\z')
    $legacyV3Recommend = '    <Recommend Template="T_Money" ID="281026" Rectangle="210,517,234,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />'
    $v3 =
        $validRoot -and $stockRootButton -and $stockRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,363,652"(?:\s|>)' -and
        $noOwnedCallbacks -and $scriptExact -and $speedBack.Count -eq 0 -and
        $xmlValidation.LegacyCallbacks -and
        $penetration.Count -eq 0 -and $updater.Count -eq 0 -and
        $commonExtended -and
        (Test-RebornSingleExactLine $recommend $legacyV3Recommend) -and
        (Test-RebornSingleExactLine $spouseText '    <spouseText Template="T_Money" Rectangle="85,517,111,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>') -and
        (Test-RebornSingleExactLine $movePercent '    <MovementSpeedPercent Template="T_Money" Rectangle="113,517,125,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />') -and
        (Test-RebornSingleExactLine $ridingPercent '    <RidingSpeedPercent Template="T_Money" Rectangle="236,517,246,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />') -and
        (Test-RebornSinglePatternLine $spouse '^    <spouse Template="T_Money" Rectangle="24,517,78,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" Visible="1" CanHovered="1" OnHovered="RebornPersonalInfoMovementSpeedHovered\(\)" OnLeft="RebornPersonalInfoSpeedLeft\(\)" />\z') -and
        (Test-RebornSinglePatternLine $riding '^    <RidingSpeed Template="T_Money" Rectangle="137,517,200,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" CanHovered="1" OnHovered="RebornPersonalInfoRidingSpeedHovered\(\)" OnLeft="RebornPersonalInfoSpeedLeft\(\)" />\z')
    $sidCallbacks = $rootOnLoadCount -eq 1 -and $rootOnCloseCount -eq 1 -and
        $ownedLoadCount -eq 1 -and $ownedCloseCount -eq 1 -and
        $xmlValidation.Sid200Callbacks -and
        $rootLine -cmatch '\sOnLoad="RebornPersonalInfoStatsLoad\(\)"[^>]*\sOnClose="RebornPersonalInfoStatsClose\(\)"' -and
        $rootLine -cmatch '\sOnLoad="RebornPersonalInfoStatsLoad\(\)"(?:\s|>)' -and
        $rootLine -cmatch '\sOnClose="RebornPersonalInfoStatsClose\(\)"(?:\s|>)'
    $sidRowsAbsent = $speedBack.Count -eq 0 -and
        $movePercent.Count -eq 0 -and $riding.Count -eq 0 -and
        $ridingPercent.Count -eq 0
    $sidUpdater = Test-RebornSingleExactLine $updater (
        '    <RebornPersonalInfoStatsUpdater Type="Text" ID="-1" Rectangle="1,1,2,2" Text="" Visible="1" OnUpdate="RebornPersonalInfoStatsUpdate()" />')
    $sid200V1 =
        $validRoot -and $stockRootButton -and $stockRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,363,652"(?:\s|>)' -and
        $sidCallbacks -and $scriptExact -and $sidRowsAbsent -and
        $commonExtended -and
        (Test-RebornSingleExactLine $recommend '    <Recommend Template="T_Money" ID="281026" Rectangle="210,517,246,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1" />') -and
        (Test-RebornSingleExactLine $spouseText '    <spouseText Template="T_Money" Rectangle="85,517,125,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1"/>') -and
        (Test-RebornSinglePatternLine $spouse '^    <spouse Template="T_Money" Rectangle="24,517,78,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" Visible="1" CanHovered="1" OnHovered="RebornPersonalInfoSpeedHovered\(\)" OnLeft="RebornPersonalInfoStatsLeft\(\)" />\z') -and
        (Test-RebornSinglePatternLine $penetration '^    <Penetration Template="T_Money" Rectangle="137,517,200,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" CanHovered="1" OnHovered="RebornPersonalInfoPenetrationHovered\(\)" OnLeft="RebornPersonalInfoStatsLeft\(\)" />\z') -and
        $sidUpdater
    $sid200FrameV1 =
        $validRoot -and $deployedWideRootButton -and $wideRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,440,652"(?:\s|>)' -and
        $sidCallbacks -and $scriptExact -and $sidRowsAbsent -and
        $commonWide -and
        (Test-RebornSingleExactLine $recommend '    <Recommend Template="T_Money" ID="281026" Rectangle="257,517,333,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1" />') -and
        (Test-RebornSingleExactLine $spouseText '    <spouseText Template="T_Money" Rectangle="84,517,160,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1"/>') -and
        (Test-RebornSinglePatternLine $spouse '^    <spouse Template="T_Money" Rectangle="24,517,80,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" Visible="1" CanHovered="1" OnHovered="RebornPersonalInfoSpeedHovered\(\)" OnLeft="RebornPersonalInfoStatsLeft\(\)" />\z') -and
        (Test-RebornSinglePatternLine $penetration '^    <Penetration Template="T_Money" Rectangle="173,517,253,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" CanHovered="1" OnHovered="RebornPersonalInfoPenetrationHovered\(\)" OnLeft="RebornPersonalInfoStatsLeft\(\)" />\z') -and
        $sidUpdater
    $sid200 =
        $validRoot -and $wideRootButton -and $wideRectangles -and
        $rootLine -cmatch '\sRectangle="100,100,454,652"(?:\s|>)' -and
        $sidCallbacks -and $scriptExact -and $sidRowsAbsent -and
        $commonWide -and
        (Test-RebornSingleExactLine $recommend '    <Recommend Template="T_Money" ID="281026" Rectangle="257,517,333,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1" />') -and
        (Test-RebornSingleExactLine $spouseText '    <spouseText Template="T_Money" Rectangle="84,517,160,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="--" Visible="1"/>') -and
        (Test-RebornSinglePatternLine $spouse '^    <spouse Template="T_Money" Rectangle="24,517,80,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" Visible="1" CanHovered="1" OnHovered="RebornPersonalInfoSpeedHovered\(\)" OnLeft="RebornPersonalInfoStatsLeft\(\)" />\z') -and
        (Test-RebornSinglePatternLine $penetration '^    <Penetration Template="T_Money" Rectangle="173,517,253,533" Format="4" FontColor="BASE_HEADCOLOR" Text="[^"\r\n]+" CanHovered="1" OnHovered="RebornPersonalInfoPenetrationHovered\(\)" OnLeft="RebornPersonalInfoStatsLeft\(\)" />\z') -and
        (Test-RebornPersonalInfoFrameInsets $xmlValidation.Document) -and
        $sidUpdater
    $states = @(
        if ($original) { 'Original' }
        if ($v1) { 'PatchedV1' }
        if ($v2) { 'PatchedV2' }
        if ($v3) { 'PatchedV3' }
        if ($sid200V1) { 'PatchedSid200V1' }
        if ($sid200FrameV1) { 'PatchedSid200FrameV1' }
        if ($sid200) { 'PatchedSid200' }
    )
    if ($states.Count -ne 1) {
        throw 'PersonalInfoUI.xml has an unknown or partially applied character-stat layout.'
    }
    return $states[0]
}
