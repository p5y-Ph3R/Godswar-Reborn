$marker = 'Standalone Gear Enhancement Forge Clone (GWGE3)'
$legacyMarker = 'Standalone Gear Enhancement (GWGE2)'
$locales = @('en_us', 'zh_cn')

function Read-Utf8File([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    return [pscustomobject]@{
        Text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
        HasBom = $hasBom
    }
}

function Convert-ToUtf8Bytes([string]$Text, [bool]$HasBom) {
    [byte[]]$body = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    if (-not $HasBom) {
        return $body
    }

    [byte[]]$result = [byte[]]::new($body.Length + 3)
    $result[0] = 0xEF
    $result[1] = 0xBB
    $result[2] = 0xBF
    [Array]::Copy($body, 0, $result, 3, $body.Length)
    return $result
}

function Write-AtomicBytes([string]$Path, [byte[]]$Bytes) {
    $directory = Split-Path -Parent $Path
    $temporaryPath = Join-Path $directory (
        '.gwge3-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporaryPath, $Bytes)
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-NewLine([string]$Text) {
    if ($Text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

function Count-Ordinal([string]$Text, [string]$Value) {
    $count = 0
    $offset = 0
    while (($offset = $Text.IndexOf(
            $Value,
            $offset,
            [StringComparison]::Ordinal)) -ge 0) {
        $count++
        $offset += $Value.Length
    }
    return $count
}

function Replace-ExactOnce(
    [string]$Text,
    [string]$OldValue,
    [string]$NewValue,
    [string]$Label
) {
    if ((Count-Ordinal $Text $OldValue) -ne 1) {
        throw "$Label does not contain its expected source exactly once."
    }
    return $Text.Replace($OldValue, $NewValue)
}

function Remove-MarkedBlocks(
    [string]$Text,
    [string]$StartToken,
    [string]$EndToken,
    [string]$Label
) {
    if ((Count-Ordinal $Text $StartToken) -ne
        (Count-Ordinal $Text $EndToken)) {
        throw "$Label contains an incomplete $StartToken patch block."
    }

    while (($start = $Text.IndexOf(
            $StartToken,
            [StringComparison]::Ordinal)) -ge 0) {
        $end = $Text.IndexOf(
            $EndToken,
            $start + $StartToken.Length,
            [StringComparison]::Ordinal)
        if ($end -lt 0) {
            throw "$Label contains an incomplete $StartToken patch block."
        }

        $lineStart = $Text.LastIndexOf("`n", $start)
        if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart++ }
        if ($lineStart -gt 0) {
            $previousLineEnd = $lineStart - 1
            if ($previousLineEnd -gt 0 -and
                $Text[$previousLineEnd - 1] -eq "`r") {
                $previousLineEnd--
            }
            $previousLineStart = $Text.LastIndexOf(
                "`n",
                [Math]::Max(0, $previousLineEnd - 1)) + 1
            $previousLine = $Text.Substring(
                $previousLineStart,
                $previousLineEnd - $previousLineStart)
            if ([string]::IsNullOrWhiteSpace($previousLine)) {
                $lineStart = $previousLineStart
            }
        }
        $removeEnd = $end + $EndToken.Length
        if ($removeEnd -lt $Text.Length -and $Text[$removeEnd] -eq "`r") {
            $removeEnd++
        }
        if ($removeEnd -lt $Text.Length -and $Text[$removeEnd] -eq "`n") {
            $removeEnd++
        }
        $Text = $Text.Remove($lineStart, $removeEnd - $lineStart)
    }
    return $Text
}

function Remove-AllPatchBlocks([string]$Text, [string]$Kind, [string]$Label) {
    if ($Kind -eq 'Xml') {
        $Text = Remove-MarkedBlocks $Text "<!-- $legacyMarker -->" (
            "<!-- /$legacyMarker -->") $Label
        $Text = Remove-MarkedBlocks $Text (
            "<!-- $legacyMarker Forge skin -->") (
            "<!-- /$legacyMarker Forge skin -->") $Label
        $Text = Remove-MarkedBlocks $Text "<!-- $marker -->" (
            "<!-- /$marker -->") $Label
        return $Text
    }

    $Text = Remove-MarkedBlocks $Text "-- $legacyMarker" (
        "-- /$legacyMarker") $Label
    $Text = Remove-MarkedBlocks $Text "-- $marker" (
        "-- /$marker") $Label
    return $Text
}

function Convert-TemplateNewLines([string]$Template, [string]$NewLine) {
    return $Template.Replace("`r`n", "`n").Replace("`n", $NewLine)
}

function Get-SystemBarXmlBlock([string]$NewLine) {
    return (@(
        "`t<!-- $marker -->",
        "`t<Gear Type=`"Button`" ID=`"211016`" Rectangle=`"98,-19,125,8`" Texture=`"./Localization/en_us/UI/Texture/Main.gwo`" TexturePos=`"152,432`" OnClick=`"GearEnhancementBtn_OnClick()`" OnHotKey=`"GearEnhancementBtn_OnClick()`" HotKey=`"69`" Visible=`"1`"/>",
        "`t<!-- /$marker -->",
        ''
    ) -join $NewLine)
}

function Set-SystemBarXml([string]$Text, [bool]$Apply, [string]$Label) {
    $Text = Remove-AllPatchBlocks $Text 'Xml' $Label
    if (-not $Apply) { return $Text }

    if ($Text.Contains('<Gear Type="Button" ID="211016"')) {
        throw "$Label already contains a conflicting Gear launcher button."
    }
    $newLine = Get-NewLine $Text
    $anchor = "`t<LiveSkill Type=`"Button`""
    return Replace-ExactOnce $Text $anchor (
        (Get-SystemBarXmlBlock $newLine) + $anchor) $Label
}

function Get-SystemBarLuaMemberBlock([string]$NewLine) {
    return (@(
        "`t-- $marker",
        "`tGear = win:GetChild(`"Gear`");",
        "`t-- /$marker",
        ''
    ) -join $NewLine)
}

function Get-SystemBarLuaToggleBlock([string]$NewLine) {
    return (@(
        "`t-- $marker",
        "`tGear:Visible(not Gear:IsVisible());",
        "`tif Gear:IsVisible() then",
        "`t`tGear:Top();",
        "`tend",
        "`t-- /$marker",
        ''
    ) -join $NewLine)
}

function Get-SystemBarLuaFunctionBlock([string]$NewLine) {
    return (@(
        '',
        "-- $marker",
        'function GearEnhancementBtn_OnClick()',
        "`tGameAPI:ExecUIScript(118);",
        'end',
        "-- /$marker",
        ''
    ) -join $NewLine)
}

function Set-SystemBarLua([string]$Text, [bool]$Apply, [string]$Label) {
    $Text = Remove-AllPatchBlocks $Text 'Lua' $Label
    if (-not $Apply) { return $Text }

    if ($Text.Contains('GearEnhancementBtn_OnClick') -or
        $Text.Contains('win:GetChild("Gear")')) {
        throw "$Label contains an unmarked Gear Enhancement launcher."
    }

    $newLine = Get-NewLine $Text
    $memberAnchor = "`tLiveSkill = win:GetChild(`"LiveSkill`"`);${newLine}"
    $Text = Replace-ExactOnce $Text $memberAnchor (
        $memberAnchor + (Get-SystemBarLuaMemberBlock $newLine)) $Label

    $toggleAnchor = (@(
        '     LiveSkill:Visible(not LiveSkill:IsVisible());',
        "`tif LiveSkill:IsVisible() then",
        "`t`tLiveSkill:Top();",
        "`tend",
        ''
    ) -join $newLine)
    $Text = Replace-ExactOnce $Text $toggleAnchor (
        $toggleAnchor + (Get-SystemBarLuaToggleBlock $newLine)) $Label
    return $Text + (Get-SystemBarLuaFunctionBlock $newLine)
}

function Get-NpcFunXmlBlock([string]$NewLine) {
    $template = @'
    <!-- Standalone Gear Enhancement Forge Clone (GWGE3) -->
    <!-- Ordinary NPC dialogs keep their original cosmetic frame. -->
    <GWGE3_LegacyFrame Template="T_SimpleWindow" ID="-1" Rectangle="0,0,600,290" Enable="0" Visible="1"/>

    <!-- Exact 350x582 EquipForgeExUI shell, shown only for dialog 118. -->
    <GWGE3_ForgeFrame Template="T_NormalWindow" ID="-1" Rectangle="0,0,350,582" BtnRect="283,13,321,50" BtnPos="0,80" Enable="0" Visible="0"/>
    <GWGE3_Title Type="Text" ID="-1" Rectangle="28,13,275,43" TexturePos="1024,1024" Text="Gear Enhancement" Font="Mainlogin2" FontColor="YELLOW_TEXTCOLOR" TextFormat="5" Enable="0" Visible="0"/>

    <!-- Exact Forge tab-row dots and work-area separator geometry. -->
    <GWGE3_Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="106,67,122,73" Text="" Enable="0" Visible="0"/>
    <GWGE3_Tp2 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="169,67,185,73" Text="" Enable="0" Visible="0"/>
    <GWGE3_MiddleLeft Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="146,174" Rectangle="14,334,28,347" Text="" Enable="0" Visible="0"/>
    <GWGE3_Middle Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="151,173" Rectangle="28,333,29,346" Text="" Enable="0" Visible="0"/>
    <GWGE3_MiddleRight Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="146,189" Rectangle="321,334,337,347" Text="" Enable="0" Visible="0"/>
    <GWGE3_MaterialText Type="Text" ID="-1" Rectangle="2,337,90,367" TexturePos="1024,1024" Text="Instructions" Font="MainFonts" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" Enable="0" Visible="0"/>
    <GWGE3_BackWin Template="T_BgWindow" ID="-1" Rectangle="26,366,325,528" Enable="0" Visible="0"/>
    <GWGE3_Readme1 Type="Text" ID="-1" Rectangle="30,377,310,421" TexturePos="1024,1024" Text="Choose Add, Enhance, or Delete." Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="16" Enable="0" Visible="0"/>
    <GWGE3_Readme2 Type="Text" ID="-1" Rectangle="30,427,310,501" TexturePos="1024,1024" Text="" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="16" Enable="0" Visible="0"/>
    <!-- /Standalone Gear Enhancement Forge Clone (GWGE3) -->
'@
    return Convert-TemplateNewLines $template $NewLine
}

function Set-NpcFunXml(
    [string]$Text,
    [bool]$Apply,
    [string]$Label
) {
    $Text = Remove-AllPatchBlocks $Text 'Xml' $Label
    $stockRoot = '  <FirstWin Template="T_SimpleWindow" ID="800000" Rectangle="380,112,980,402"  BtnRect="566,7,589,31" BtnPos="454,693"   Visible="0">'
    $cloneRoot = '  <FirstWin Type="Window" ID="800000" Modal="0" Rectangle="380,112,980,694" BtnRect="566,7,589,31" BtnPos="454,693" UseEsc="1" Visible="0">'
    $stockClose = '    <CloseBtn Template="T_CloseButton" ID="800040" Rectangle="240,13,532,50" Text="" Visible="0"/>'
    $unsafeClose = '    <CloseBtn Template="T_CloseButton" ID="800040" Rectangle="560,13,597,50" Text="" Visible="0"/>'

    if ((Count-Ordinal $Text $unsafeClose) -eq 1) {
        $Text = Replace-ExactOnce $Text $unsafeClose $stockClose $Label
    }
    if ((Count-Ordinal $Text $stockClose) -ne 1) {
        throw "$Label does not preserve native CloseBtn 800040."
    }

    if (-not $Apply) {
        if ((Count-Ordinal $Text $cloneRoot) -eq 1) {
            return Replace-ExactOnce $Text $cloneRoot $stockRoot $Label
        }
        if ((Count-Ordinal $Text $stockRoot) -ne 1) {
            throw "$Label contains an unknown FirstWin root revision."
        }
        return $Text
    }

    if ((Count-Ordinal $Text $stockRoot) -eq 1) {
        $Text = Replace-ExactOnce $Text $stockRoot $cloneRoot $Label
    }
    elseif ((Count-Ordinal $Text $cloneRoot) -ne 1) {
        throw "$Label contains an unknown FirstWin root revision."
    }

    $newLine = Get-NewLine $Text
    return Replace-ExactOnce $Text ($cloneRoot + $newLine) (
        $cloneRoot + $newLine + (Get-NpcFunXmlBlock $newLine)) $Label
}
