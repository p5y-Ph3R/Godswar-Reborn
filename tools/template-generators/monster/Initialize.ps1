$ErrorActionPreference = "Stop"

$monsterConfigPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\MonsterConfig.ini"
$questMonsterPath = Join-Path $ClientRoot "Localization\en_us\Text\QuestMonster.dat"
$questPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Quest.xml"

foreach ($path in @($monsterConfigPath, $questMonsterPath, $questPath)) {
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

function ConvertTo-CSharpNullableFloat($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "null"
    }

    return ([float]$value).ToString($invariantCulture) + "f"
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

function ConvertTo-SqlNullableReal($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "NULL"
    }

    return ([float]$value).ToString($invariantCulture)
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

        if ($trimmed -match '^\[(?<name>[^\]]+)\]') {
            $currentSection = $Matches.name.Trim()
            if (-not $sections.Contains($currentSection)) {
                $sections[$currentSection] = [ordered]@{}
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
            $values[[int]$parts[0].Trim()] = $parts[1].Trim()
        }
    }

    return $values
}

function Resolve-ClientPath([string]$path) {
    $normalized = $path.Trim().Replace('/', '\')
    if ($normalized.StartsWith(".\")) {
        $normalized = $normalized.Substring(2)
    }

    return Join-Path $ClientRoot $normalized
}

function Get-AttributeValue($attributes, [string]$name) {
    if ($attributes.Contains($name)) {
        return $attributes[$name]
    }

    return $null
}

function ConvertTo-Position([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value -eq "1,1") {
        return $null
    }

    $parts = $value.Replace("f", "").Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) {
        return $null
    }

    return @{
        X = [float]::Parse($parts[0].Trim(), $invariantCulture)
        Z = [float]::Parse($parts[1].Trim(), $invariantCulture)
    }
}

function Get-Rank([string]$templateKey, [string]$displayName) {
    if ($templateKey -match 'boss' -or $displayName -match '\[BOSS\]' -or $displayName -match 'Boss') {
        return "boss"
    }

    if ($templateKey -match 'elite' -or $displayName -match '\[Elite\]' -or $displayName -match '\[ELITE\]') {
        return "elite"
    }

    if ($displayName -match '\[Pet\]') {
        return "pet"
    }

    return "normal"
}

function Get-FloatValue($attributes, [string]$name) {
    $value = Get-AttributeValue $attributes $name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return [Nullable[Single]][float]::Parse($value, $invariantCulture)
}

function Get-ShortValue($attributes, [string]$name) {
    $value = Get-AttributeValue $attributes $name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return [Nullable[Int16]][Int16]$value
}
