param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int]$MinimumLevel = 135
)

$ErrorActionPreference = "Stop"

$defendFraction = '330,475,750,950,1350,1720,2225,3860,5250,8000,12000,17000,22000,25300,-1'
$defendEff = '1,2,3,4,5,6,7,8,9,10,11,12,13,14,14'

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

function Test-TargetBodyArmor([string]$line) {
    $type = Get-AttributeValue $line 'Type'
    if ($type -ne 'armor' -and $type -ne 'cloth') {
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

    $changed = [ref]0
    $text = Get-Content -LiteralPath $path -Raw
    $patched = [regex]::Replace($text, '<[A-Za-z0-9_]+\b[^<>]*?/>', {
        param($match)

        $node = $match.Value
        if (-not (Test-TargetBodyArmor $node)) {
            return $node
        }

        $node = Set-AttributeValue $node 'DefendFraction' $defendFraction
        $node = Set-AttributeValue $node 'DefendEff' $defendEff
        $changed.Value++
        return $node
    }, [System.Text.RegularExpressions.RegexOptions]::Singleline)

    Set-Content -LiteralPath $path -Value $patched -Encoding UTF8 -NoNewline
    [pscustomobject]@{
        Path = $path
        PatchedRows = $changed.Value
        MinimumLevel = $MinimumLevel
        DefendFraction = $defendFraction
        DefendEff = $defendEff
    }
}

$paths = @(
    (Join-Path $ClientRoot 'Localization\en_us\Settings\Sys\ItemBaseAttribute.xml'),
    (Join-Path $ClientRoot 'Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml')
)

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\level135plus-armor-rank-thresholds-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path $path) {
        $culture = Split-Path (Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$culture.ItemBaseAttribute.xml") -Force
    }
}

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
