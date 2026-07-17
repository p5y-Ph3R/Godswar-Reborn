param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int]$MinimumLevel = 135
)

$ErrorActionPreference = "Stop"

$qualityProfile = @(0, 8, 18, 28, 40, 54, 74, 100, 140, 200, 230, 260, 295, 330, 370, 410, 455, 500, 550, 600)
$gradeProfile = @(10, 13, 16, 20, 24, 28, 32, 40, 50, 60, 80, 100, 116, 133, 151, 170, 190, 211, 233, 256, 280, 305, 332, 365, 400)
$zero20 = (@(1..20 | ForEach-Object { '0' }) -join ',')

$numericStats20 = @(
    'Attack',
    'AttackRadius',
    'AttackSpeed',
    'MaxHP',
    'MaxMP',
    'Defence',
    'MagicAk',
    'MagicRec',
    'Hit',
    'Miss',
    'State',
    'StateImmunity',
    'AcceptCure',
    'Cure',
    'PhysicalDamage',
    'MagicDamage',
    'MagicDamageAbsorb',
    'PhysicalDamageAbsorb',
    'Speed',
    'FuryAddAk',
    'FuryAddRec',
    'InjureImbibe'
)

function Get-AttributeValue([string]$line, [string]$name) {
    $pattern = "(?<=\s)$([regex]::Escape($name))=`"([^`"]*)`""
    $match = [regex]::Match($line, $pattern)
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups[1].Value
}

function Set-AttributeValue([string]$line, [string]$name, [string]$value) {
    $pattern = "(?<=\s)$([regex]::Escape($name))=`"[^`"]*`""
    $replacement = "$name=`"$value`""
    if ([regex]::IsMatch($line, $pattern)) {
        return [regex]::Replace($line, $pattern, $replacement, 1)
    }

    $insert = " $replacement"
    return $line -replace '\s*/>\s*$', "$insert/>"
}

function Split-List([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @()
    }

    return @($value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
}

function Format-Decimal([decimal]$value) {
    return $value.ToString('0.############', [Globalization.CultureInfo]::InvariantCulture)
}

function Extend-NumericList([string]$value, [int]$targetCount, [string]$fallback) {
    $parts = Split-List $value
    if ($parts.Count -eq 0) {
        return $fallback
    }

    if ($parts.Count -ge $targetCount) {
        return (($parts | Select-Object -First $targetCount) -join ',')
    }

    $numbers = [System.Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) {
        $numbers.Add([decimal]::Parse($part, [Globalization.CultureInfo]::InvariantCulture))
    }

    $delta = if ($numbers.Count -ge 2) {
        $numbers[$numbers.Count - 1] - $numbers[$numbers.Count - 2]
    } else {
        0
    }

    while ($numbers.Count -lt $targetCount) {
        $numbers.Add($numbers[$numbers.Count - 1] + $delta)
    }

    return (($numbers | ForEach-Object { Format-Decimal $_ }) -join ',')
}

function Extend-TextList([string]$value, [int]$targetCount) {
    $parts = Split-List $value
    if ($parts.Count -eq 0) {
        return $value
    }

    if ($parts.Count -ge $targetCount) {
        return (($parts | Select-Object -First $targetCount) -join ',')
    }

    $last = $parts[$parts.Count - 1]
    while ($parts.Count -lt $targetCount) {
        $parts += $last
    }

    return ($parts -join ',')
}

function Extend-ScoreProfile([string]$value, [int[]]$profile, [int]$anchorIndex, [int]$targetCount) {
    $parts = Split-List $value
    if ($parts.Count -eq 0) {
        throw "Cannot extend an empty score profile."
    }

    if ($parts.Count -le $anchorIndex) {
        throw "Cannot extend score profile with only $($parts.Count) values; anchor index $anchorIndex is missing."
    }

    $anchorValue = [decimal]::Parse($parts[$anchorIndex], [Globalization.CultureInfo]::InvariantCulture)
    $anchorProfile = [decimal]$profile[$anchorIndex]
    $result = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $targetCount; $i++) {
        if ($i -lt $parts.Count -and $i -le $anchorIndex) {
            $result.Add($parts[$i])
            continue
        }

        $scaled = if ($anchorProfile -eq 0) {
            0
        } else {
            $anchorValue * ([decimal]$profile[$i] / $anchorProfile)
        }

        $rounded = [Math]::Round($scaled, 0, [MidpointRounding]::AwayFromZero)
        $result.Add(([int]$rounded).ToString([Globalization.CultureInfo]::InvariantCulture))
    }

    return ($result -join ',')
}

function Test-TargetHelmet([string]$line) {
    if ((Get-AttributeValue $line 'Type') -ne 'head') {
        return $false
    }

    $playLevel = Get-AttributeValue $line 'PlayLv'
    if ([string]::IsNullOrWhiteSpace($playLevel)) {
        return $false
    }

    $minLevelText = ($playLevel -split ',', 2)[0]
    $minLevel = 0
    if (-not [int]::TryParse($minLevelText, [ref]$minLevel)) {
        return $false
    }

    return $minLevel -ge $MinimumLevel
}

function Patch-ItemBaseAttribute([string]$path) {
    if (-not (Test-Path $path)) {
        throw "ItemBaseAttribute.xml not found: $path"
    }

    $lines = Get-Content -LiteralPath $path
    $changed = 0
    $patched = foreach ($line in $lines) {
        if (-not (Test-TargetHelmet $line)) {
            $line
            continue
        }

        foreach ($stat in $numericStats20) {
            $current = Get-AttributeValue $line $stat
            $line = Set-AttributeValue $line $stat (Extend-NumericList $current 20 $zero20)
        }

        $line = Set-AttributeValue $line 'MainAttribute' (Extend-TextList (Get-AttributeValue $line 'MainAttribute') 25)
        $line = Set-AttributeValue $line 'BaseFraction' (Extend-ScoreProfile (Get-AttributeValue $line 'BaseFraction') $qualityProfile 9 20)
        $line = Set-AttributeValue $line 'AppFraction' (Extend-ScoreProfile (Get-AttributeValue $line 'AppFraction') $gradeProfile 11 25)

        $changed++
        $line
    }

    Set-Content -LiteralPath $path -Value $patched -Encoding UTF8
    [pscustomobject]@{
        Path = $path
        PatchedRows = $changed
        MinimumLevel = $MinimumLevel
    }
}

$paths = @(
    (Join-Path $ClientRoot 'Localization\en_us\Settings\Sys\ItemBaseAttribute.xml'),
    (Join-Path $ClientRoot 'Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml')
)

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\level135plus-helmet-caps-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path $path) {
        $culture = Split-Path (Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$culture.ItemBaseAttribute.xml") -Force
    }
}

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
