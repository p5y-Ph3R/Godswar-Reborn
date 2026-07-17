param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int]$MinimumLevel = 135
)

$ErrorActionPreference = "Stop"

$qualityScoreProfile = '0,8,18,28,40,54,74,100,140,200,230,260,295,330,370,410,455,500,550,600'
$zeroEffectProfile = '0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0'

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

        $line = Set-AttributeValue $line 'DefendFraction' $qualityScoreProfile
        $line = Set-AttributeValue $line 'DefendEff' $zeroEffectProfile
        $changed++
        $line
    }

    Set-Content -LiteralPath $path -Value $patched -Encoding UTF8
    [pscustomobject]@{
        Path = $path
        PatchedRows = $changed
        MinimumLevel = $MinimumLevel
        DefendFraction = $qualityScoreProfile
        DefendEff = $zeroEffectProfile
    }
}

$paths = @(
    (Join-Path $ClientRoot 'Localization\en_us\Settings\Sys\ItemBaseAttribute.xml'),
    (Join-Path $ClientRoot 'Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml')
)

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\level135plus-helmet-rank-contribution-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path $path) {
        $culture = Split-Path (Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$culture.ItemBaseAttribute.xml") -Force
    }
}

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
