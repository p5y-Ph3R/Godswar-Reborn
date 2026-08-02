local win = UIAPI:GetElement("FirstWin");
--ʥ����
function NpcFunSDAN_SetUI(Type,Index)
	FirstWin_Text1:SetPosition(45,100);
	FirstWin_Text1:Visible(false);
	FirstWin_ButtonA1:Visible(true);
	FirstWin_ButtonA2:Visible(true);
	strDesig = ""
	win:Visible(true);

end

function NpcFunSDAN_SetText(Type,Index,BtnID,SubID)
        if SubID == 117 then
           FirstWin_Text1:SetText(Msg_Bug01);
           FirstWin_Text1:Visible(true);
           NPCFUN:EndMessage(true);
	elseif Index== 1 then    ----��1��ҳ��------
	    if SubID == 100 then
			FirstWin_Text1:SetText(NF_L0_SD100);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
	    elseif SubID == 1 then
			FirstWin_Text1:SetText(NF_L0_SD1000);
			FirstWin_Text1:Visible(true);
			local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD1);
			Button:SetPosition(45,130);
			Button:Visible(true);
	    elseif SubID == 2 then
			local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD2);
			Button:SetPosition(45,160);
			Button:Visible(true);

		end;
	elseif Index== 2 then
	    if SubID == 101 then
			FirstWin_Text1:SetText(NF_L0_SD101);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 102 then
		    	FirstWin_Text1:SetText(NF_L0_SD102);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 103 then
		    	FirstWin_Text1:SetText(NF_L0_SD103);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 3 then
		    	FirstWin_Text1:SetText(NF_L0_SD1001);
			FirstWin_Text1:Visible(true);
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD3);
			Button:SetPosition(45,130);
			Button:Visible(true);
		elseif SubID == 4 then	
		    	FirstWin_Text1:SetText(NF_L0_SD1002);
			FirstWin_Text1:Visible(true);
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
		elseif SubID == 201 then
	      print(BtnID,SubID);
	      local Button = win:GetChild("FirstWin_Button" .. BtnID);
	      Button:SetText(NF_L0_SD6);
	      Button:Visible(true);
	      Button:SetPosition(25,100);
		elseif SubID == 202 then
	      print(BtnID,SubID);
	      local Button = win:GetChild("FirstWin_Button" .. BtnID);
	      Button:SetText(NF_L0_SD7);
	      Button:Visible(true);
	      Button:SetPosition(25,120);
		elseif SubID == 203 then
	      print(BtnID,SubID);
	      local Button = win:GetChild("FirstWin_Button" .. BtnID);
	      Button:SetText(NF_L0_SD8);
	      Button:Visible(true);
	      Button:SetPosition(25,140);
		elseif SubID == 204 then
	      print(BtnID,SubID);
	      local Button = win:GetChild("FirstWin_Button" .. BtnID);
	      Button:SetText(NF_L0_SD9);
	      Button:Visible(true);
	      Button:SetPosition(25,160);
		elseif SubID == 205 then
	      print(BtnID,SubID);
	      local Button = win:GetChild("FirstWin_Button" .. BtnID);
	      Button:SetText(NF_L0_SD10);
	      Button:Visible(true);
	      Button:SetPosition(25,180);
		elseif SubID == 206 then
	      print(BtnID,SubID);
	      local Button = win:GetChild("FirstWin_Button" .. BtnID);
	      Button:SetText(NF_L0_SD11);
	      Button:Visible(true);
	      Button:SetPosition(25,200);
		end;
	elseif Index== 3 then
	    if SubID == 104 then
			FirstWin_Text1:SetText(NF_L0_SD104);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
	    elseif math.mod(SubID,1000) == 105 then
			FirstWin_Text1:SetText(NF_L0_SD1050.."|cFFF79709"..((SubID - 105)/1000).."|cFFFFFFFF"..NF_L0_SD1051.."|cFFF79709"..(100 - (SubID - 105)/1000).."|cFFFFFFFF"..NF_L0_SD1052);
			FirstWin_Text1:SetPosition(40,25);
			FirstWin_Text1:Visible(true);
			FirstWin_Text2:SetText(NF_L0_SD1053);
            		FirstWin_Text2:SetPosition(40,50);
			FirstWin_Text2:Visible(true);
			InputText1:Visible(true);
			Input11:Visible(true); --����Ϊ�ɼ�
			Input12:Visible(true);
			Input13:Visible(true);
			Input11:SetPosition(98,120);
			Input12:SetPosition(90,120);
			Input13:SetPosition(192,120);
			InputText1:SetPosition(94,121);
		elseif SubID == 106 then
		    	FirstWin_Text1:SetText(NF_L0_SD106);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 107 then
		    	FirstWin_Text1:SetText(NF_L0_SD107);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif math.mod(SubID ,1000) == 108 then
                        local luatext = {5000, 10000, 20000, 30000, 50000}
			FirstWin_Text1:SetText(NF_L0_SD1080.."|cFFF79709"..((SubID - 108)/1000).."|cFFFFFFFF"..NF_L0_SD1081);
			FirstWin_Text1:Visible(true);
			FirstWin_Text2:SetText(NF_L0_SD1082.."|cFFF79709".. luatext[((SubID - 108)/1000) + 1] .."|cFFFFFFFF");
                        FirstWin_Text2:SetPosition(50,140)
			FirstWin_Text2:Visible(true);
			NPCFUN:EndMessage(true);
		elseif math.mod(SubID ,1000) == 116 then
			FirstWin_Text3:SetText(NF_L0_SD1083.."|cFFF79709"..((SubID - 116)/1000).."|cFFFFFFFF");
                        FirstWin_Text3:SetPosition(50,180)
			FirstWin_Text3:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 109 then
			FirstWin_Text1:SetText(NF_L0_SD109);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 6 then
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD6);
			Button:SetPosition(20,70)
			Button:Visible(true);
		elseif SubID == 7 then
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD7);
			Button:SetPosition(20,100)
			Button:Visible(true);
		elseif SubID == 8 then
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD8);
			Button:SetPosition(20,130)
			Button:Visible(true);
		elseif SubID == 9 then
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD9);
			Button:SetPosition(20,160)
			Button:Visible(true);
		elseif SubID == 10 then
		    	local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_SD10);
			Button:SetPosition(20,190)
			Button:Visible(true);
		end;
	elseif Index== 4 then
	    if SubID == 110 then
			FirstWin_Text1:SetText(NF_L0_SD110);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 111 then
			FirstWin_Text1:SetText(NF_L0_SD111);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 1100 then
			FirstWin_Text1:SetText(NF_L0_SD1100);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 112 then
			FirstWin_Text1:SetText(NF_L0_SD112);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 113 then
			FirstWin_Text1:SetText(NF_L0_SD113);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif SubID == 114 then
			FirstWin_Text1:SetText(NF_L0_SD114);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		elseif math.mod(SubID,1000) == 115 then
			FirstWin_Text1:SetText("|cFFF79709"..((SubID - 115)/1000).."|cFFFFFFFF"..NF_L0_SD115);
			FirstWin_Text1:Visible(true);
			NPCFUN:EndMessage(true);
		end;
	end;
end;
