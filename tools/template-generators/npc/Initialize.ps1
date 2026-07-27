$ErrorActionPreference = "Stop"

$npcIniPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\NPC.INI"
$npcNamePath = Join-Path $ClientRoot "Localization\en_us\Text\NpcName.dat"
$npcDescriptionPath = Join-Path $ClientRoot "Localization\en_us\Text\NPCDescription.dat"
$questPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Quest.xml"
$npcFunPath = Join-Path $ClientRoot "Localization\en_us\UI\XML\NpcFun.lua"
$newbieGuideScriptPath = Join-Path $ClientRoot "Localization\en_us\UI\XML\NpcFun\NpcFunNewMan.lua"
$luaTextPath = Join-Path $ClientRoot "Localization\en_us\UI\Base\LuaText.lua"

foreach ($path in @($npcIniPath, $npcNamePath, $npcDescriptionPath, $questPath, $npcFunPath, $newbieGuideScriptPath, $luaTextPath)) {
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

function Get-AttributeValue($attributes, [string]$name) {
    if ($attributes.Contains($name)) {
        return $attributes[$name]
    }

    return $null
}

function Get-NpcKey([string]$templateKey) {
    # Scene keys legitimately contain underscores and digits
    # (Parnitha_1, Field_Test2, Colosseu1, and others). Capture the
    # rightmost "_<actor-id>" segment instead of truncating the scene at
    # its first numeric component.
    if ($templateKey -match '^(?<scene>.+)_(?<id>\d+)(?:_|$)') {
        return "$($Matches.scene)_$($Matches.id)"
    }

    return $templateKey
}

function Get-SceneKey([string]$npcKey) {
    if ($npcKey -match '^(?<scene>.+)_\d+$') {
        return $Matches.scene
    }

    return ""
}

function ConvertTo-Position([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
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
