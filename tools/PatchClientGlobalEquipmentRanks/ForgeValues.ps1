function Join-Integers([int[]]$Values) {
    return (($Values | ForEach-Object { $_.ToString($invariant) }) -join ',')
}

function Split-Values([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
}

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
    if (-not $match.Success) {
        throw "Required attribute '$Name' is missing."
    }
    $replacement = $Name + '="' + $Value + '"'
    return $Element.Substring(0, $match.Index) + $replacement +
        $Element.Substring($match.Index + $match.Length)
}

function Test-Prefix(
    [string[]]$Actual,
    [int[]]$Expected,
    [int]$Count
) {
    if ($Actual.Count -lt $Count -or $Expected.Count -lt $Count) {
        return $false
    }
    for ($index = 0; $index -lt $Count; $index++) {
        if ($Actual[$index] -cne $Expected[$index].ToString($invariant)) {
            return $false
        }
    }
    return $true
}

function Set-ScaledTail(
    [string]$Value,
    [int[]]$Profile,
    [int]$AnchorIndex,
    [string]$Label
) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -lt $Profile.Count) {
        throw "$Label has only $($parts.Count) entries; expected at least $($Profile.Count)."
    }
    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) {
        $numbers.Add([decimal]::Parse($part, $invariant))
    }
    $anchorValue = $numbers[$AnchorIndex]
    $anchorProfile = [decimal]$Profile[$AnchorIndex]
    if ($anchorValue -le 0 -or $anchorProfile -le 0) {
        throw "$Label has an invalid score anchor."
    }
    $result = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $Profile.Count; $index++) {
        if ($index -le $AnchorIndex) {
            $result.Add($parts[$index])
            continue
        }
        $scaled = $anchorValue * ([decimal]$Profile[$index] / $anchorProfile)
        $rounded = [Math]::Round($scaled, 0, [MidpointRounding]::AwayFromZero)
        $result.Add(([int]$rounded).ToString($invariant))
    }
    return ($result -join ',')
}

function Get-ForgeIds([string]$Text, [string]$Locale) {
    [xml]$document = $Text
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $document.SelectNodes('//*[@ID]')) {
        if (-not $ids.Add($node.GetAttribute('ID'))) {
            throw "Duplicate EquipForge ID $($node.GetAttribute('ID')) for $Locale."
        }
    }
    if ($ids.Count -ne $expectedForgeRows[$Locale]) {
        throw "Expected $($expectedForgeRows[$Locale]) EquipForge IDs for $Locale; found $($ids.Count)."
    }
    if ($ids.Contains('1111')) {
        throw "Non-forgeable GM weapon 1111 unexpectedly appears in $Locale EquipForge."
    }
    foreach ($protectedId in $protectedForgeIds) {
        if (-not $ids.Contains($protectedId)) {
            throw "Protected custom GM equipment $protectedId is missing from $Locale EquipForge."
        }
    }
    return ,$ids
}

function Get-WeaponEffects([string]$ClassId, [string]$ItemId, [string]$Locale) {
    switch ($ClassId) {
        '0' { return Join-Integers $physicalWeaponEffects }
        '1' { return Join-Integers $physicalWeaponEffects }
        '2' { return Join-Integers $class2WeaponEffects }
        '3' { return Join-Integers $class3WeaponEffects }
        default {
            throw "Forgeable ranked weapon $ItemId has unsupported class '$ClassId' for $Locale."
        }
    }
}
