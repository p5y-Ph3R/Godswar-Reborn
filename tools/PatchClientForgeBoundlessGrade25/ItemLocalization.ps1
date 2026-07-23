function Get-ForgeIds([string]$EquipForgeText) {
    [xml]$document = $EquipForgeText
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $document.SelectNodes('//*[@ID]')) {
        if (-not $ids.Add($node.ID)) {
            throw "Duplicate EquipForge ID $($node.ID)."
        }
    }
    return ,$ids
}

function Patch-ItemBaseText(
    [string]$Text,
    [Collections.Generic.HashSet[string]]$ForgeIds,
    [string]$Locale
) {
    $state = @{ Rows = 0; Changed = 0 }
    $semanticArrays = @{}
    [xml]$sourceDocument = $Text
    foreach ($node in $sourceDocument.SelectNodes('//*[@ID]')) {
        if ($ForgeIds.Contains($node.ID)) {
            $semanticArrays[$node.ID] = @(
                $node.GetAttribute('MainAttribute'),
                $node.GetAttribute('ArmEffFraction'),
                $node.GetAttribute('ArmEff'),
                $node.GetAttribute('DefendFraction'),
                $node.GetAttribute('DefendEff')
            )
        }
    }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $id = Get-AttributeValue $element 'ID'
        if (-not $ForgeIds.Contains($id)) { return $element }
        $state.Rows++
        $updated = $element
        foreach ($name in $numericQualityAttributes) {
            $value = Get-AttributeValue $updated $name
            if ($null -ne $value) {
                $updated = Set-AttributeValue $updated $name (
                    Extend-NumericValues $value 20
                )
            }
        }
        foreach ($entry in @(
            @{ Name='BaseFraction'; Count=20 },
            @{ Name='AppFraction'; Count=25 }
        )) {
            $value = Get-AttributeValue $updated $entry.Name
            if ($null -eq $value) {
                throw "Forgeable item $id lacks $($entry.Name) for $Locale."
            }
            $updated = Set-AttributeValue $updated $entry.Name (
                Extend-NumericValues $value $entry.Count
            )
        }
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    if ($state.Rows -ne $ForgeIds.Count) {
        throw "ItemBaseAttribute $Locale matched $($state.Rows) of $($ForgeIds.Count) forge IDs."
    }
    $patched = Upsert-XmlElementAfterId $patched '4215' '4214' (
        '<MaterialBase6 ID="4215" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="36,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />'
    ) 'Sapphire 5 item'
    $patched = Upsert-XmlElementAfterId $patched '4225' '4224' (
        '<MaterialAppend6 ID="4225" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="72,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />'
    ) 'Emerald 5 item'
    $patched = Upsert-XmlElementAfterId $patched '4234' '4233' (
        '<MaterialOdds5 ID="4234" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="0,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />'
    ) 'Crystal 5 item'
    [xml]$document = $patched
    $matched = @(
        $document.SelectNodes('//*[@ID]') |
            Where-Object { $ForgeIds.Contains($_.ID) }
    )
    if ($matched.Count -ne $ForgeIds.Count) {
        throw "ItemBaseAttribute validation count mismatch for $Locale."
    }
    foreach ($node in $matched) {
        foreach ($name in $numericQualityAttributes) {
            if ($node.HasAttribute($name) -and
                @(Split-Values $node.GetAttribute($name)).Count -lt 20) {
                throw "$name remains shorter than Q20 for $Locale item $($node.ID)."
            }
        }
        if (@(Split-Values $node.BaseFraction).Count -lt 20) {
            throw "BaseFraction remains shorter than Q20 for $Locale item $($node.ID)."
        }
        if (@(Split-Values $node.AppFraction).Count -lt 25) {
            throw "AppFraction remains shorter than G25 for $Locale item $($node.ID)."
        }
        $before = $semanticArrays[$node.ID]
        $names = @(
            'MainAttribute', 'ArmEffFraction', 'ArmEff',
            'DefendFraction', 'DefendEff'
        )
        for ($index = 0; $index -lt $names.Count; $index++) {
            if ($node.GetAttribute($names[$index]) -cne $before[$index]) {
                throw "Semantic array $($names[$index]) changed for $Locale item $($node.ID)."
            }
        }
    }
    foreach ($id in @('4215', '4225', '4234')) {
        if (@($document.SelectNodes("//*[@ID='$id']")).Count -ne 1) {
            throw "Item ID $id validation failed for $Locale."
        }
    }
    return [pscustomobject]@{
        Text = $patched
        Rows = $state.Rows
        ChangedRows = $state.Changed
    }
}

function Decode-Utf8([string]$Base64) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Set-LocalizedLine(
    [string]$Text,
    [string]$Key,
    [string]$Value,
    [string]$AnchorKey
) {
    $line = $Key + [char]9 + $Value
    $existing = [regex]::Matches(
        $Text,
        ('(?m)^{0}\t[^\r\n]*(?=\r?$)' -f [regex]::Escape($Key))
    )
    if ($existing.Count -gt 1) {
        throw "Duplicate localization key '$Key'."
    }
    if ($existing.Count -eq 1) {
        $match = $existing[0]
        return $Text.Substring(0, $match.Index) + $line +
            $Text.Substring($match.Index + $match.Length)
    }
    $anchors = [regex]::Matches(
        $Text,
        ('(?m)^{0}\t[^\r\n]*(?=\r?$)' -f [regex]::Escape($AnchorKey))
    )
    if ($anchors.Count -ne 1) {
        throw "Expected one localization anchor '$AnchorKey' for '$Key'."
    }
    $anchor = $anchors[0]
    $newline = if ($Text.Contains([string][char]13 + [char]10)) {
        [string][char]13 + [char]10
    }
    else {
        [string][char]10
    }
    $insert = $anchor.Value + $newline + $line
    return $Text.Substring(0, $anchor.Index) + $insert +
        $Text.Substring($anchor.Index + $anchor.Length)
}

function Patch-DescriptionText([string]$Text, [string]$Locale) {
    if ($Locale -eq 'en_us') {
        $Text = Set-LocalizedLine $Text 'MaterialBase6' (
            'Radiant sapphire with concentrated energy. It raises equipment quality with a greater success bonus.|cFF39d8b8Can only be used on equipment at current quality 8-19.|cffffffff'
        ) 'MaterialBase5'
        $Text = Set-LocalizedLine $Text 'MaterialAppend6' (
            'Radiant emerald with concentrated energy. It raises equipment star level with a greater success bonus.|cFF39d8b8Can only be used on equipment at current grade 10-24.|cffffffff'
        ) 'MaterialAppend5'
        $Text = Set-LocalizedLine $Text 'MaterialOdds5' (
            'Brilliant crystal with concentrated energy. Each crystal adds 25 percentage points to the authoritative forge chance.'
        ) 'MaterialOdds4'
    }
    else {
        $Text = Set-LocalizedLine $Text 'MaterialBase6' (
            Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn6JOd5a6d55+z77yM6IO95aSf5Lul5pu06auY5oiQ5Yqf546H5o+Q5Y2H6KOF5aSH5ZOB6LSo44CCfGNGRjM5ZDhiOOWPr+eUqOS6juW9k+WJjeWTgei0qOWFq+iHs+WNgeS5nee6p+eahOijheWkh+OAgnxjZmZmZmZmZmY='
        ) 'MaterialBase5'
        $Text = Set-LocalizedLine $Text 'MaterialAppend6' (
            Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn57u/5a6d55+z77yM6IO95aSf5Lul5pu06auY5oiQ5Yqf546H5o+Q5Y2H6KOF5aSH5pif57qn44CCfGNGRjM5ZDhiOOWPr+eUqOS6juW9k+WJjeWNgeaYn+iHs+S6jOWNgeWbm+aYn+eahOijheWkh+OAgnxjZmZmZmZmZmY='
        ) 'MaterialAppend5'
        $Text = Set-LocalizedLine $Text 'MaterialOdds5' (
            Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn5rC05pm277yM5q+P6aKX5Y+v5L2/5pyN5Yqh5Zmo5Yik5a6a55qE5omT6YCg5oiQ5Yqf546H5o+Q6auYMjXkuKrnmb7liIbngrnjgII='
        ) 'MaterialOdds4'
    }
    return $Text
}
