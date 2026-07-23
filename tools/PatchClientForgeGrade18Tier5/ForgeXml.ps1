function Extend-NumericValues([string]$Value, [int]$TargetCount) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -ge $TargetCount) { return $Value }
    if ($parts.Count -lt 2) { throw "Cannot extrapolate a vector with $($parts.Count) value(s)." }

    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) { $numbers.Add([decimal]::Parse($part, $invariant)) }
    $delta = $numbers[$numbers.Count - 1] - $numbers[$numbers.Count - 2]
    while ($numbers.Count -lt $TargetCount) { $numbers.Add($numbers[$numbers.Count - 1] + $delta) }
    return (($numbers | ForEach-Object { Format-Decimal $_ }) -join ',')
}

function Set-GradeProbabilityVector([string]$Value) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -lt 11) { throw "AppendProyAdd has only $($parts.Count) values; G1-G11 are required." }
    $result = @($parts | Select-Object -First 11) + @('-245', '-270', '-295', '-320', '-345', '-370', '0')
    if ($parts.Count -gt 18) { $result += @($parts | Select-Object -Skip 18) }
    return ($result -join ',')
}

function Set-GradeMoneyVector([string]$Cmoney, [string]$Bmoney, [string]$ItemId) {
    $current = @(Split-Values $Cmoney)
    $qualityMoney = @(Split-Values $Bmoney)
    if ($current.Count -lt 11) { throw "Cmoney for item $ItemId has only $($current.Count) values; G1-G11 are required." }
    if ($qualityMoney.Count -lt 2) { throw "Bmoney for item $ItemId has no economy unit." }

    $unit = [decimal]::Parse($qualityMoney[1], $invariant)
    $tail = @(25, 30, 35, 40, 45, 50) | ForEach-Object { Format-Decimal ($unit * $_) }
    $result = @($current | Select-Object -First 11) + $tail + @('0')
    if ($current.Count -gt 18) { $result += @($current | Select-Object -Skip 18) }
    return ($result -join ',')
}

function Patch-EquipForgeText([string]$Text, [string]$Locale) {
    $state = @{ Rows = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $append = Get-AttributeValue $element 'AppendProyAdd'
        $cmoney = Get-AttributeValue $element 'Cmoney'
        $bmoney = Get-AttributeValue $element 'Bmoney'
        if ($null -eq $append -or $null -eq $cmoney -or $null -eq $bmoney) { return $element }

        $state.Rows++
        $id = Get-AttributeValue $element 'ID'
        $updated = Set-AttributeValue $element 'AppendProyAdd' (Set-GradeProbabilityVector $append)
        $updated = Set-AttributeValue $updated 'Cmoney' (Set-GradeMoneyVector $cmoney $bmoney $id)
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    $expectedRows = $expectedForgeRows[$Locale]
    if ($null -eq $expectedRows -or $state.Rows -ne $expectedRows) {
        throw "Expected $expectedRows EquipForge rows for $Locale; found $($state.Rows)."
    }

    [xml]$document = $patched
    foreach ($node in $document.SelectNodes('//*[@ID and @AppendProyAdd and @Cmoney and @Bmoney]')) {
        $append = @(Split-Values $node.AppendProyAdd)
        $money = @(Split-Values $node.Cmoney)
        if ($append.Count -lt 18 -or ($append[11..17] -join ',') -ne '-245,-270,-295,-320,-345,-370,0') {
            throw "AppendProyAdd G12-G18 validation failed for $Locale item $($node.ID)."
        }
        if ($money.Count -lt 18 -or $money[17] -ne '0') {
            throw "Cmoney G18 validation failed for $Locale item $($node.ID)."
        }
    }

    return [pscustomobject]@{ Text = $patched; Rows = $state.Rows; ChangedRows = $state.Changed }
}

function Upsert-XmlElementAfterId(
    [string]$Text,
    [string]$Id,
    [string]$AnchorId,
    [string]$Element,
    [string]$Label
) {
    $existingPattern = "<[^<>]*\bID=`"$([regex]::Escape($Id))`"[^<>]*/>"
    $existing = [regex]::Matches($Text, $existingPattern)
    if ($existing.Count -gt 1) { throw "Found duplicate $Label rows for ID $Id." }
    if ($existing.Count -eq 1) {
        $match = $existing[0]
        return $Text.Substring(0, $match.Index) + $Element + $Text.Substring($match.Index + $match.Length)
    }

    $anchorPattern = "<[^<>]*\bID=`"$([regex]::Escape($AnchorId))`"[^<>]*/>"
    $anchors = [regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) { throw "Expected one $Label anchor ID $AnchorId; found $($anchors.Count)." }
    $anchor = $anchors[0]
    $afterAnchor = $anchor.Index + $anchor.Length
    $tail = $Text.Substring($afterAnchor)
    $separator = ''
    if ($tail.StartsWith("`r`n") -or $tail.StartsWith("`n")) {
        $newline = if ($tail.StartsWith("`r`n")) { "`r`n" } else { "`n" }
        $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $anchor.Index - 1)) + 1
        $indent = $Text.Substring($lineStart, $anchor.Index - $lineStart)
        if ($indent -notmatch '^[ \t]*$') { $indent = '' }
        $separator = $newline + $indent
    }
    return $Text.Substring(0, $afterAnchor) + $separator + $Element + $tail
}

function Patch-BijouForgeText([string]$Text, [string]$Locale) {
    $emeraldPattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="4223"[^<>]*/>'
    $matches = [regex]::Matches($Text, $emeraldPattern)
    if ($matches.Count -ne 1) { throw "Expected one Emerald 4 rule for $Locale; found $($matches.Count)." }
    $emerald = $matches[0].Value
    if ((Get-AttributeValue $emerald 'MaterialType') -ne '3') { throw "Emerald 4 MaterialType mismatch for $Locale." }
    $patched = $Text.Substring(0, $matches[0].Index) +
        (Set-AttributeValue $emerald 'Round' '10,17') +
        $Text.Substring($matches[0].Index + $matches[0].Length)

    $patched = Upsert-XmlElementAfterId $patched '4215' '4213' '<MaterialBase6 ID="4215" MaterialType="2" MaterialProyAdd="32" Round="8,12"/>' 'Sapphire 5'
    $patched = Upsert-XmlElementAfterId $patched '4225' '4223' '<MaterialAppend6 ID="4225" MaterialType="3" MaterialProyAdd="32" Round="10,17"/>' 'Emerald 5'
    $patched = Upsert-XmlElementAfterId $patched '4234' '4233' "<MaterialOdds5 ID=`"4234`" MaterialType=`"4`" MaterialProyAdd=`"$tier5CrystalProbabilityBonus`"/>" 'Crystal 5'

    [xml]$document = $patched
    $expected = @(
        @{ Id = '4215'; Type = '2'; Bonus = '32'; Round = '8,12' },
        @{ Id = '4223'; Type = '3'; Bonus = '24'; Round = '10,17' },
        @{ Id = '4225'; Type = '3'; Bonus = '32'; Round = '10,17' },
        @{ Id = '4234'; Type = '4'; Bonus = "$tier5CrystalProbabilityBonus"; Round = '' }
    )
    foreach ($entry in $expected) {
        $nodes = @($document.SelectNodes("//*[@ID='$($entry.Id)']"))
        if ($nodes.Count -ne 1 -or $nodes[0].MaterialType -ne $entry.Type -or $nodes[0].MaterialProyAdd -ne $entry.Bonus) {
            throw "Tier-5 Bijou validation failed for $Locale ID $($entry.Id)."
        }
        if ($entry.Round -and $nodes[0].Round -ne $entry.Round) {
            throw "Tier-5 Round validation failed for $Locale ID $($entry.Id)."
        }
    }
    return $patched
}
