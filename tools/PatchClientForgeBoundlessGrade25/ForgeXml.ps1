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
