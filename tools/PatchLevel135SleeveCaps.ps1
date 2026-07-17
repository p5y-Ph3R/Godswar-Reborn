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

function Test-TargetSleeve([System.Xml.XmlElement]$node) {
    if ($node.GetAttribute('Type') -ne 'cuff') {
        return $false
    }

    $playLevel = $node.GetAttribute('PlayLv')
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
    if (-not (Test-Path -LiteralPath $path)) {
        throw "ItemBaseAttribute.xml not found: $path"
    }

    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.Load($path)

    $changed = 0
    foreach ($node in $document.SelectNodes('/ItemBaseAttribute//*[@ID]')) {
        if (-not (Test-TargetSleeve $node)) {
            continue
        }

        foreach ($stat in $numericStats20) {
            $node.SetAttribute($stat, (Extend-NumericList $node.GetAttribute($stat) 20 $zero20))
        }

        $node.SetAttribute('MainAttribute', (Extend-TextList $node.GetAttribute('MainAttribute') 25))
        $node.SetAttribute('BaseFraction', (Extend-ScoreProfile $node.GetAttribute('BaseFraction') $qualityProfile 9 20))
        $node.SetAttribute('AppFraction', (Extend-ScoreProfile $node.GetAttribute('AppFraction') $gradeProfile 11 25))
        $changed++
    }

    $document.Save($path)
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
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\level135plus-sleeve-caps-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path) {
        $culture = Split-Path (Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$culture.ItemBaseAttribute.xml") -Force
    }
}

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
