$ErrorActionPreference = "Stop"

$skillIniPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Skill.ini"
$magicIniPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Magic.ini"
$itemBasePath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\ItemBaseAttribute.xml"
$equipNamePath = Join-Path $ClientRoot "Localization\en_us\Text\EquipName.dat"
$skillInfoPath = Join-Path $ClientRoot "Localization\en_us\Text\SkillInfo.dat"

foreach ($path in @($skillIniPath, $magicIniPath, $itemBasePath, $equipNamePath, $skillInfoPath)) {
    if (-not (Test-Path $path)) {
        throw "Required client file not found: $path"
    }
}

$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

function ConvertTo-CSharpString([string]$value) {
    if ($null -eq $value) {
        return 'string.Empty'
    }

    return '"' + $value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function ConvertTo-SqlString([string]$value) {
    if ($null -eq $value) {
        return 'NULL'
    }

    return "'" + $value.Replace("'", "''") + "'"
}

function ConvertTo-CSharpShortArray([Int16[]]$values) {
    if ($values.Count -eq 0) {
        return "[]"
    }

    return "new short[] { " + (($values | ForEach-Object { $_.ToString($invariantCulture) }) -join ", ") + " }"
}

function ConvertTo-SqlSmallintArray([Int16[]]$values) {
    if ($values.Count -eq 0) {
        return "ARRAY[]::smallint[]"
    }

    return "ARRAY[" + (($values | ForEach-Object { $_.ToString($invariantCulture) }) -join ",") + "]::smallint[]"
}

function ConvertTo-CSharpNullableShort($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "null"
    }

    return "(short)$(([Int16]$value).ToString($invariantCulture))"
}

function ConvertTo-CSharpNullableInt($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "null"
    }

    return ([Int32]$value).ToString($invariantCulture)
}

function ConvertTo-SqlNullableSmallint($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "NULL"
    }

    return ([Int16]$value).ToString($invariantCulture)
}

function ConvertTo-SqlNullableInt($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "NULL"
    }

    return ([Int32]$value).ToString($invariantCulture)
}

function ConvertTo-CSharpDecimal([decimal]$value) {
    return $value.ToString($invariantCulture) + "m"
}

function ConvertTo-SqlNumeric([decimal]$value) {
    return $value.ToString($invariantCulture)
}

function ConvertTo-RequiredMagicInt($section, [string]$field, [int]$skillId) {
    if ($null -eq $section -or -not $section.Contains($field)) {
        throw "Magic.ini section [$skillId] is missing required field '$field'."
    }

    return [int]::Parse([string]$section[$field], $invariantCulture)
}

function ConvertTo-RequiredMagicDecimal($section, [string]$field, [int]$skillId) {
    if ($null -eq $section -or -not $section.Contains($field)) {
        throw "Magic.ini section [$skillId] is missing required field '$field'."
    }

    return [decimal]::Parse([string]$section[$field], $invariantCulture)
}

function Get-AttributeValue($attributes, [string]$name) {
    if ($attributes.Contains($name)) {
        return $attributes[$name]
    }

    return $null
}

function ConvertTo-IntegerPair([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @(0, 0)
    }

    $parts = $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) {
        return @([int]$parts[0], 0)
    }

    return @([int]$parts[0].Trim(), [int]$parts[1].Trim())
}

function ConvertTo-EffectPair([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @{
            Id = 0
            Value = [decimal]0
        }
    }

    $parts = $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    $effectValue = if ($parts.Count -gt 1) {
        [decimal]::Parse($parts[1].Trim(), $invariantCulture)
    } else {
        [decimal]0
    }

    return @{
        Id = [int]$parts[0].Trim()
        Value = $effectValue
    }
}

function ConvertTo-ClassIds([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return [Int16[]]@()
    }

    return [Int16[]]@(
        $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { [Int16]$_.Trim() }
    )
}

function ConvertTo-LevelRange([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @{
            Min = $null
            Max = $null
        }
    }

    $parts = $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    return @{
        Min = if ($parts.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($parts[0])) { [Nullable[Int32]][int]$parts[0].Trim() } else { $null }
        Max = if ($parts.Count -gt 1 -and -not [string]::IsNullOrWhiteSpace($parts[1])) { [Nullable[Int32]][int]$parts[1].Trim() } else { $null }
    }
}

function ConvertTo-SkillNames([string]$displayName) {
    if ([string]::IsNullOrWhiteSpace($displayName)) {
        return @{
            Display = ""
            Base = ""
            Level = $null
        }
    }

    $clean = $displayName.Trim()
    $clean = $clean -replace '^Book:\s*', ''
    $clean = $clean -replace '^Read it to learn\s+', ''

    $level = $null
    $base = $clean
    if ($clean -match '^(.+?)\s+(\d+)$') {
        $base = $Matches[1].Trim()
        $level = [Nullable[Int16]][Int16]$Matches[2]
    }

    return @{
        Display = $clean
        Base = $base
        Level = $level
    }
}
