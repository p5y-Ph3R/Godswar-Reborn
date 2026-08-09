$script:NativeMountQualityCount = 10
$script:TargetMountQualityCount = 20
$script:MountSpeedQualityBonuses = @(
    [decimal]0.00, [decimal]0.01, [decimal]0.02, [decimal]0.03, [decimal]0.04,
    [decimal]0.05, [decimal]0.06, [decimal]0.07, [decimal]0.08, [decimal]0.10,
    [decimal]0.12, [decimal]0.14, [decimal]0.16, [decimal]0.18, [decimal]0.20,
    [decimal]0.22, [decimal]0.24, [decimal]0.26, [decimal]0.28, [decimal]0.30
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

function Format-Decimal([decimal]$Value) {
    return $Value.ToString(
        '0.############',
        [Globalization.CultureInfo]::InvariantCulture
    )
}

function Get-MinimumPositiveDelta([object[]]$Values) {
    $ordered = @($Values | Sort-Object -Unique)
    $minimum = [decimal]0
    for ($index = 1; $index -lt $ordered.Count; $index++) {
        $delta = [decimal]$ordered[$index] - [decimal]$ordered[$index - 1]
        if ($delta -gt 0 -and ($minimum -eq 0 -or $delta -lt $minimum)) {
            $minimum = $delta
        }
    }
    return $minimum
}

function Extend-ByNativeAverageSlope([string]$Value, [int]$TargetCount) {
    $parts = @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
    if ($parts.Count -gt $TargetCount) {
        throw "Refusing to shrink a $($parts.Count)-entry mount vector to $TargetCount."
    }
    if ($parts.Count -lt $script:NativeMountQualityCount) {
        throw "Mount vector has only $($parts.Count) values; expected the native Q1-Q$($script:NativeMountQualityCount) prefix."
    }

    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in ($parts | Select-Object -First $script:NativeMountQualityCount)) {
        $numbers.Add([decimal]::Parse(
            $part,
            [Globalization.CultureInfo]::InvariantCulture
        ))
    }
    for ($index = 1; $index -lt $numbers.Count; $index++) {
        if ($numbers[$index] -lt $numbers[$index - 1]) {
            throw 'Refusing to extend a decreasing mount quality vector.'
        }
    }

    $result = [Collections.Generic.List[string]]::new()
    foreach ($part in ($parts | Select-Object -First $script:NativeMountQualityCount)) {
        $result.Add($part)
    }
    $averageSlope = (
        $numbers[$script:NativeMountQualityCount - 1] - $numbers[0]
    ) / ($script:NativeMountQualityCount - 1)
    $integerOnly = @(
        $numbers | Where-Object { $_ -ne [decimal]::Truncate($_) }
    ).Count -eq 0
    $extensionCount = $TargetCount - $script:NativeMountQualityCount
    for ($step = 1; $step -le $extensionCount; $step++) {
        $valueAtQuality =
            $numbers[$script:NativeMountQualityCount - 1] + ($averageSlope * $step)
        if ($integerOnly) {
            $valueAtQuality = [decimal]::Round(
                $valueAtQuality,
                0,
                [MidpointRounding]::AwayFromZero
            )
        }
        $result.Add((Format-Decimal $valueAtQuality))
    }
    return ($result -join ',')
}

function Extend-FlatMountByFamilyDelta(
    [string]$Value,
    [int]$TargetCount,
    [decimal]$FamilyDelta
) {
    $parts = @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
    if ($parts.Count -gt $TargetCount) {
        throw "Refusing to shrink a $($parts.Count)-entry mount vector to $TargetCount."
    }
    if ($parts.Count -lt $script:NativeMountQualityCount) {
        throw "Mount vector has only $($parts.Count) values; expected the native Q1-Q$($script:NativeMountQualityCount) prefix."
    }

    $numbers = @(
        $parts | Select-Object -First $script:NativeMountQualityCount |
            ForEach-Object {
                [decimal]::Parse(
                    $_,
                    [Globalization.CultureInfo]::InvariantCulture
                )
            }
    )
    for ($index = 1; $index -lt $numbers.Count; $index++) {
        if ($numbers[$index] -lt $numbers[$index - 1]) {
            throw 'Refusing to extend a decreasing mount quality vector.'
        }
    }
    if (@($numbers | Sort-Object -Unique).Count -gt 1) {
        return Extend-ByNativeAverageSlope $Value $TargetCount
    }

    $result = [Collections.Generic.List[string]]::new()
    foreach ($part in ($parts | Select-Object -First $script:NativeMountQualityCount)) {
        $result.Add($part)
    }
    $baseValue = $numbers[0]
    $extensionCount = $TargetCount - $script:NativeMountQualityCount
    for ($step = 1; $step -le $extensionCount; $step++) {
        $valueAtQuality = $baseValue
        if ($baseValue -gt 0 -and $FamilyDelta -gt 0) {
            $valueAtQuality += $FamilyDelta * $step / $extensionCount
        }
        $result.Add((Format-Decimal $valueAtQuality))
    }
    return ($result -join ',')
}

function Set-MountSpeedQualityCurve([string]$Value, [int]$TargetCount) {
    $parts = @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
    if ($parts.Count -lt $script:NativeMountQualityCount -or
        $parts.Count -gt $TargetCount) {
        throw "Mount Speed has $($parts.Count) values; expected Q1-Q$($script:NativeMountQualityCount) or Q1-Q$TargetCount data."
    }
    if ($TargetCount -ne $script:MountSpeedQualityBonuses.Count) {
        throw 'The mount Speed profile does not match the target quality count.'
    }

    $common = [decimal]::Parse(
        $parts[0],
        [Globalization.CultureInfo]::InvariantCulture
    )
    if ($common -lt [decimal]0) {
        throw 'Mount Common Speed cannot be negative.'
    }
    return ($script:MountSpeedQualityBonuses | ForEach-Object {
        Format-Decimal ($common + $_)
    }) -join ','
}
