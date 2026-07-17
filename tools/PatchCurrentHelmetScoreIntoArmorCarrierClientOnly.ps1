param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [int[]]$CarrierTemplateIds = @(2144, 2244),
    [int]$GradeLevel = 12,
    [int]$CarrierGradeScore = 700
)

$ErrorActionPreference = "Stop"

$targetIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$CarrierTemplateIds | ForEach-Object { [void]$targetIds.Add([string]$_) }

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

function Set-ListValue([string]$csv, [int]$oneBasedIndex, [int]$value) {
    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($part in ($csv -split ',')) {
        $parts.Add($part)
    }

    while ($parts.Count -lt $oneBasedIndex) {
        $parts.Add('0')
    }

    $parts[$oneBasedIndex - 1] = [string]$value
    return $parts -join ','
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

        $appFraction = Get-AttributeValue $line 'AppFraction'
        if ([string]::IsNullOrWhiteSpace($appFraction)) {
            $line
            continue
        }

        $newAppFraction = Set-ListValue -csv $appFraction -oneBasedIndex $GradeLevel -value $CarrierGradeScore
        $line = Set-AttributeValue -line $line -name 'AppFraction' -value $newAppFraction
        $changed++
        $line
    }

    Set-Content -LiteralPath $path -Value $patched -Encoding UTF8
    [pscustomobject]@{
        Path = $path
        PatchedRows = $changed
        CarrierTemplateIds = ($CarrierTemplateIds -join ',')
        GradeLevel = $GradeLevel
        CarrierGradeScore = $CarrierGradeScore
    }
}

$paths = @(
    (Join-Path $ClientRoot 'Localization\en_us\Settings\Sys\ItemBaseAttribute.xml'),
    (Join-Path $ClientRoot 'Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml')
)

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path (Resolve-Path '.').Path "backups\current-helmet-score-carrier-client-only-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path $path) {
        $culture = Split-Path (Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$culture.ItemBaseAttribute.xml") -Force
    }
}

$paths | ForEach-Object { Patch-ItemBaseAttribute $_ }
