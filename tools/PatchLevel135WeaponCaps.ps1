param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int]$MinimumLevel = 135
)

$ErrorActionPreference = "Stop"

$baseFraction = '0,8,18,28,40,54,74,100,140,200,260,340,440,560,700,860,1040,1240,1460,1700'
$appFraction = '10,13,16,20,24,28,32,40,50,60,80,100,130,170,220,280,350,430,520,620,730,850,980,1120,1270'
$armEffFraction = '40,100,180,240,300,460,600,1200,4000,8000,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1'
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
    'InjureImbibe',
    'DefendFraction',
    'DefendEff'
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

function Extend-NumericList([string]$value, [int]$targetCount, [string]$fallback) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $fallback
    }

    $parts = @($value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
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

    return (($numbers | ForEach-Object {
        $_.ToString('0.############', [Globalization.CultureInfo]::InvariantCulture)
    }) -join ',')
}

function Extend-TextList([string]$value, [int]$targetCount) {
    $parts = @()
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $parts = @($value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
    }

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

function Get-ArmEff([string]$classId) {
    switch ($classId) {
        '2' { return '201,202,203,204,205,205,205,206,208,209,205,205,205,205,205,205,205,205,205,205,205,205,205,205,205' }
        '3' { return '51,52,53,54,55,55,55,56,58,59,55,55,55,55,55,55,55,55,55,55,55,55,55,55,55' }
        default { return '1,2,3,4,5,5,5,6,8,9,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5' }
    }
}

function Test-TargetWeapon([string]$line) {
    if ((Get-AttributeValue $line 'Type') -ne 'weapon') {
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
        if (-not (Test-TargetWeapon $line)) {
            $line
            continue
        }

        foreach ($stat in $numericStats20) {
            $current = Get-AttributeValue $line $stat
            $line = Set-AttributeValue $line $stat (Extend-NumericList $current 20 $zero20)
        }

        $line = Set-AttributeValue $line 'MainAttribute' (Extend-TextList (Get-AttributeValue $line 'MainAttribute') 25)
        $line = Set-AttributeValue $line 'BaseFraction' $baseFraction
        $line = Set-AttributeValue $line 'AppFraction' $appFraction
        $line = Set-AttributeValue $line 'ArmEffFraction' $armEffFraction
        $line = Set-AttributeValue $line 'ArmEff' (Get-ArmEff (Get-AttributeValue $line 'Class'))

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

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
