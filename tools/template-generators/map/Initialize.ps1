$ErrorActionPreference = "Stop"

$addressConfigPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\AddressConfig.ini"
$mapIdToNamePath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\MapIdToNameConfig.ini"
$mapSoundPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\MapSoundConfig.xml"
$spanMapPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\SpanMapConfig.xml"
$scenesPath = Join-Path $ClientRoot "Localization\en_us\Text\Scenes.dat"

foreach ($path in @($addressConfigPath, $mapIdToNamePath, $mapSoundPath, $spanMapPath, $scenesPath)) {
    if (-not (Test-Path $path)) {
        throw "Required client file not found: $path"
    }
}

$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

function ConvertTo-CSharpString([string]$value) {
    if ($null -eq $value) {
        return 'string.Empty'
    }

    return '"' + $value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function ConvertTo-SqlString([string]$value) {
    if ($null -eq $value) {
        return 'NULL'
    }

    return "'" + $value.Replace("'", "''") + "'"
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

function ConvertTo-CSharpFloat([float]$value) {
    return $value.ToString($invariantCulture) + "f"
}

function ConvertTo-SqlReal([float]$value) {
    return $value.ToString($invariantCulture)
}

function ConvertTo-CSharpShortArray($values) {
    if ($null -eq $values -or $values.Count -eq 0) {
        return "[]"
    }

    return "new short[] { " + (($values | ForEach-Object { ([Int16]$_).ToString($invariantCulture) }) -join ", ") + " }"
}

function ConvertTo-SqlSmallintArray($values) {
    if ($null -eq $values -or $values.Count -eq 0) {
        return "'{}'::smallint[]"
    }

    return "ARRAY[" + (($values | ForEach-Object { ([Int16]$_).ToString($invariantCulture) }) -join ",") + "]::smallint[]"
}

function Read-IniFile([string]$path) {
    $sections = [ordered]@{}
    $currentSection = ""
    $sections[$currentSection] = [ordered]@{}

    foreach ($line in Get-Content $path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith(";") -or $trimmed.StartsWith("#")) {
            continue
        }

        if ($trimmed -match '^\[(?<name>[^\]]+)\]\s*;?(?<comment>.*)$') {
            $currentSection = $Matches.name.Trim()
            if (-not $sections.Contains($currentSection)) {
                $sections[$currentSection] = [ordered]@{}
            }

            if (-not [string]::IsNullOrWhiteSpace($Matches.comment)) {
                $sections[$currentSection]["__comment"] = $Matches.comment.Trim()
            }

            continue
        }

        if ($trimmed -match '^(?<key>[^=]+)=(?<value>.*)$') {
            $sections[$currentSection][$Matches.key.Trim()] = $Matches.value.Trim().TrimEnd(";")
        }
    }

    return $sections
}

function Read-TabFile([string]$path) {
    $values = @{}
    foreach ($line in Get-Content $path) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("//")) {
            continue
        }

        $parts = $line -split "`t", 2
        if ($parts.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($parts[0])) {
            $values[$parts[0].Trim()] = $parts[1].Trim()
        }
    }

    return $values
}

function ConvertTo-Position([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $parts = $value.Replace("f", "").TrimEnd(";").Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) {
        return $null
    }

    return @{
        X = [float]::Parse($parts[0].Trim(), $invariantCulture)
        Z = [float]::Parse($parts[1].Trim(), $invariantCulture)
    }
}

function ConvertTo-Area([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $parts = $value.Replace("f", "").TrimEnd(";").Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 4) {
        return $null
    }

    return @{
        X1 = [float]::Parse($parts[0].Trim(), $invariantCulture)
        Z1 = [float]::Parse($parts[1].Trim(), $invariantCulture)
        X2 = [float]::Parse($parts[2].Trim(), $invariantCulture)
        Z2 = [float]::Parse($parts[3].Trim(), $invariantCulture)
    }
}

function Resolve-ClientPath([string]$path) {
    $normalized = $path.Trim().Replace('/', '\')
    if ($normalized.StartsWith(".\")) {
        $normalized = $normalized.Substring(2)
    }

    return Join-Path $ClientRoot $normalized
}
