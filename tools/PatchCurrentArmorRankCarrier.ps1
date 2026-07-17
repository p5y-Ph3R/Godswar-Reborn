param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int[]]$TemplateIds = @(2144, 2244),
    [int]$DisplayedScore = 7450,
    [int]$NextScore = 8000
)

$ErrorActionPreference = "Stop"

$defendFraction = "330,475,750,950,1350,1720,2225,3860,$DisplayedScore,$NextScore"
$defendEff = '1,2,3,4,5,6,7,8,9,10'
$targetIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$TemplateIds | ForEach-Object { [void]$targetIds.Add([string]$_) }

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

function Test-TargetItem([string]$line) {
    $id = Get-AttributeValue $line 'ID'
    return $null -ne $id -and $targetIds.Contains($id)
}

function Patch-ItemBaseAttribute([string]$path) {
    if (-not (Test-Path $path)) {
        throw "ItemBaseAttribute.xml not found: $path"
    }

    $lines = Get-Content -LiteralPath $path
    $changed = 0
    $patched = foreach ($line in $lines) {
        if (-not (Test-TargetItem $line)) {
            $line
            continue
        }

        $line = Set-AttributeValue $line 'DefendFraction' $defendFraction
        $line = Set-AttributeValue $line 'DefendEff' $defendEff
        $changed++
        $line
    }

    Set-Content -LiteralPath $path -Value $patched -Encoding UTF8
    [pscustomobject]@{
        Path = $path
        PatchedRows = $changed
        TemplateIds = ($TemplateIds -join ',')
        DefendFraction = $defendFraction
        DefendEff = $defendEff
    }
}

$paths = @(
    (Join-Path $ClientRoot 'Localization\en_us\Settings\Sys\ItemBaseAttribute.xml'),
    (Join-Path $ClientRoot 'Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml')
)

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\current-armor-rank-carrier-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path $path) {
        $culture = Split-Path (Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$culture.ItemBaseAttribute.xml") -Force
    }
}

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
