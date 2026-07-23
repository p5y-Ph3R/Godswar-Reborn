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
