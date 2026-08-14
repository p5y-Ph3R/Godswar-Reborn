$helperAnchorLines = @(
    'local win = UIAPI:GetElement("FirstWin");'
)
$comparisonHelperLines = @(
    'local win = UIAPI:GetElement("FirstWin");',
    'local PhoenixGrowthNew = {};',
    'local PhoenixGrowthCurrent = {};',
    '',
    'local function PhoenixGrowthLabel(Index)',
    "`tif Index == 1 then return NG_L0_P105; end;",
    "`tif Index == 2 then return NG_L0_P106; end;",
    "`tif Index == 3 then return NG_L0_P107; end;",
    "`tif Index == 4 then return NG_L0_P108; end;",
    "`tif Index == 5 then return NG_L0_P109; end;",
    "`treturn NG_L0_P110;",
    'end',
    '',
    'local function PhoenixGrowthEnglish()',
    "`treturn NG_L0_P105 == `"Agility (Growth Rate value): `";",
    'end',
    '',
    'local function PhoenixGrowthClearComparison()',
    "`tPhoenixGrowthNew = {};",
    "`tPhoenixGrowthCurrent = {};",
    "`tFirstWin_Text2:SetText(`"`");",
    "`tFirstWin_Text2:Visible(false);",
    "`tfor Index = 1, 12 do",
    "`t`tlocal Button = win:GetChild(`"FirstWin_Button`" .. Index);",
    "`t`tButton:Visible(false);",
    "`tend;",
    'end',
    '',
    'local function PhoenixGrowthShowNew(BtnID,SubID,Suffix)',
    "`tlocal Index = Suffix - 7;",
    "`tlocal Value = (SubID - Suffix)/10000;",
    "`tPhoenixGrowthNew[Index] = Value;",
    "`tlocal Button = win:GetChild(`"FirstWin_Button`" .. BtnID);",
    "`tButton:SetPosition(25,60 + Index * 20);",
    "`tButton:SetText(PhoenixGrowthLabel(Index) .. `"|cFFF79709`" .. string.format(`"%.2f`", Value) .. `"|cFFFFFFFF`");",
    "`tButton:Visible(true);",
    "`tButton:Enable(false);",
    "`tUIAPI:SetChecked(false,Button);",
    'end',
    '',
    'local function PhoenixGrowthShowCurrent(BtnID,SubID,Suffix)',
    "`tlocal Index = Suffix - 19;",
    "`tlocal Value = (SubID - Suffix)/10000;",
    "`tPhoenixGrowthCurrent[Index] = Value;",
    "`tlocal CurrentLabel = `"Current: `";",
    "`tif not PhoenixGrowthEnglish() then CurrentLabel = string.char(229,189,147,229,137,141,239,188,154); end;",
    "`tlocal Button = win:GetChild(`"FirstWin_Button`" .. BtnID);",
    "`tButton:SetPosition(340,60 + Index * 20);",
    "`tButton:SetText(`"|cff39D8B8`" .. CurrentLabel .. string.format(`"%.2f`", Value) .. `"|cFFFFFFFF`");",
    "`tButton:Visible(true);",
    "`tButton:Enable(false);",
    "`tUIAPI:SetChecked(false,Button);",
    "`tif Index ~= 6 then return; end;",
    "`tlocal NewTotal = 0;",
    "`tlocal CurrentTotal = 0;",
    "`tfor Stat = 1, 6 do",
    "`t`tif PhoenixGrowthNew[Stat] == nil or PhoenixGrowthCurrent[Stat] == nil then return; end;",
    "`t`tNewTotal = NewTotal + PhoenixGrowthNew[Stat];",
    "`t`tCurrentTotal = CurrentTotal + PhoenixGrowthCurrent[Stat];",
    "`tend;",
    "`tlocal NewTotalLabel = `"New total: `";",
    "`tlocal CurrentTotalLabel = `"Current total: `";",
    "`tif not PhoenixGrowthEnglish() then",
    "`t`tNewTotalLabel = string.char(230,150,176,229,128,188,230,128,187,232,174,161,239,188,154);",
    "`t`tCurrentTotalLabel = string.char(229,189,147,229,137,141,230,128,187,232,174,161,239,188,154);",
    "`tend;",
    "`tFirstWin_Text2:SetPosition(25,205);",
    "`tFirstWin_Text2:SetText(NewTotalLabel .. `"|cFFF79709`" .. string.format(`"%.2f`", NewTotal) .. `"|cFFFFFFFF    `" .. CurrentTotalLabel .. `"|cff39D8B8`" .. string.format(`"%.2f`", CurrentTotal) .. `"|cFFFFFFFF`");",
    "`tFirstWin_Text2:Visible(true);",
    'end'
)
$comparisonOrdinalHelperLines = @($comparisonHelperLines)
# The native callback ordinal also counts the page-heading record. Address
# comparison rows by their suffix-derived stat slot so the twelfth value
# (current Luck) uses FirstWin_Button12 instead of the missing Button13.
$comparisonHelperLines[32] =
    "`tlocal Button = win:GetChild(`"FirstWin_Button`" .. Index);"
$comparisonHelperLines[46] =
    "`tlocal Button = win:GetChild(`"FirstWin_Button`" .. (Index + 6));"

$basicOriginalBlockLines = @(
    "`t`tif SubID == 120 then",
    "`t`t`tFirstWin_Text1:SetText(NF_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NF_L0_P124);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)
$basicPreviousPatchedBlockLines = @(
    "`t`tif SubID == 120 then",
    "`t`t`tFirstWin_Text1:SetText(NF_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);",
    "`t`t`tFirstWin_ButtonA1:SetText(NF_X0_2);",
    "`t`t`tFirstWin_ButtonA1:SetPosition(250,100);",
    "`t`t`tFirstWin_ButtonA1:Visible(true);",
    "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_3);",
    "`t`t`tFirstWin_ButtonA2:SetPosition(325,100);",
    "`t`t`tFirstWin_ButtonA2:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NF_L0_P124);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)
$basicPatchedBlockLines = @(
    "`t`tif SubID == 120 then",
    "`t`t`tFirstWin_Text1:SetText(NF_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);",
    "`t`t`tFirstWin_ButtonA1:Visible(false);",
    "`t`t`tFirstWin_ButtonA2:Visible(false);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NF_L0_P124);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)

$originalBlockLines = @(
    "`t`telseif SubID == 130 then",
    "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);`t",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)
$legacyPatchedBlockLines = @(
    "`t`telseif SubID == 130 then",
    "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);`t",
    "`t`t`tFirstWin_ButtonA1:Visible(false);",
    "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_2);",
    "`t`t`tFirstWin_ButtonA2:SetPosition(325,100);",
    "`t`t`tFirstWin_ButtonA2:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)
$topRowPatchedBlockLines = @(
    "`t`telseif SubID == 130 then",
    "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);`t",
    "`t`t`tFirstWin_ButtonA1:SetText(NF_X0_2);",
    "`t`t`tFirstWin_ButtonA1:SetPosition(250,100);",
    "`t`t`tFirstWin_ButtonA1:Visible(true);",
    "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_3);",
    "`t`t`tFirstWin_ButtonA2:SetPosition(325,100);",
    "`t`t`tFirstWin_ButtonA2:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)
$bottomPatchedBlockLines = @(
    "`t`telseif SubID == 130 then",
    "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);`t",
    "`t`t`tFirstWin_ButtonA1:SetText(NF_X0_2);",
    "`t`t`tFirstWin_ButtonA1:SetPosition(250,240);",
    "`t`t`tFirstWin_ButtonA1:Visible(true);",
    "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_3);",
    "`t`t`tFirstWin_ButtonA2:SetPosition(325,240);",
    "`t`t`tFirstWin_ButtonA2:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
    "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
)
$patchedBlockLines = @(
    "`t`telseif SubID == 130 then",
    "`t`t`tPhoenixGrowthClearComparison();",
    "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
    "`t`t`tFirstWin_Text1:Visible(true);`t",
    "`t`t`tFirstWin_ButtonA1:SetText(NF_X0_2);",
    "`t`t`tFirstWin_ButtonA1:SetPosition($okX,$okY);",
    "`t`t`tFirstWin_ButtonA1:Visible(true);",
    "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_3);",
    "`t`t`tFirstWin_ButtonA2:SetPosition($cancelX,$cancelY);",
    "`t`t`tFirstWin_ButtonA2:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:Visible(true);",
    "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
    "`t`t`tFirstWin_ButtonA3:SetPosition($phoenixResetX,$phoenixResetY);",
    "`t`telseif math.mod(SubID ,100) >= 8 and math.mod(SubID ,100) <= 13 then",
    "`t`t`tPhoenixGrowthShowNew(BtnID,SubID,math.mod(SubID ,100));",
    "`t`telseif math.mod(SubID ,100) >= 20 and math.mod(SubID ,100) <= 25 then",
    "`t`t`tPhoenixGrowthShowCurrent(BtnID,SubID,math.mod(SubID ,100));"
)
