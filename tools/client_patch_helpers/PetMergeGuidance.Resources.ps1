function New-PetMergeGuidanceLuaFunctions {
    param([string]$NewLine)

    $stock = @'
function set_cantUp(eleID)
	local elem = uiapi:GetElement(eleID);
	elem:SetText(str_cantUp);
end
'@ -replace "`n", $NewLine
    $patchedV1 = @'
local mergeSavvyBaseByResult = {
	[861014] = 861013, [861024] = 861023,
	[861034] = 861033, [861044] = 861043,
	[861054] = 861053, [861064] = 861063
};

function set_cantUp(eleID)
	local elem = uiapi:GetElement(eleID);
	local baseID = mergeSavvyBaseByResult[eleID];
	local base = nil;
	if baseID ~= nil then
		base = tonumber(uiapi:GetElement(baseID):GetText());
	end
	if base == nil then
		elem:SetText(str_cantUp);
		return;
	end
	local required = base - 39.90;
	if required < 0 then required = 0; end
	elem:SetText("Can't; safe deputy " .. string.format("%.2f", required) .. "+");
end
'@ -replace "`n", $NewLine
    $patchedV2 = @'
local mergeSavvyBaseByResult = {
	[861014] = 861013, [861024] = 861023,
	[861034] = 861033, [861044] = 861043,
	[861054] = 861053, [861064] = 861063
};

local function read_mergeSavvyBase(baseID)
	local elem = uiapi:GetElement(baseID);
	if elem == nil then return nil; end
	local raw = elem:GetText();
	if raw == nil then return nil; end
	local token = string.match(raw, "(%d+%.?%d*)%s*$");
	if token == nil then return nil; end
	return tonumber(token);
end

function set_cantUp(eleID)
	local elem = uiapi:GetElement(eleID);
	local baseID = mergeSavvyBaseByResult[eleID];
	local base = nil;
	if baseID ~= nil then
		base = read_mergeSavvyBase(baseID);
	end
	if base == nil then
		elem:SetText("Need stronger deputy");
		return;
	end
	local required = base - 39.90;
	if required < 0 then required = 0; end
	elem:SetText("Need deputy " .. string.format("%.2f", required) .. "+");
end
'@ -replace "`n", $NewLine
    $patchedV3 = @'
local mergeSavvyBaseByResult = {
	[861014] = 861013, [861024] = 861023,
	[861034] = 861033, [861044] = 861043,
	[861054] = 861053, [861064] = 861063
};
local mergeResultByStat = {
	[1] = 861014, [2] = 861024, [3] = 861034,
	[4] = 861044, [5] = 861054, [6] = 861064
};

local function read_mergeSavvyBase(baseID)
	local elem = uiapi:GetElement(baseID);
	if elem == nil then return nil; end
	local raw = elem:GetText();
	if raw == nil then return nil; end
	local token = string.match(raw, "(%d+%.?%d*)%s*$");
	if token == nil then return nil; end
	return tonumber(token);
end

function set_cantUp(eleID, remainingHundredths)
	local resultID = tonumber(eleID);
	local remaining = tonumber(remainingHundredths);
	if resultID ~= nil and resultID < 0 and remaining == nil then
		local encoded = -resultID;
		local stat = encoded % 10;
		resultID = mergeResultByStat[stat];
		remaining = math.floor(encoded / 10);
	end
	local elem = nil;
	if resultID ~= nil then
		elem = uiapi:GetElement(resultID);
	end
	if elem == nil then return; end
	if remaining ~= nil and remaining >= 0 then
		elem:SetText("Need " .. string.format("%.2f", remaining / 100) .. " more");
		return;
	end
	local baseID = mergeSavvyBaseByResult[resultID];
	local base = nil;
	if baseID ~= nil then
		base = read_mergeSavvyBase(baseID);
	end
	if base == nil then
		elem:SetText("Need stronger deputy");
		return;
	end
	local required = base - 39.90;
	if required < 0 then required = 0; end
	elem:SetText("Need deputy " .. string.format("%.2f", required) .. "+");
end
'@ -replace "`n", $NewLine

    $patched = @'
local mergeSavvyBaseByResult = {
	[861014] = 861013, [861024] = 861023,
	[861034] = 861033, [861044] = 861043,
	[861054] = 861053, [861064] = 861063
};
local mergeResultByStat = {
	[1] = 861014, [2] = 861024, [3] = 861034,
	[4] = 861044, [5] = 861054, [6] = 861064
};

local function read_mergeSavvyBase(baseID)
	local elem = uiapi:GetElement(baseID);
	if elem == nil then return nil; end
	local raw = elem:GetText();
	if raw == nil then return nil; end
	local token = string.match(raw, "(%d+%.?%d*)%s*$");
	if token == nil then return nil; end
	return tonumber(token);
end

function set_cantUp(eleID, remainingHundredths)
	local resultID = tonumber(eleID);
	local remaining = tonumber(remainingHundredths);
	local encoded = nil;
	if remaining == nil and resultID ~= nil then
		if resultID < 0 then
			encoded = -resultID;
		elseif resultID > 2147483647 then
			encoded = 4294967296 - resultID;
		end
	end
	if encoded ~= nil then
		local stat = encoded % 10;
		resultID = mergeResultByStat[stat];
		remaining = math.floor(encoded / 10);
	end
	local elem = nil;
	if resultID ~= nil then
		elem = uiapi:GetElement(resultID);
	end
	if elem == nil then return; end
	if remaining ~= nil and remaining >= 0 then
		elem:SetText("Need " .. string.format("%.2f", remaining / 100) .. " more");
		return;
	end
	local baseID = mergeSavvyBaseByResult[resultID];
	local base = nil;
	if baseID ~= nil then
		base = read_mergeSavvyBase(baseID);
	end
	if base == nil then
		elem:SetText("Need stronger deputy");
		return;
	end
	local required = base - 39.90;
	if required < 0 then required = 0; end
	elem:SetText("Need deputy " .. string.format("%.2f", required) .. "+");
end
'@ -replace "`n", $NewLine

    return [pscustomobject]@{
        Stock = $stock
        PatchedV1 = $patchedV1
        PatchedV2 = $patchedV2
        PatchedV3 = $patchedV3
        Patched = $patched
    }
}
