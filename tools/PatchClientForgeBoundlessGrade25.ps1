param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8Bom = [Text.UTF8Encoding]::new($true)
$utf16LeBom = [Text.UnicodeEncoding]::new($false, $true)
$gb2312 = [Text.Encoding]::GetEncoding(936)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$expectedForgeRows = @{ en_us = 611; zh_cn = 550 }
$maximumQuality = 20
$maximumGrade = 25
$tier5PrimaryBonus = 32
$tier5CrystalBonus = 25

$qualityProbability = @(
    50, 30, -5, -15, -45, -75, -105, -165, -215, -225,
    -235, -245, -255, -265, -275, -285, -295, -305, -315, 0
)
$gradeProbability = @(
    60, 35, 5, -10, -25, -50, -65, -85, -115, -175, -220,
    -245, -270, -295, -320, -345, -370, -395, -420, -445,
    -470, -495, -520, -545, 0
)
$qualityCostMultipliers = @(20, 25, 30, 35, 40, 45, 50, 55, 60, 65)
$gradeCostMultipliers = @(25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85)
$numericQualityAttributes = @(
    'Attack', 'AttackRadius', 'AttackSpeed', 'MaxHP', 'MaxMP', 'Defence',
    'MagicAk', 'MagicRec', 'Hit', 'Miss', 'State', 'StateImmunity',
    'AcceptCure', 'Cure', 'PhysicalDamage', 'MagicDamage',
    'PhysicalDamageAbsorb', 'MagicDamageAbsorb',
    'Speed', 'FuryAddAk', 'FuryAddRec', 'InjureImbibe'
)

function Get-AttributeValue([string]$Element, [string]$Name) {
    $match = [regex]::Match(
        $Element,
        ('(?<=\s){0}="([^"]*)"' -f [regex]::Escape($Name))
    )
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Set-AttributeValue([string]$Element, [string]$Name, [string]$Value) {
    $pattern = '(?<=\s){0}="[^"]*"' -f [regex]::Escape($Name)
    $match = [regex]::Match($Element, $pattern)
    if (-not $match.Success) { throw "Required attribute '$Name' is missing." }
    $replacement = $Name + '="' + $Value + '"'
    return $Element.Substring(0, $match.Index) + $replacement +
        $Element.Substring($match.Index + $match.Length)
}

function Split-Values([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
}

function Format-Decimal([decimal]$Value) {
    return $Value.ToString('0.############', $invariant)
}

function Join-Integers([int[]]$Values) {
    return (($Values | ForEach-Object { $_.ToString($invariant) }) -join ',')
}

function Extend-NumericValues([string]$Value, [int]$TargetCount) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -ge $TargetCount) { return $Value }
    if ($parts.Count -lt 2) {
        throw "Cannot extend a numeric vector with $($parts.Count) value(s)."
    }
    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) {
        $numbers.Add([decimal]::Parse($part, $invariant))
    }
    $delta = $numbers[$numbers.Count - 1] - $numbers[$numbers.Count - 2]
    if ($delta -lt 0) {
        throw "Refusing to extrapolate a decreasing numeric vector (delta $delta)."
    }
    while ($numbers.Count -lt $TargetCount) {
        $numbers.Add($numbers[$numbers.Count - 1] + $delta)
    }
    return (($numbers | ForEach-Object { Format-Decimal $_ }) -join ',')
}

function Set-QualityMoneyVector([string]$Value, [string]$ItemId) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -lt 9) {
        throw "Bmoney for item $ItemId has only $($parts.Count) values."
    }
    $unit = [int]::Parse($parts[1], $invariant)
    if ($unit -le 0) { throw "Invalid economy unit for item $ItemId." }
    $result = @($parts | Select-Object -First 9)
    foreach ($multiplier in $qualityCostMultipliers) {
        $result += ([long]$unit * $multiplier).ToString($invariant)
    }
    $result += '0'
    return ($result -join ',')
}

function Set-GradeMoneyVector(
    [string]$Cmoney,
    [string]$Bmoney,
    [string]$ItemId
) {
    $parts = @(Split-Values $Cmoney)
    $qualityMoney = @(Split-Values $Bmoney)
    if ($parts.Count -lt 11 -or $qualityMoney.Count -lt 2) {
        throw "Incomplete grade economy vectors for item $ItemId."
    }
    $unit = [int]::Parse($qualityMoney[1], $invariant)
    if ($unit -le 0) { throw "Invalid economy unit for item $ItemId." }
    $result = @($parts | Select-Object -First 11)
    foreach ($multiplier in $gradeCostMultipliers) {
        $result += ([long]$unit * $multiplier).ToString($invariant)
    }
    $result += '0'
    return ($result -join ',')
}

function Patch-EquipForgeText([string]$Text, [string]$Locale) {
    $state = @{ Rows = 0; Changed = 0 }
    $qualityPrefix = '50,30,-5,-15,-45,-75,-105,-165,-215'
    $gradePrefix = '60,35,5,-10,-25,-50,-65,-85,-115,-175,-220'
    $qualityText = Join-Integers $qualityProbability
    $gradeText = Join-Integers $gradeProbability
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $base = Get-AttributeValue $element 'BaseProyAdd'
        $append = Get-AttributeValue $element 'AppendProyAdd'
        $bmoney = Get-AttributeValue $element 'Bmoney'
        $cmoney = Get-AttributeValue $element 'Cmoney'
        if ($null -eq $base -or $null -eq $append -or
            $null -eq $bmoney -or $null -eq $cmoney) {
            return $element
        }
        $state.Rows++
        $id = Get-AttributeValue $element 'ID'
        if (((@(Split-Values $base) | Select-Object -First 9) -join ',') -ne
            $qualityPrefix) {
            throw "Unexpected BaseProyAdd prefix for $Locale item $id."
        }
        if (((@(Split-Values $append) | Select-Object -First 11) -join ',') -ne
            $gradePrefix) {
            throw "Unexpected AppendProyAdd prefix for $Locale item $id."
        }
        $updated = Set-AttributeValue $element 'BaseProyAdd' $qualityText
        $updated = Set-AttributeValue $updated 'AppendProyAdd' $gradeText
        $updated = Set-AttributeValue $updated 'Bmoney' (
            Set-QualityMoneyVector $bmoney $id
        )
        $updated = Set-AttributeValue $updated 'Cmoney' (
            Set-GradeMoneyVector $cmoney $bmoney $id
        )
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    $expected = $expectedForgeRows[$Locale]
    if ($state.Rows -ne $expected) {
        throw "Expected $expected EquipForge rows for $Locale; found $($state.Rows)."
    }
    [xml]$document = $patched
    foreach ($node in $document.SelectNodes(
            '//*[@ID and @BaseProyAdd and @AppendProyAdd and @Bmoney and @Cmoney]'
        )) {
        $base = @(Split-Values $node.BaseProyAdd)
        $append = @(Split-Values $node.AppendProyAdd)
        $bmoney = @(Split-Values $node.Bmoney)
        $cmoney = @(Split-Values $node.Cmoney)
        if ($base.Count -ne 20 -or ($base -join ',') -ne $qualityText -or
            $base[19] -ne '0') {
            throw "Q20 probability validation failed for $Locale item $($node.ID)."
        }
        if ($append.Count -ne 25 -or ($append -join ',') -ne $gradeText -or
            $append[24] -ne '0') {
            throw "G25 probability validation failed for $Locale item $($node.ID)."
        }
        if ($bmoney.Count -ne 20 -or $bmoney[19] -ne '0' -or
            $cmoney.Count -ne 25 -or $cmoney[24] -ne '0') {
            throw "Forge cost validation failed for $Locale item $($node.ID)."
        }
    }
    return [pscustomobject]@{
        Text = $patched
        Rows = $state.Rows
        ChangedRows = $state.Changed
    }
}

function Upsert-XmlElementAfterId(
    [string]$Text,
    [string]$Id,
    [string]$AnchorId,
    [string]$Element,
    [string]$Label
) {
    $existing = [regex]::Matches(
        $Text,
        ('<[^<>]*\bID="{0}"[^<>]*/>' -f [regex]::Escape($Id))
    )
    if ($existing.Count -gt 1) { throw "Duplicate $Label rows for ID $Id." }
    if ($existing.Count -eq 1) {
        $match = $existing[0]
        return $Text.Substring(0, $match.Index) + $Element +
            $Text.Substring($match.Index + $match.Length)
    }
    $anchors = [regex]::Matches(
        $Text,
        ('<[^<>]*\bID="{0}"[^<>]*/>' -f [regex]::Escape($AnchorId))
    )
    if ($anchors.Count -ne 1) {
        throw "Expected one $Label anchor ID $AnchorId; found $($anchors.Count)."
    }
    $anchor = $anchors[0]
    $after = $anchor.Index + $anchor.Length
    $tail = $Text.Substring($after)
    $separator = ''
    $lf = [char]10
    if ($tail.StartsWith([Environment]::NewLine) -or $tail.StartsWith($lf)) {
        $newline = if ($tail.StartsWith([Environment]::NewLine)) {
            [Environment]::NewLine
        }
        else {
            [string]$lf
        }
        $lineStart = $Text.LastIndexOf($lf, [Math]::Max(0, $anchor.Index - 1)) + 1
        $indent = $Text.Substring($lineStart, $anchor.Index - $lineStart)
        if ($indent -notmatch '^[ \t]*$') { $indent = '' }
        $separator = $newline + $indent
    }
    return $Text.Substring(0, $after) + $separator + $Element + $tail
}

function Patch-BijouForgeText([string]$Text, [string]$Locale) {
    [xml]$before = $Text
    $sapphireFour = @($before.SelectNodes("//*[@ID='4213']"))
    $emeraldFour = @($before.SelectNodes("//*[@ID='4223']"))
    if ($sapphireFour.Count -ne 1 -or $sapphireFour[0].MaterialType -ne '2' -or
        $sapphireFour[0].MaterialProyAdd -ne '24' -or
        $sapphireFour[0].Round -ne '8,12') {
        throw "Level-4 Sapphire prerequisite mismatch for $Locale."
    }
    if ($emeraldFour.Count -ne 1 -or $emeraldFour[0].MaterialType -ne '3' -or
        $emeraldFour[0].MaterialProyAdd -ne '24' -or
        $emeraldFour[0].Round -ne '10,17') {
        throw "Level-4 Emerald prerequisite mismatch for $Locale."
    }
    $sapphireFourXml = $sapphireFour[0].OuterXml
    $emeraldFourXml = $emeraldFour[0].OuterXml
    $patched = Upsert-XmlElementAfterId $Text '4215' '4213' (
        '<MaterialBase6 ID="4215" MaterialType="2" MaterialProyAdd="32" Round="8,19"/>'
    ) 'Sapphire 5'
    $patched = Upsert-XmlElementAfterId $patched '4225' '4223' (
        '<MaterialAppend6 ID="4225" MaterialType="3" MaterialProyAdd="32" Round="10,24"/>'
    ) 'Emerald 5'
    $patched = Upsert-XmlElementAfterId $patched '4234' '4233' (
        '<MaterialOdds5 ID="4234" MaterialType="4" MaterialProyAdd="25"/>'
    ) 'Crystal 5'
    [xml]$document = $patched
    if ($document.SelectSingleNode("//*[@ID='4213']").OuterXml -cne
        $sapphireFourXml -or
        $document.SelectSingleNode("//*[@ID='4223']").OuterXml -cne
        $emeraldFourXml) {
        throw "A Level-4 material row changed for $Locale."
    }
    $expected = @(
        @{ Id='4215'; Type='2'; Bonus='32'; Round='8,19' },
        @{ Id='4225'; Type='3'; Bonus='32'; Round='10,24' },
        @{ Id='4234'; Type='4'; Bonus='25'; Round='' }
    )
    foreach ($entry in $expected) {
        $nodes = @($document.SelectNodes("//*[@ID='$($entry.Id)']"))
        if ($nodes.Count -ne 1 -or $nodes[0].MaterialType -ne $entry.Type -or
            $nodes[0].MaterialProyAdd -ne $entry.Bonus -or
            ($entry.Round -and $nodes[0].Round -ne $entry.Round)) {
            throw "Tier-5 Bijou validation failed for $Locale ID $($entry.Id)."
        }
    }
    return $patched
}

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

function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $rootPath,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

function Assert-BinaryContext([byte[]]$Bytes, [hashtable]$Site) {
    if ($Site.Offset -lt $Site.Prefix.Count -or
        $Site.Offset + $Site.Suffix.Count -ge $Bytes.Count) {
        throw "Origin.exe site '$($Site.Name)' is outside the file."
    }
    for ($index = 0; $index -lt $Site.Prefix.Count; $index++) {
        if ($Bytes[$Site.Offset - $Site.Prefix.Count + $index] -ne
            $Site.Prefix[$index]) {
            throw "Origin.exe prefix mismatch at $($Site.Name)."
        }
    }
    for ($index = 0; $index -lt $Site.Suffix.Count; $index++) {
        if ($Bytes[$Site.Offset + 1 + $index] -ne $Site.Suffix[$index]) {
            throw "Origin.exe suffix mismatch at $($Site.Name)."
        }
    }
    if ($Site.Allowed -notcontains $Bytes[$Site.Offset]) {
        throw "Origin.exe byte mismatch at $($Site.Name): got 0x$(
            '{0:X2}' -f $Bytes[$Site.Offset]
        )."
    }
}

function Assert-ExactBytes(
    [byte[]]$Bytes,
    [int]$Offset,
    [byte[]]$Expected,
    [string]$Name
) {
    if ($Offset -lt 0 -or $Offset + $Expected.Count -gt $Bytes.Count) {
        throw "Origin.exe prerequisite '$Name' is outside the file."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            throw "Origin.exe prerequisite '$Name' mismatch at 0x$(
                '{0:X}' -f ($Offset + $index)
            )."
        }
    }
}

function Assert-ItemAppendAttributePrerequisite(
    [string]$Path,
    [string]$Locale
) {
    [xml]$document = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $nodes = @($document.SelectNodes('/ItemAppendAttribute/*[@ID]'))
    if ($nodes.Count -lt 193) {
        throw "ItemAppendAttribute $Locale has only $($nodes.Count) rows."
    }
    foreach ($node in $nodes) {
        for ($level = 1; $level -le 25; $level++) {
            if (-not $node.HasAttribute("L$level")) {
                throw "ItemAppendAttribute $Locale ID $($node.ID) lacks L$level."
            }
        }
    }
}

function Assert-LocalizationKeys(
    [string]$Text,
    [string]$Locale,
    [string]$Label
) {
    foreach ($key in @('MaterialBase6', 'MaterialAppend6', 'MaterialOdds5')) {
        $matches = [regex]::Matches(
            $Text,
            ('(?m)^{0}\t[^\r\n]*(?=\r?$)' -f [regex]::Escape($key))
        )
        if ($matches.Count -ne 1) {
            throw "$Label $Locale must contain exactly one '$key' key; found $($matches.Count)."
        }
    }
}

$paths = @{}
$results = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $base = Join-Path $ClientRoot "Localization\$locale"
    $paths[$locale] = @{
        Equip = Join-Path $base 'Settings\Sys\EquipForge.xml'
        Bijou = Join-Path $base 'Settings\Sys\BijouForge.xml'
        Item = Join-Path $base 'Settings\Sys\ItemBaseAttribute.xml'
        ItemAppend = Join-Path $base 'Settings\Sys\ItemAppendAttribute.xml'
        Names = Join-Path $base 'Text\EquipName.dat'
        Descriptions = Join-Path $base 'Text\EquipDescription.dat'
    }
    foreach ($path in $paths[$locale].Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required client file was not found: $path"
        }
    }
    Assert-ItemAppendAttributePrerequisite $paths[$locale].ItemAppend $locale
    $equipResult = Patch-EquipForgeText (
        [IO.File]::ReadAllText($paths[$locale].Equip, [Text.Encoding]::UTF8)
    ) $locale
    $bijouResult = Patch-BijouForgeText (
        [IO.File]::ReadAllText($paths[$locale].Bijou, [Text.Encoding]::UTF8)
    ) $locale
    $itemResult = Patch-ItemBaseText (
        [IO.File]::ReadAllText($paths[$locale].Item, [Text.Encoding]::UTF8)
    ) (Get-ForgeIds $equipResult.Text) $locale
    $descriptionEncoding = if ($locale -eq 'en_us') {
        $utf16LeBom
    }
    else {
        $gb2312
    }
    Assert-LocalizationKeys (
        [IO.File]::ReadAllText($paths[$locale].Names, $descriptionEncoding)
    ) $locale 'EquipName'
    $descriptionResult = Patch-DescriptionText (
        [IO.File]::ReadAllText(
            $paths[$locale].Descriptions,
            $descriptionEncoding
        )
    ) $locale
    $results[$locale] = @{
        Equip = $equipResult
        Bijou = $bijouResult
        Item = $itemResult
        Descriptions = $descriptionResult
        DescriptionEncoding = $descriptionEncoding
    }
}

$exePath = Join-Path $ClientRoot 'Origin.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Origin.exe was not found: $exePath"
}
$exeBytes = [IO.File]::ReadAllBytes($exePath)

# These existing Q20/G25 score and L25 append-attribute patches are required,
# but are outside this tool's write scope.
Assert-ExactBytes $exeBytes 0xA70AA (
    [byte[]](0x83,0xF8,0x14)
) 'single-item Q20 score cap'
Assert-ExactBytes $exeBytes 0xA70B3 (
    [byte[]](0x83,0xFF,0x19)
) 'single-item G25 score cap'
Assert-ExactBytes $exeBytes 0xA7505 (
    [byte[]](0x83,0xF9,0x15)
) 'aggregate Q20 score cap'
Assert-ExactBytes $exeBytes 0xA750E (
    [byte[]](0x83,0xFD,0x1A)
) 'aggregate G25 score cap'
Assert-ExactBytes $exeBytes 0x3F275 (
    [byte[]](0x80,0x38,0x4C,0x0F,0x85)
) 'L25 XML loader hook'
Assert-ExactBytes $exeBytes 0x3F2CA (
    [byte[]](0x83,0xF9,0x19)
) 'L25 XML loader ceiling'
Assert-ExactBytes $exeBytes 0x180370 (
    [byte[]](0x74,0x5A)
) 'L25 append vector clamp branch'
Assert-ExactBytes $exeBytes 0x180381 (
    [byte[]](0x8D,0x58,0xFF,0x90,0x90)
) 'L25 append vector clamp body'

$binarySites = @(
    @{ Name='sapphire_preflight_current_q19'; Offset=0x23A18; Prefix=[byte[]](0x80,0x7F,0x48); Allowed=[byte[]](0x09,0x0C,0x13); Desired=[byte]0x13; Suffix=[byte[]](0x7E) },
    @{ Name='shared_success_quality_q20'; Offset=0x2459C; Prefix=[byte[]](0x80,0xF9); Allowed=[byte[]](0x0A,0x0C,0x0D,0x14); Desired=[byte]0x14; Suffix=[byte[]](0x0F,0x8F) },
    @{ Name='generic_result_quality_q20'; Offset=0x24776; Prefix=[byte[]](0x80,0xF9); Allowed=[byte[]](0x0A,0x0D,0x14); Desired=[byte]0x14; Suffix=[byte[]](0x7F) },
    @{ Name='quality_increment_ceiling_q20'; Offset=0x24981; Prefix=[byte[]](0x80,0x78,0x48); Allowed=[byte[]](0x0A,0x0D,0x14); Desired=[byte]0x14; Suffix=[byte[]](0x7D) },
    @{ Name='forge_ui_main_exclusive_q21'; Offset=0x15DEC4; Prefix=[byte[]](0x3C); Allowed=[byte[]](0x0B,0x0D,0x0E,0x15); Desired=[byte]0x15; Suffix=[byte[]](0x0F,0x8D) },
    @{ Name='forge_ui_alt_exclusive_q21'; Offset=0x15E818; Prefix=[byte[]](0x3C); Allowed=[byte[]](0x0B,0x0D,0x0E,0x15); Desired=[byte]0x15; Suffix=[byte[]](0x0F,0x8D) },
    @{ Name='forge_ui_sapphire_current_q19'; Offset=0x160CA2; Prefix=[byte[]](0x80,0x7B,0x48); Allowed=[byte[]](0x09,0x0C,0x13); Desired=[byte]0x13; Suffix=[byte[]](0x7E) },
    @{ Name='emerald_preflight_current_g24'; Offset=0x23A24; Prefix=[byte[]](0x80,0x7F,0x49); Allowed=[byte[]](0x0B,0x11,0x18); Desired=[byte]0x18; Suffix=[byte[]](0xBD) },
    @{ Name='shared_success_grade_g25'; Offset=0x245B0; Prefix=[byte[]](0x80,0xF9); Allowed=[byte[]](0x0C,0x12,0x19); Desired=[byte]0x19; Suffix=[byte[]](0x0F,0x8F) },
    @{ Name='generic_result_grade_g25'; Offset=0x24781; Prefix=[byte[]](0x3C); Allowed=[byte[]](0x0C,0x12,0x19); Desired=[byte]0x19; Suffix=[byte[]](0x7F,0x19) },
    @{ Name='forge_ui_emerald_current_g24'; Offset=0x160CAF; Prefix=[byte[]](0x80,0x7B,0x49); Allowed=[byte[]](0x0B,0x11,0x18); Desired=[byte]0x18; Suffix=[byte[]](0x7F,0x04) }
)

# Only quality-indexed/default base vectors and AppFraction are resized.
# ArmEff*/Defend* are rank tables, and MainAttribute is an allowed-ID list.
$constructorSites = @(
    @{ Name='item_default_max_hp'; Offset=0x37202; Desired=0x14; Float=$false },
    @{ Name='item_default_max_mp'; Offset=0x37217; Desired=0x14; Float=$false },
    @{ Name='item_default_attack'; Offset=0x3722C; Desired=0x14; Float=$false },
    @{ Name='item_default_defence'; Offset=0x37241; Desired=0x14; Float=$false },
    @{ Name='item_default_magic_attack'; Offset=0x37256; Desired=0x14; Float=$false },
    @{ Name='item_default_magic_recovery'; Offset=0x3726F; Desired=0x14; Float=$false },
    @{ Name='item_default_hit'; Offset=0x37280; Desired=0x14; Float=$false },
    @{ Name='item_default_miss'; Offset=0x37295; Desired=0x14; Float=$false },
    @{ Name='item_default_fury_attack'; Offset=0x372AA; Desired=0x14; Float=$false },
    @{ Name='item_default_fury_recovery'; Offset=0x372BF; Desired=0x14; Float=$false },
    @{ Name='item_default_speed'; Offset=0x372D6; Desired=0x14; Float=$true },
    @{ Name='item_default_physical_damage'; Offset=0x372ED; Desired=0x14; Float=$true },
    @{ Name='item_default_magic_damage'; Offset=0x37304; Desired=0x14; Float=$true },
    @{ Name='item_default_injure_imbibe'; Offset=0x37319; Desired=0x14; Float=$false },
    @{ Name='item_default_accept_cure'; Offset=0x37330; Desired=0x14; Float=$true },
    @{ Name='item_default_cure'; Offset=0x37347; Desired=0x14; Float=$true },
    @{ Name='item_default_state'; Offset=0x3735C; Desired=0x14; Float=$false },
    @{ Name='item_default_state_immunity'; Offset=0x37371; Desired=0x14; Float=$false },
    @{ Name='item_default_attack_radius'; Offset=0x37388; Desired=0x14; Float=$true },
    @{ Name='item_default_attack_speed_1'; Offset=0x3739F; Desired=0x14; Float=$true },
    @{ Name='item_default_attack_speed_2'; Offset=0x373BA; Desired=0x14; Float=$false },
    @{ Name='item_default_base_fraction'; Offset=0x373CB; Desired=0x14; Float=$false },
    @{ Name='item_default_append_fraction'; Offset=0x373E0; Desired=0x19; Float=$false }
)
foreach ($site in $constructorSites) {
    $suffix = if ($site.Float) {
        [byte[]](0xD9,0x5C,0x24,0x1C)
    }
    else {
        [byte[]](0x8D,0x44,0x24,0x1C)
    }
    $allowedCounts = if ($site.Desired -eq 0x14) {
        [byte[]](0x0C,0x0D,0x14)
    }
    else {
        [byte[]](0x0C,0x0D,0x19)
    }
    $binarySites += @{
        Name = $site.Name
        Offset = $site.Offset
        Prefix = [byte[]](0x6A)
        Allowed = $allowedCounts
        Desired = [byte]$site.Desired
        Suffix = $suffix
    }
}

# These four constructor counts belong to independent rank tables. Require the
# current Q13-era count and validate it again after writing, but never resize it.
$binarySites += @(
    @{ Name='preserve_item_default_armor_effect'; Offset=0x373F5; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) },
    @{ Name='preserve_item_default_armor_effect_ratio'; Offset=0x3740A; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) },
    @{ Name='preserve_item_default_defend_effect'; Offset=0x3741F; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) },
    @{ Name='preserve_item_default_defend_ratio'; Offset=0x37434; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) }
)

$binaryChanges = 0
foreach ($site in $binarySites) {
    Assert-BinaryContext $exeBytes $site
    if ($exeBytes[$site.Offset] -ne $site.Desired) {
        $exeBytes[$site.Offset] = $site.Desired
        $binaryChanges++
    }
}

$changedPaths = [Collections.Generic.List[string]]::new()
foreach ($locale in @('en_us', 'zh_cn')) {
    $result = $results[$locale]
    if ([IO.File]::ReadAllText(
            $paths[$locale].Equip,
            [Text.Encoding]::UTF8
        ) -cne $result.Equip.Text) {
        $changedPaths.Add($paths[$locale].Equip)
    }
    if ([IO.File]::ReadAllText(
            $paths[$locale].Bijou,
            [Text.Encoding]::UTF8
        ) -cne $result.Bijou) {
        $changedPaths.Add($paths[$locale].Bijou)
    }
    if ([IO.File]::ReadAllText(
            $paths[$locale].Item,
            [Text.Encoding]::UTF8
        ) -cne $result.Item.Text) {
        $changedPaths.Add($paths[$locale].Item)
    }
    if ([IO.File]::ReadAllText(
            $paths[$locale].Descriptions,
            $result.DescriptionEncoding
        ) -cne $result.Descriptions) {
        $changedPaths.Add($paths[$locale].Descriptions)
    }
}
if ($binaryChanges -gt 0) { $changedPaths.Add($exePath) }

$changedPathSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($path in $changedPaths) {
    [void]$changedPathSet.Add([IO.Path]::GetFullPath($path))
}

$backupPath = $null
if ($changedPaths.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot (
        'client-forge-boundless-g25-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    )
    [IO.Directory]::CreateDirectory($backupPath) | Out-Null
    foreach ($path in $changedPaths) {
        $relative = Get-ClientRelativePath $ClientRoot $path
        $destination = Join-Path $backupPath $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
            Out-Null
        Copy-Item -LiteralPath $path -Destination $destination
    }
    foreach ($locale in @('en_us', 'zh_cn')) {
        $result = $results[$locale]
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Equip)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Equip,
                $result.Equip.Text,
                $utf8Bom
            )
        }
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Bijou)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Bijou,
                $result.Bijou,
                $utf8Bom
            )
        }
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Item)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Item,
                $result.Item.Text,
                $utf8Bom
            )
        }
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Descriptions)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Descriptions,
                $result.Descriptions,
                $result.DescriptionEncoding
            )
        }
    }
    if ($binaryChanges -gt 0) {
        [IO.File]::WriteAllBytes($exePath, $exeBytes)
    }
}

# Re-run every transform against written data. Any difference is an
# idempotence or post-write validation failure.
foreach ($locale in @('en_us', 'zh_cn')) {
    $equipText = [IO.File]::ReadAllText(
        $paths[$locale].Equip,
        [Text.Encoding]::UTF8
    )
    if ((Patch-EquipForgeText $equipText $locale).Text -cne $equipText) {
        throw "EquipForge post-write validation was not idempotent for $locale."
    }
    $bijouText = [IO.File]::ReadAllText(
        $paths[$locale].Bijou,
        [Text.Encoding]::UTF8
    )
    if ((Patch-BijouForgeText $bijouText $locale) -cne $bijouText) {
        throw "BijouForge post-write validation was not idempotent for $locale."
    }
    $itemText = [IO.File]::ReadAllText(
        $paths[$locale].Item,
        [Text.Encoding]::UTF8
    )
    if ((Patch-ItemBaseText (
                $itemText
            ) (Get-ForgeIds $equipText) $locale).Text -cne $itemText) {
        throw "ItemBaseAttribute post-write validation was not idempotent for $locale."
    }
    $encoding = $results[$locale].DescriptionEncoding
    $descriptionText = [IO.File]::ReadAllText(
        $paths[$locale].Descriptions,
        $encoding
    )
    if ((Patch-DescriptionText $descriptionText $locale) -cne
        $descriptionText) {
        throw "Description post-write validation was not idempotent for $locale."
    }
}

$writtenBytes = [IO.File]::ReadAllBytes($exePath)
foreach ($site in $binarySites) {
    Assert-BinaryContext $writtenBytes $site
    if ($writtenBytes[$site.Offset] -ne $site.Desired) {
        throw "Origin.exe post-write validation failed at $($site.Name)."
    }
}

[pscustomobject]@{
    ChangedFiles = $changedPaths.Count
    BackupPath = $backupPath
    EnForgeRows = $results['en_us'].Equip.Rows
    ZhForgeRows = $results['zh_cn'].Equip.Rows
    EnForgeRowsChanged = $results['en_us'].Equip.ChangedRows
    ZhForgeRowsChanged = $results['zh_cn'].Equip.ChangedRows
    EnItemRows = $results['en_us'].Item.Rows
    ZhItemRows = $results['zh_cn'].Item.Rows
    EnItemRowsChanged = $results['en_us'].Item.ChangedRows
    ZhItemRowsChanged = $results['zh_cn'].Item.ChangedRows
    Tier5Ids = '4215,4225,4234'
    MaximumQuality = $maximumQuality
    MaximumGrade = $maximumGrade
    Tier5PrimaryBonus = $tier5PrimaryBonus
    Tier5CrystalBonus = $tier5CrystalBonus
    BinaryBytesChanged = $binaryChanges
    OriginSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash
}
