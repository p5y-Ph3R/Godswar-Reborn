function Get-ForgeIds([string]$EquipForgeText) {
    [xml]$document = $EquipForgeText
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $document.SelectNodes('//*[@ID]')) {
        if (-not $ids.Add($node.ID)) { throw "Duplicate EquipForge ID $($node.ID)." }
    }
    return ,$ids
}

function Patch-ItemBaseText(
    [string]$Text,
    [Collections.Generic.HashSet[string]]$ForgeIds,
    [string]$Locale
) {
    $state = @{ Rows = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $id = Get-AttributeValue $element 'ID'
        if (-not $ForgeIds.Contains($id)) { return $element }

        $state.Rows++
        $appFraction = Get-AttributeValue $element 'AppFraction'
        if ($null -eq $appFraction) { throw "Forgeable item $id lacks AppFraction for $Locale." }
        $updated = Set-AttributeValue $element 'AppFraction' (Extend-NumericValues $appFraction 18)
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    if ($state.Rows -ne $ForgeIds.Count) {
        throw "ItemBaseAttribute $Locale matched $($state.Rows) of $($ForgeIds.Count) forge IDs."
    }

    $patched = Upsert-XmlElementAfterId $patched '4215' '4214' '<MaterialBase6 ID="4215" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="36,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />' 'Sapphire 5 item'
    $patched = Upsert-XmlElementAfterId $patched '4225' '4224' '<MaterialAppend6 ID="4225" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="72,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />' 'Emerald 5 item'
    $patched = Upsert-XmlElementAfterId $patched '4234' '4233' '<MaterialOdds5 ID="4234" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="0,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />' 'Crystal 5 item'

    [xml]$document = $patched
    $matched = @($document.SelectNodes('//*[@ID]') | Where-Object { $ForgeIds.Contains($_.ID) })
    if ($matched.Count -ne $ForgeIds.Count) { throw "ItemBaseAttribute validation count mismatch for $Locale." }
    foreach ($node in $matched) {
        if (@(Split-Values $node.AppFraction).Count -lt 18) {
            throw "AppFraction remains shorter than G18 for $Locale item $($node.ID)."
        }
    }
    foreach ($id in @('4215', '4225', '4234')) {
        if (@($document.SelectNodes("//*[@ID='$id']")).Count -ne 1) { throw "Item ID $id validation failed for $Locale." }
    }

    return [pscustomobject]@{ Text = $patched; Rows = $state.Rows; ChangedRows = $state.Changed }
}

function Set-LocalizedLine(
    [string]$Text,
    [string]$Key,
    [string]$Value,
    [string]$AnchorKey
) {
    $line = "$Key`t$Value"
    $existingPattern = "(?m)^$([regex]::Escape($Key))\t[^\r\n]*(?=\r?$)"
    $existing = [regex]::Matches($Text, $existingPattern)
    if ($existing.Count -gt 1) { throw "Duplicate localization key '$Key'." }
    if ($existing.Count -eq 1) {
        $match = $existing[0]
        return $Text.Substring(0, $match.Index) + $line + $Text.Substring($match.Index + $match.Length)
    }

    $anchorPattern = "(?m)^$([regex]::Escape($AnchorKey))\t[^\r\n]*(?=\r?$)"
    $anchors = [regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) { throw "Expected one localization anchor '$AnchorKey' for '$Key'; found $($anchors.Count)." }
    $anchor = $anchors[0]
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $insert = $anchor.Value + $newline + $line
    return $Text.Substring(0, $anchor.Index) + $insert + $Text.Substring($anchor.Index + $anchor.Length)
}

function Patch-LocalizationText([string]$Names, [string]$Descriptions, [string]$Locale) {
    if ($Locale -eq 'en_us') {
        $names = Set-LocalizedLine $Names 'MaterialBase6' 'Level 5 Sapphire' 'MaterialBase5'
        $names = Set-LocalizedLine $names 'MaterialAppend6' 'Level 5 Emerald' 'MaterialAppend5'
        $names = Set-LocalizedLine $names 'MaterialOdds5' 'Level 5 Crystal' 'MaterialOdds4'

        $descriptions = Set-LocalizedLine $Descriptions 'MaterialAppend4' 'Polished emerald with much energy. It can increase equipment''s star level.|cFF39d8b8Can only be used to improve 11-18 star equipment.|cffffffff' 'MaterialAppend4'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialBase6' 'Radiant sapphire with concentrated energy. It raises equipment quality with a greater success bonus.|cFF39d8b8Can only be used on equipment at current quality 8-12.|cffffffff' 'MaterialBase5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialAppend6' 'Radiant emerald with concentrated energy. It raises equipment star level with a greater success bonus.|cFF39d8b8Can only be used on equipment at current grade 10-17.|cffffffff' 'MaterialAppend5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialOdds5' "Brilliant crystal with concentrated energy. Each crystal adds $tier5CrystalProbabilityBonus percentage points to the authoritative forge chance." 'MaterialOdds4'
    }
    else {
        $names = Set-LocalizedLine $Names 'MaterialBase6' (Decode-Utf8 '5LqU57qn6JOd5a6d55+z') 'MaterialBase5'
        $names = Set-LocalizedLine $names 'MaterialAppend6' (Decode-Utf8 '5LqU57qn57u/5a6d55+z') 'MaterialAppend5'
        $names = Set-LocalizedLine $names 'MaterialOdds5' (Decode-Utf8 '5LqU57qn5rC05pm2') 'MaterialOdds4'

        $descriptions = Set-LocalizedLine $Descriptions 'MaterialAppend4' (Decode-Utf8 '57uP6L+H57K+5b+D5omT56Oo55qE5Zub57qn57u/5a6d55+z77yM6IO95aSf5o+Q5Y2H6KOF5aSH55qE5pif57qn44CCfGNGRjM5ZDhiOOWPquiDveaJk+mAoOWNgeS4gOaYn+iHs+WNgeWFq+aYn+eahOijheWkh+OAgnxjZmZmZmZmZmY=') 'MaterialAppend4'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialBase6' (Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn6JOd5a6d55+z77yM6IO95aSf5Lul5pu06auY5oiQ5Yqf546H5o+Q5Y2H6KOF5aSH5ZOB6LSo44CCfGNGRjM5ZDhiOOWPr+eUqOS6juW9k+WJjeWTgei0qOWFq+iHs+WNgeS6jOe6p+eahOijheWkh+OAgnxjZmZmZmZmZmY=') 'MaterialBase5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialAppend6' (Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn57u/5a6d55+z77yM6IO95aSf5Lul5pu06auY5oiQ5Yqf546H5o+Q5Y2H6KOF5aSH5pif57qn44CCfGNGRjM5ZDhiOOWPr+eUqOS6juW9k+WJjeWNgeaYn+iHs+WNgeS4g+aYn+eahOijheWkh+OAgnxjZmZmZmZmZmY=') 'MaterialAppend5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialOdds5' (Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn5rC05pm277yM5q+P6aKX5Y+v5L2/5pyN5Yqh5Zmo5Yik5a6a55qE5omT6YCg5oiQ5Yqf546H5o+Q6auYMTjkuKrnmb7liIbngrnjgII=') 'MaterialOdds4'
    }

    return [pscustomobject]@{ Names = $names; Descriptions = $descriptions }
}
