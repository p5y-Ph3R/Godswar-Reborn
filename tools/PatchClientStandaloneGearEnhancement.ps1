param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [ValidateSet('Verify', 'Apply', 'Revert')]
    [string]$Mode = 'Verify',
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Get-EnhancerLuaBlock([string]$NewLine) {
    $template = @'
-- Standalone Gear Enhancement Forge Clone (GWGE3)
local GWGE3_Win = UIAPI:GetElement("FirstWin");
local GWGE3_LegacyFrame = GWGE3_Win:GetChild("GWGE3_LegacyFrame");
local GWGE3_ForgeFrame = GWGE3_Win:GetChild("GWGE3_ForgeFrame");
local GWGE3_Title = GWGE3_Win:GetChild("GWGE3_Title");
local GWGE3_Tp1 = GWGE3_Win:GetChild("GWGE3_Tp1");
local GWGE3_Tp2 = GWGE3_Win:GetChild("GWGE3_Tp2");
local GWGE3_MiddleLeft = GWGE3_Win:GetChild("GWGE3_MiddleLeft");
local GWGE3_Middle = GWGE3_Win:GetChild("GWGE3_Middle");
local GWGE3_MiddleRight = GWGE3_Win:GetChild("GWGE3_MiddleRight");
local GWGE3_MaterialText = GWGE3_Win:GetChild("GWGE3_MaterialText");
local GWGE3_BackWin = GWGE3_Win:GetChild("GWGE3_BackWin");
local GWGE3_Readme1 = GWGE3_Win:GetChild("GWGE3_Readme1");
local GWGE3_Readme2 = GWGE3_Win:GetChild("GWGE3_Readme2");
local GWGE3_Close = GWGE3_Win:GetChild("CloseBtn");
local GWGE3_A1DefaultText = FirstWin_ButtonA1:GetText();
local GWGE3_A2DefaultText = FirstWin_ButtonA2:GetText();
local GWGE3_ForgeDecorations = {
	GWGE3_ForgeFrame, GWGE3_Title, GWGE3_Tp1, GWGE3_Tp2,
	GWGE3_MiddleLeft, GWGE3_Middle, GWGE3_MiddleRight,
	GWGE3_MaterialText, GWGE3_BackWin, GWGE3_Readme1, GWGE3_Readme2
};
local GWGE3_TabText = { "Add", "Enhance", "Delete" };
local GWGE3_TabSubID = { 3, 2, 6 };
local GWGE3_TabX = { 53, 116, 178 };
local GWGE3_TabWidth = { 60, 60, 61 };

local function GWGE3_Resize(Control,Width,Height)
	local CurrentWidth = Control:GetWidth();
	local CurrentHeight = Control:GetHeight();
	if CurrentWidth <= 0 or CurrentHeight <= 0 then
		return;
	end;
	if math.abs(CurrentWidth - Width) <= 1 and math.abs(CurrentHeight - Height) <= 1 then
		return;
	end;
	local ScaleX = Width / CurrentWidth;
	local ScaleY = Height / CurrentHeight;
	Control:OnScale(ScaleX,ScaleY);

	-- OnScale is multiplicative on this build. The readback fallback also
	-- makes this safe if another build treats it as an absolute base scale.
	local ActualWidth = Control:GetWidth();
	local ActualHeight = Control:GetHeight();
	if math.abs(ActualWidth - Width) > 1 or math.abs(ActualHeight - Height) > 1 then
		local BaseWidth = ActualWidth / ScaleX;
		local BaseHeight = ActualHeight / ScaleY;
		if BaseWidth > 0 and BaseHeight > 0 then
			Control:OnScale(Width / BaseWidth,Height / BaseHeight);
		end;
	end;
end

local function GWGE3_ShowForgeDecorations(Visible)
	for Index = 1, table.getn(GWGE3_ForgeDecorations) do
		GWGE3_ForgeDecorations[Index]:Visible(Visible);
	end;
	GWGE3_LegacyFrame:Visible(not Visible);
	if Visible then
		GWGE3_Win:SetPosition(300,188);
		GWGE3_Close:SetPosition(283,13);
		GWGE3_Resize(GWGE3_Close,38,37);
		GWGE3_Close:Visible(true);
		GWGE3_Close:Top();
	else
		GWGE3_Win:SetPosition(380,112);
		GWGE3_Close:SetPosition(240,13);
		GWGE3_Resize(GWGE3_Close,292,37);
		GWGE3_Close:Visible(false);
	end;
end

local function GWGE3_RestoreNativeControls()
	for Index = 1, 3 do
		local Button = GWGE3_Win:GetChild("FirstWin_Button" .. Index);
		GWGE3_Resize(Button,220,21);
		Button:SetTexture("./Localization/en_us/UI/Texture/main.gwo");
		Button:SetTexturePos(609,620);
		UIAPI:SetChecked(false,Button);
	end;
	GWGE3_Resize(FirstWin_ButtonA1,60,27);
	GWGE3_Resize(FirstWin_ButtonA2,60,27);
	FirstWin_ButtonA1:SetTexturePos(38,80);
	FirstWin_ButtonA2:SetTexturePos(38,80);
	FirstWin_ButtonA1:SetText(GWGE3_A1DefaultText);
	FirstWin_ButtonA2:SetText(GWGE3_A2DefaultText);
	GWGE3_Resize(FirstWin_Text1,557,206);
	GWGE3_Resize(FirstWin_Text2,557,206);
	GWGE3_Resize(FirstWin_Text3,527,206);
end

local function GWGE3_SetTab(Index,Selected)
	local Button = GWGE3_Win:GetChild("FirstWin_Button" .. Index);
	GWGE3_Resize(Button,GWGE3_TabWidth[Index],23);
	Button:SetTexture("./Localization/en_us/UI/Texture/main.gwo");
	Button:SetTexturePos(244,450);
	Button:SetText(GWGE3_TabText[Index]);
	Button:SetPosition(GWGE3_TabX[Index],48);
	Button:Enable(true);
	Button:Visible(true);
	UIAPI:SetChecked(Selected,Button);
	Button:Top();
end

local function GWGE3_ShowTabs(SelectedSubID)
	for Index = 1, 3 do
		GWGE3_SetTab(Index,GWGE3_TabSubID[Index] == SelectedSubID);
	end;
	for Index = 4, 12 do
		GWGE3_Win:GetChild("FirstWin_Button" .. Index):Visible(false);
	end;
end

local function GWGE3_ShowMenu()
	GWGE3_ShowForgeDecorations(true);
	FirstWin_ItemBtn1:Visible(false);
	FirstWin_ItemBtn2:Visible(false);
	FirstWin_ItemBtn3:Visible(false);
	FirstWin_ItemBtn4:Visible(false);
	FirstWin_ButtonA1:Visible(false);
	FirstWin_ButtonA2:Visible(false);
	GWGE3_Readme1:SetText("Choose Add, Enhance, or Delete.");
	GWGE3_Readme2:SetText("");
end

local function GWGE3_ShowOperation(SubID)
	GWGE3_ShowForgeDecorations(true);
	GWGE3_ShowTabs(SubID);

	FirstWin_Text1:SetText("Gear");
	FirstWin_Text1:SetPosition(52,97);
	GWGE3_Resize(FirstWin_Text1,170,42);
	FirstWin_ItemBtn1:SetPosition(242,97);
	FirstWin_ItemBtn1:Visible(true);

	FirstWin_Text3:SetText("Attribute Stone");
	FirstWin_Text3:SetPosition(52,149);
	GWGE3_Resize(FirstWin_Text3,170,42);
	FirstWin_ItemBtn3:SetPosition(242,149);
	FirstWin_ItemBtn3:Visible(true);

	if SubID == 3 then
		FirstWin_Text2:SetText("Flame Spark");
		GWGE3_Readme1:SetText("Add an attribute to the selected gear.");
		GWGE3_Readme2:SetText("Insert gear, an Attribute Stone, and one Flame Spark.");
	elseif SubID == 2 then
		FirstWin_Text2:SetText("Quartz Plate (1 piece)");
		GWGE3_Readme1:SetText("Enhance an existing gear attribute.");
		GWGE3_Readme2:SetText("Insert gear, the matching Attribute Stone, and one Quartz Plate.");
	else
		FirstWin_Text2:SetText("Water Grain");
		GWGE3_Readme1:SetText("Delete an existing gear attribute.");
		GWGE3_Readme2:SetText("Insert gear, the matching Attribute Stone, and one Water Grain.");
	end;
	FirstWin_Text2:SetPosition(52,203);
	GWGE3_Resize(FirstWin_Text2,170,42);
	FirstWin_ItemBtn2:SetPosition(242,203);
	FirstWin_ItemBtn2:Visible(true);
	FirstWin_ItemBtn4:Visible(false);

	FirstWin_ButtonA1:SetText("Start");
	FirstWin_ButtonA1:SetTexturePos(197,81);
	FirstWin_ButtonA1:SetPosition(194,538);
	GWGE3_Resize(FirstWin_ButtonA1,68,27);
	FirstWin_ButtonA1:Visible(true);
	FirstWin_ButtonA2:SetText("Reset");
	FirstWin_ButtonA2:SetTexturePos(197,81);
	FirstWin_ButtonA2:SetPosition(262,538);
	GWGE3_Resize(FirstWin_ButtonA2,68,27);
	FirstWin_ButtonA2:Visible(true);

	FirstWin_ItemBtn1:Top();
	FirstWin_ItemBtn2:Top();
	FirstWin_ItemBtn3:Top();
	FirstWin_ButtonA1:Top();
	FirstWin_ButtonA2:Top();
end

local function GWGE3_ShowResult()
	GWGE3_ShowForgeDecorations(true);
	for Index = 1, 12 do
		GWGE3_Win:GetChild("FirstWin_Button" .. Index):Visible(false);
	end;
	FirstWin_ItemBtn1:Visible(false);
	FirstWin_ItemBtn2:Visible(false);
	FirstWin_ItemBtn3:Visible(false);
	FirstWin_ItemBtn4:Visible(false);
	FirstWin_Text1:SetPosition(30,390);
	GWGE3_Resize(FirstWin_Text1,280,80);
	FirstWin_Text2:Visible(false);
	FirstWin_Text3:Visible(false);
	GWGE3_Readme1:SetText("");
	GWGE3_Readme2:SetText("");
	FirstWin_ButtonA1:SetText("OK");
	FirstWin_ButtonA1:SetTexturePos(197,81);
	FirstWin_ButtonA1:SetPosition(194,538);
	GWGE3_Resize(FirstWin_ButtonA1,68,27);
	FirstWin_ButtonA1:Visible(true);
	FirstWin_ButtonA2:Visible(false);
	FirstWin_Text1:Top();
	FirstWin_ButtonA1:Top();
end

local GWGE3_OriginalSetNpcFunUI = Set_NpcFun_UI;
function Set_NpcFun_UI(Type,Index)
	GWGE3_OriginalSetNpcFunUI(Type,Index);
	if Type == NPC_FLAG_SYS_ENHANCER then
		GWGE3_ShowMenu();
	else
		GWGE3_ShowForgeDecorations(false);
		GWGE3_RestoreNativeControls();
	end;
end

local GWGE3_OriginalEnhancerSetText = NpcFunEnhancer_SetText;
function NpcFunEnhancer_SetText(Type,Index,BtnID,SubID)
	GWGE3_OriginalEnhancerSetText(Type,Index,BtnID,SubID);
	if Index == 1 then
		GWGE3_ShowForgeDecorations(true);
		if SubID == 3 then
			GWGE3_SetTab(1,false);
		elseif SubID == 2 then
			GWGE3_SetTab(2,false);
		elseif SubID == 6 then
			GWGE3_SetTab(3,false);
		elseif SubID >= 999 then
			GWGE3_ShowResult();
		end;
	end;
end

local GWGE3_OriginalEnhancerSetMsg = NpcFunEnhancer_SetMsg;
function NpcFunEnhancer_SetMsg(Type,Index,PreSubID,SubID)
	GWGE3_OriginalEnhancerSetMsg(Type,Index,PreSubID,SubID);
	if Index == 1 and (SubID == 2 or SubID == 3 or SubID == 6) then
		GWGE3_ShowOperation(SubID);
	elseif Index == 1 then
		GWGE3_ShowResult();
	end;
end
-- /Standalone Gear Enhancement Forge Clone (GWGE3)
'@
    return $NewLine + (Convert-TemplateNewLines $template $NewLine)
}

function Set-EnhancerLua(
    [string]$Text,
    [bool]$Apply,
    [string]$Label
) {
    $Text = Remove-AllPatchBlocks $Text 'Lua' $Label
    # This shipped enhancer script ends at `end` without a terminal newline.
    # The bridge is appended at EOF, so normalize that delimiter to keep both
    # Apply idempotent and Revert byte-for-byte exact.
    $Text = $Text.TrimEnd([char[]]"`r`n")
    if (-not $Apply) { return $Text }

    if ($Text.Contains('GWGE3_OriginalSetNpcFunUI') -or
        $Text.Contains('GWGE2_OriginalSetNpcFunUI')) {
        throw "$Label contains an unmarked Gear Enhancement bridge."
    }
    return $Text + (Get-EnhancerLuaBlock (Get-NewLine $Text))
}

function Assert-ForgeSource([string]$Path) {
    $source = Read-Utf8File $Path
    $document = [Xml.XmlDocument]::new()
    $document.LoadXml($source.Text)
    $root = $document.SelectSingleNode('/UIConfig/EquipForge')
    if ($null -eq $root -or
        $root.GetAttribute('Template') -ne 'T_NormalWindow' -or
        $root.GetAttribute('Rectangle') -ne '300,188,650,770' -or
        $root.GetAttribute('BtnRect') -ne '283,13,321,50') {
        throw "$Path is not the supported EquipForgeExUI source."
    }
    $backWin = $root.SelectSingleNode('BackWin')
    if ($null -eq $backWin -or
        $backWin.GetAttribute('Rectangle') -ne '26,366,325,528') {
        throw "$Path does not contain the expected Forge work area."
    }
}

function Assert-PatchedNpcFun([string]$Text, [string]$Label) {
    $document = [Xml.XmlDocument]::new()
    $document.LoadXml($Text)
    $root = $document.SelectSingleNode('/UIConfig/FirstWin')
    if ($null -eq $root -or $root.GetAttribute('Type') -ne 'Window' -or
        $root.GetAttribute('Rectangle') -ne '380,112,980,694') {
        throw "$Label does not contain the neutral native FirstWin root."
    }
    $forgeFrame = $root.SelectSingleNode('GWGE3_ForgeFrame')
    if ($null -eq $forgeFrame -or
        $forgeFrame.GetAttribute('Template') -ne 'T_NormalWindow' -or
        $forgeFrame.GetAttribute('Rectangle') -ne '0,0,350,582' -or
        $forgeFrame.GetAttribute('BtnRect') -ne '283,13,321,50') {
        throw "$Label does not contain the exact 350x582 Forge shell."
    }
    $backWin = $root.SelectSingleNode('GWGE3_BackWin')
    if ($null -eq $backWin -or
        $backWin.GetAttribute('Rectangle') -ne '26,366,325,528') {
        throw "$Label does not preserve the Forge instruction panel geometry."
    }
    foreach ($id in @('800001', '800002', '800003', '800020', '800021',
            '800031', '800032', '800033', '800040')) {
        $nodes = $document.SelectNodes("//*[@ID='$id']")
        if ($nodes.Count -ne 1) {
            throw "$Label must contain native control ID $id exactly once."
        }
    }
}

$systemBarXmlPaths = @{}
$npcFunXmlPaths = @{}
foreach ($locale in $locales) {
    $systemBarXmlPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\SystemBar.xml")
    $npcFunXmlPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\NpcFun.xml")
}
$systemBarLuaPath = Join-Path $ClientRoot (
    'Localization\en_us\UI\XML\SystemBar.lua')
$enhancerLuaPath = Join-Path $ClientRoot (
    'Localization\en_us\UI\XML\NpcFun\NpcFunEnhancer.lua')
$forgeSourcePath = Join-Path $ClientRoot (
    'Localization\en_us\UI\XML\EquipForgeExUI.xml')
$allPaths = @($systemBarXmlPaths.Values) + @($npcFunXmlPaths.Values) +
    @($systemBarLuaPath, $enhancerLuaPath)

foreach ($path in @($allPaths) + @($forgeSourcePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client file is missing: $path"
    }
}
Assert-ForgeSource $forgeSourcePath

$documents = @{}
$desired = @{}
$desiredApply = $Mode -ne 'Revert'
foreach ($path in $allPaths) {
    $documents[$path] = Read-Utf8File $path
    if ($systemBarXmlPaths.Values -contains $path) {
        $desired[$path] = Set-SystemBarXml (
            $documents[$path].Text) $desiredApply $path
    }
    elseif ($npcFunXmlPaths.Values -contains $path) {
        $desired[$path] = Set-NpcFunXml (
            $documents[$path].Text) $desiredApply $path
    }
    elseif ($path -eq $systemBarLuaPath) {
        $desired[$path] = Set-SystemBarLua (
            $documents[$path].Text) $desiredApply $path
    }
    else {
        $desired[$path] = Set-EnhancerLua (
            $documents[$path].Text) $desiredApply $path
    }

    if (($systemBarXmlPaths.Values -contains $path) -or
        ($npcFunXmlPaths.Values -contains $path)) {
        $xmlCheck = [Xml.XmlDocument]::new()
        $xmlCheck.LoadXml($desired[$path])
    }
}

if ($desiredApply) {
    foreach ($path in $npcFunXmlPaths.Values) {
        Assert-PatchedNpcFun $desired[$path] $path
    }
}

$changeCount = @($allPaths | Where-Object {
    $desired[$_] -cne $documents[$_].Text
}).Count

if ($Mode -eq 'Verify') {
    $hasCurrentMarker = @($allPaths | Where-Object {
        $documents[$_].Text.Contains($marker)
    }).Count -gt 0
    $hasLegacyMarker = @($allPaths | Where-Object {
        $documents[$_].Text.Contains($legacyMarker)
    }).Count -gt 0
    [pscustomobject]@{
        Mode = $Mode
        ClientRoot = $ClientRoot
        State = if ($changeCount -eq 0) { 'Patched' }
            elseif ($hasLegacyMarker) { 'UpgradeRequired' }
            elseif (-not $hasCurrentMarker) { 'Original' }
            else { 'Mixed' }
        OriginExeChanged = $false
        Window = 'Exact 350x582 EquipForgeExUI shell on native FirstWin'
        NativeTabs = '800001..800003 (Add, Enhance, Delete)'
        NativeSlots = '800031 Gear, 800033 Attribute Stone, 800032 Catalyst'
        FilesNeedingChange = @($allPaths | Where-Object {
            $desired[$_] -cne $documents[$_].Text
        })
    }
    return
}

if ($changeCount -eq 0) {
    [pscustomobject]@{
        Mode = $Mode
        State = 'AlreadyDesired'
        ClientRoot = $ClientRoot
    }
    return
}

if (Get-Process -Name 'Origin' -ErrorAction SilentlyContinue) {
    throw 'Close Origin.exe before applying or reverting the Gear Enhancement Forge clone.'
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\backups'))
}
$clientRootFull = [IO.Path]::GetFullPath($ClientRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if ($clientRootFull -eq [IO.Path]::GetPathRoot($clientRootFull)) {
    throw 'ClientRoot cannot be a filesystem root.'
}
$clientRootPrefix = $clientRootFull + [IO.Path]::DirectorySeparatorChar
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$backupDirectory = Join-Path $BackupRoot (
    "client-standalone-gear-enhancement-$Mode-$timestamp")
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

foreach ($path in $allPaths) {
    $pathFull = [IO.Path]::GetFullPath($path)
    if (-not $pathFull.StartsWith(
            $clientRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to back up a path outside ClientRoot: $pathFull"
    }
    $relativePath = $pathFull.Substring($clientRootPrefix.Length)
    $backupPath = Join-Path $backupDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force |
        Out-Null
    Copy-Item -LiteralPath $path -Destination $backupPath
}

foreach ($path in $allPaths) {
    Write-AtomicBytes $path (Convert-ToUtf8Bytes (
        $desired[$path]) $documents[$path].HasBom)
}

foreach ($path in $allPaths) {
    $readback = Read-Utf8File $path
    if ($readback.Text -cne $desired[$path]) {
        throw "Post-write verification failed: $path"
    }
    if (($systemBarXmlPaths.Values -contains $path) -or
        ($npcFunXmlPaths.Values -contains $path)) {
        $xmlCheck = [Xml.XmlDocument]::new()
        $xmlCheck.LoadXml($readback.Text)
    }
}

[pscustomobject]@{
    Mode = $Mode
    State = if ($desiredApply) { 'Patched' } else { 'Original' }
    ClientRoot = $ClientRoot
    BackupDirectory = $backupDirectory
    OriginExeChanged = $false
    Window = if ($desiredApply) {
        'Exact 350x582 EquipForgeExUI shell on native FirstWin'
    } else {
        'Shipped T_SimpleWindow FirstWin dialog'
    }
    MenuOrder = if ($desiredApply) {
        'Add, Enhance, Delete'
    } else {
        'Shipped NpcFunEnhancer.lua behavior'
    }
    SlotOrder = if ($desiredApply) {
        'Gear, Attribute Stone, Catalyst'
    } else {
        'Shipped NpcFunEnhancer.lua behavior'
    }
}
