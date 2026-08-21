Set-StrictMode -Version Latest

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    $different = if ($Actual -is [string] -and $Expected -is [string]) {
        $Actual -cne $Expected
    } else {
        $Actual -ne $Expected
    }
    if ($different) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Get-FashionPatchProfile {
    [byte[]]$code = @(
        0x9C,0x60,0x8D,0x95,0x38,0x74,0x00,0x00,0x3B,0xC2,0x75,0x2B,
        0x83,0xB8,0xF4,0x00,0x00,0x00,0x00,0x75,0x22,0xE8,0xF6,0xF6,
        0xBA,0xFF,0x85,0xC0,0x74,0x19,0x3B,0x68,0x08,0x75,0x14,0x8B,
        0x88,0x08,0x53,0x00,0x00,0x85,0xC9,0x74,0x0A,0x8B,0x11,0x6A,
        0x01,0xFF,0x92,0xDC,0x00,0x00,0x00,0x61,0x9D,0xB9,0x3E,0x00,
        0x00,0x00,0xE9,0x70,0x9B,0xAE,0xFF)
    [byte[]]$cave = [byte[]]::new(0x60)
    [Array]::Copy($code, $cave, $code.Length)
    return [pscustomobject]@{
        HookOffset = 0x0ADB4E
        Hook = [byte[]]@(0xE9,0x4D,0x64,0x51,0x00)
        CaveOffset = 0x5C3FA0
        Cave = $cave
    }
}

function Set-FashionFixture([string]$Root) {
    $fashion = Get-FashionPatchProfile
    $exe = Join-Path $Root 'Origin.exe'
    [byte[]]$bytes = [IO.File]::ReadAllBytes($exe)
    Copy-RebornBytes $fashion.Hook $bytes $fashion.HookOffset
    Copy-RebornBytes $fashion.Cave $bytes $fashion.CaveOffset
    [IO.File]::WriteAllBytes($exe, $bytes)
}

function Assert-FashionFixture([string]$Root, [string]$Label) {
    $fashion = Get-FashionPatchProfile
    [byte[]]$bytes = [IO.File]::ReadAllBytes((Join-Path $Root 'Origin.exe'))
    Assert-True (Test-RebornBytes $bytes $fashion.HookOffset $fashion.Hook) (
        "$Label Fashion hook")
    Assert-True (Test-RebornBytes $bytes $fashion.CaveOffset $fashion.Cave) (
        "$Label Fashion cave")
}

function Assert-Throws(
    [scriptblock]$Operation,
    [string]$Fragment,
    [string]$Label
) {
    try { & $Operation }
    catch {
        Assert-True ($_.Exception.Message -like "*$Fragment*") (
            "$Label error message")
        return
    }
    throw "Expected failure: $Label"
}

function Test-OffsetAllowed([int]$Offset, [object[]]$Ranges) {
    foreach ($range in $Ranges) {
        if ($Offset -ge $range.Offset -and
            $Offset -lt $range.Offset + $range.Length) { return $true }
    }
    return $false
}

function Get-BackupFileCount([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0 }
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File).Count
}

function New-ClientFixture([string]$Name) {
    $root = Join-Path $script:testRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Copy-Item -LiteralPath (Join-Path $script:fixtureRoot 'Origin.exe') `
        -Destination (Join-Path $root 'Origin.exe')
    foreach ($locale in 'en_us', 'zh_cn') {
        $relative = "Localization\$locale\UI\XML"
        $targetDirectory = Join-Path $root $relative
        [IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
        foreach ($name in 'PersonalInfoUI.xml', 'Constellation.lua',
            'PersonalInfoSpeedStats.lua') {
            $source = Join-Path $script:fixtureRoot "$relative\$name"
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                Copy-Item -LiteralPath $source -Destination (
                    Join-Path $targetDirectory $name)
            }
        }
    }
    return $root
}

function Normalize-Original([string]$Root, [string]$BackupRoot) {
    $speed = & $script:speedPatcher -ClientRoot $Root -Mode Status
    if ($speed.State -ne 'Original') {
        & $script:speedPatcher -ClientRoot $Root -Mode Revert `
            -BackupRoot $BackupRoot | Out-Null
    }
    $quest = & $script:questPatcher -ClientExe (Join-Path $Root 'Origin.exe') `
        -Mode Status
    if ($quest.State -eq 'Patched') {
        & $script:questPatcher -ClientExe (Join-Path $Root 'Origin.exe') `
            -Mode Revert -BackupRoot $BackupRoot | Out-Null
    }
    Assert-Equal (& $script:speedPatcher -ClientRoot $Root -Mode Status).State `
        'Original' "$Root normalized character-stat state"
    Assert-Equal (& $script:questPatcher -ClientExe (
            Join-Path $Root 'Origin.exe') -Mode Status).State `
        'Original' "$Root normalized QuestView state"
}

function Get-LegacyRows(
    [string]$Locale,
    [string]$State,
    [string]$NewLine
) {
    if ($State -eq 'PatchedV1') {
        $movement = Get-SpeedFullLabel $Locale $true
        $riding = Get-SpeedFullLabel $Locale $false
        return @(
            "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$movement`" Visible=`"1`" />",
            '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
            '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
            "    <RidingSpeed Template=`"T_Money`" Rectangle=`"29,548,152,564`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$riding`" />",
            '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
        ) -join $NewLine
    }
    $movement = Get-SpeedCompactLabel $Locale $true
    $riding = Get-SpeedCompactLabel $Locale $false
    if ($State -eq 'PatchedV2') {
        return @(
            "    <spouse Template=`"T_Money`" Rectangle=`"29,522,152,538`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$movement`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
            '    <spouseText Template="T_Money" Rectangle="183,522,221,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
            '    <MovementSpeedPercent Template="T_Money" Rectangle="223,522,239,538" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
            "    <RidingSpeed Template=`"T_Money`" Rectangle=`"29,548,152,564`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$riding`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoRidingSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
            '    <RidingSpeedPercent Template="T_Money" Rectangle="223,548,239,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
        ) -join $NewLine
    }
    return @(
        "    <spouse Template=`"T_Money`" Rectangle=`"24,517,78,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$movement`" Visible=`"1`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoMovementSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <spouseText Template="T_Money" Rectangle="85,517,111,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="100" Visible="1"/>',
        '    <MovementSpeedPercent Template="T_Money" Rectangle="113,517,125,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />',
        "    <RidingSpeed Template=`"T_Money`" Rectangle=`"137,517,200,533`" Format=`"4`" FontColor=`"BASE_HEADCOLOR`" Text=`"$riding`" CanHovered=`"1`" OnHovered=`"RebornPersonalInfoRidingSpeedHovered()`" OnLeft=`"RebornPersonalInfoSpeedLeft()`" />",
        '    <RidingSpeedPercent Template="T_Money" Rectangle="236,517,246,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="%" />'
    ) -join $NewLine
}

function Set-LegacyFixture(
    [string]$Root,
    [string]$State
) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $Root "Localization\$locale\UI\XML"
        $xmlPath = Join-Path $directory 'PersonalInfoUI.xml'
        $xml = [IO.File]::ReadAllText($xmlPath, $encoding)
        $newLine = if ($xml.Contains("`r`n")) { "`r`n" } else { "`n" }
        if ($State -eq 'PatchedV2') {
            $xml = Replace-RegexOnce $xml 'Rectangle="100,100,363,626"' (
                'Rectangle="100,100,363,692"') 'V2 bounds'
        } elseif ($State -eq 'PatchedV3') {
            $xml = Replace-RegexOnce $xml 'Rectangle="100,100,363,626"' (
                'Rectangle="100,100,363,652"') 'V3 bounds'
            $xml = Replace-RegexOnce $xml (
                '(?m)^[ \t]*<BaseBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
                '    <BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,536" />') 'V3 BaseBack'
            $xml = Replace-RegexOnce $xml (
                '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
                '    <FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,536" />') 'V3 FightBack'
        }
        if ($State -ne 'PatchedV3') {
            $fight = [regex]::Match($xml,
                '(?m)^[ \t]*<FightBack\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)').Value
            $speedBack = '    <SpeedBack Template="T_BgWindow" ID="-1" Rectangle="19,513,243,576" />'
            $xml = Replace-RegexOnce $xml ([regex]::Escape($fight)) (
                $fight + $newLine + $speedBack) 'legacy SpeedBack'
        }
        $recommend = if ($State -eq 'PatchedV3') {
            '    <Recommend Template="T_Money" ID="281026" Rectangle="210,517,234,533" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />'
        } else {
            '    <Recommend Template="T_Money" ID="281026" Rectangle="183,548,221,564" Format="4" FontColor="ORDINARY_INFOCOLOR" Text="0" Visible="1" />'
        }
        $xml = Replace-RegexOnce $xml (
            '(?m)^[ \t]*<Recommend\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
            $recommend) 'legacy Recommend'
        $xml = Replace-RegexOnce $xml (
            '(?m)^[ \t]*<spouse\b[^\r\n]*/>[ \t]*\r?\n[ \t]*<spouseText\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)') (
            Get-LegacyRows $locale $State $newLine) 'legacy rows'
        if ($State -in 'PatchedV2', 'PatchedV3') {
            $script = "  <Script File=`"./Localization/$locale/UI/XML/PersonalInfoSpeedStats.lua`" Help=`"`"/>"
            $xml = Replace-RegexOnce $xml '(?m)^</UIConfig>[ \t]*(?=\r?\n|\z)' (
                $script + $newLine + '</UIConfig>') 'legacy script'
            [IO.File]::WriteAllText((Join-Path $directory (
                        'PersonalInfoSpeedStats.lua')), (
                    Get-PersonalInfoSpeedLua $locale), $encoding)
        }
        Assert-True (-not $xml.Contains("`r`r`n")) (
            "$locale synthetic $State has valid line endings")
        [IO.File]::WriteAllText($xmlPath, $xml, $encoding)
        Assert-Equal (Get-PersonalInfoXmlState $xml) $State (
            "$locale synthetic $State XML")
    }
    $profile = Get-CharacterStatsBinaryProfile
    $exe = Join-Path $Root 'Origin.exe'
    [byte[]]$bytes = [IO.File]::ReadAllBytes($exe)
    Copy-RebornBytes $profile.LegacyHook $bytes $profile.HookOffset
    Copy-RebornBytes $profile.LegacyCave $bytes $profile.CaveOffset
    [IO.File]::WriteAllBytes($exe, $bytes)
    Assert-Equal (& $script:speedPatcher -ClientRoot $Root -Mode Status).State (
        $State) "Synthetic $State combined state"
}

function Set-LegacyPartialFixture([string]$Root) {
    Assert-Equal (& $script:speedPatcher -ClientRoot $Root -Mode Status).State (
        'Original') 'Legacy-partial source is normalized'
    $profile = Get-CharacterStatsBinaryProfile
    $exe = Join-Path $Root 'Origin.exe'
    [byte[]]$bytes = [IO.File]::ReadAllBytes($exe)
    Copy-RebornBytes $profile.LegacyHook $bytes $profile.HookOffset
    Copy-RebornBytes $profile.LegacyCave $bytes $profile.CaveOffset
    [IO.File]::WriteAllBytes($exe, $bytes)
    Assert-Equal (& $script:speedPatcher -ClientRoot $Root -Mode Status).State (
        'LegacyPartial') 'Synthetic exact LegacyPartial state'
}

function Assert-Sid200Patched([string]$Root) {
    $status = & $script:speedPatcher -ClientRoot $Root -Mode Status
    Assert-Equal $status.State 'PatchedSid200' 'SID200 patch state'
    Assert-Equal $status.BinaryState 'Original' 'Stock PersonalInfo binary state'
    Assert-True $status.NpcInteractionSafe 'NPC interaction-safe binary'
    Assert-Equal $status.Transport 'pull-only ConsEvent SID 200' 'Transport'
    Assert-Equal $status.WindowRectangle '100,100,454,652' (
        'Widened PersonalInfo status bounds')
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $Root "Localization\$locale\UI\XML"
        $xml = [IO.File]::ReadAllText((Join-Path $directory (
                    'PersonalInfoUI.xml')), $encoding)
        $labels = Get-CharacterStatsUiText $locale
        Assert-True $xml.Contains('Rectangle="100,100,454,652"') (
            "$locale widened window bounds")
        Assert-True $xml.Contains('BtnRect="287,13,324,50"') (
            "$locale aligned close button")
        $validation = Get-RebornPersonalInfoXmlValidation $xml
        Assert-True (Test-RebornPersonalInfoRectangles (
                $validation.Document) $true $xml) "$locale exact widened rows"
        $expectedControls = [ordered]@{
            Defend = @('281114', '24,412,80,428')
            DefendText = @('281014', '84,412,160,428')
            MagicDefend = @('281116', '24,464,80,480')
            MagicDefendText = @('281016', '84,464,160,480')
            Recommend = @('281026', '257,517,333,533')
        }
        foreach ($entry in $expectedControls.GetEnumerator()) {
            $nodes = @($validation.Document.SelectNodes('//*') | Where-Object {
                    $_.Name -ceq $entry.Key
                })
            Assert-Equal $nodes.Count 1 "$locale one $($entry.Key) control"
            Assert-Equal $nodes[0].GetAttribute('Template') 'T_Money' (
                "$locale $($entry.Key) template")
            Assert-Equal $nodes[0].GetAttribute('ID') $entry.Value[0] (
                "$locale $($entry.Key) native ID")
            Assert-Equal $nodes[0].GetAttribute('Rectangle') $entry.Value[1] (
                "$locale $($entry.Key) widened rectangle")
        }
        Assert-True $xml.Contains("Text=`"$($labels.Speed)`" Visible=`"1`"") (
            "$locale Speed label")
        Assert-True $xml.Contains(
            "Text=`"$($labels.Penetration)`" CanHovered=`"1`"") (
            "$locale Penetration label")
        Assert-True $xml.Contains('Rectangle="1,1,2,2" Text="" Visible="1" OnUpdate="RebornPersonalInfoStatsUpdate()"') (
            "$locale dedicated updater")
        Assert-True (-not $xml.Contains('<MovementSpeedPercent ')) (
            "$locale no legacy Movement suffix")
        Assert-True (-not $xml.Contains('<RidingSpeed ')) (
            "$locale no legacy Riding row")
        $luaPath = Join-Path $directory 'PersonalInfoSpeedStats.lua'
        $lua = [IO.File]::ReadAllText($luaPath, $encoding)
        Assert-Equal $lua (Get-PersonalInfoStatsLua $locale) (
            "$locale exact PersonalInfo Lua")
        foreach ($fragment in @(
            'GameAPI:ConsEventRequest(200,200,1,0)',
            'unacknowledgedAttempts < 3',
            'this:RegisterEvent(EUSER_EVENT_FIRSTENTERGAME,',
            'if class == 0 or class == 1 then',
            'elseif class == 2 or class == 3 then',
            'local function RebornPersonalInfoFormatFixedBasisPoints(value)',
            'return whole..".0"..fraction.."%"')) {
            Assert-True $lua.Contains($fragment) "$locale Lua $fragment"
        }
        Assert-True (-not $lua.Contains('type(')) (
            "$locale PersonalInfo never calls mutable global type")
        Assert-Equal ([regex]::Matches($lua,
                'penetrationValue:SetText\(RebornPersonalInfoFormatFixedBasisPoints\(').Count) 2 (
            "$locale both Penetration channels use fixed precision")
        Assert-Equal ([regex]::Matches($lua,
                'speedValue:SetText\(RebornPersonalInfoFormatBasisPoints\(').Count) 1 (
            "$locale Speed keeps trimmed precision")
        $constellation = [IO.File]::ReadAllText((Join-Path $directory (
                    'Constellation.lua')), $encoding)
        Assert-Equal (Get-ConstellationStatsLuaState $constellation) (
            'Patched') "$locale Constellation SID200 state"
        Assert-Equal ([regex]::Matches($constellation,
                '(?m)^function SMsg\(').Count) 1 "$locale one SMsg definition"
        Assert-True ($constellation.IndexOf(
                'if sid == SID_REBORN_PERSONAL_INFO_STATS then') -lt
            $constellation.IndexOf('if wincon == nil then')) (
            "$locale SID200 branch precedes Zodiac UI access")
        Assert-True $constellation.Contains(
            'RebornPersonalInfoClampBasisPoints(v1,1000,100000)') (
            "$locale Speed clamp")
        Assert-True $constellation.Contains(
            'RebornPersonalInfoClampBasisPoints(v2,0,8000)') (
            "$locale Physical Penetration clamp")
        Assert-True $constellation.Contains(
            'RebornPersonalInfoClampBasisPoints(v3,0,8000)') (
            "$locale Magical Penetration clamp")
        $prelude = Get-ConstellationStatsPrelude "`r`n"
        Assert-True (-not $prelude.Contains('type(')) (
            "$locale clamp never calls mutable global type")
    }
}
