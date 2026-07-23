param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8Bom = [Text.UTF8Encoding]::new($true)
$targetQualityCount = 20
$expectedItemCount = 385
$mountTypes = @(
    'mount',
    'mounthead',
    'mountarmor',
    'mountsoul',
    'mountornament',
    'mountamulet'
)
$qualityVectors = @(
    'Attack', 'AttackRadius', 'AttackSpeed', 'MaxHP', 'MaxMP', 'Defence',
    'MagicAk', 'MagicRec', 'Hit', 'Miss', 'State', 'StateImmunity',
    'AcceptCure', 'Cure', 'PhysicalDamage', 'MagicDamage',
    'PhysicalDamageAbsorb', 'MagicDamageAbsorb', 'Speed', 'FuryAddAk',
    'FuryAddRec', 'InjureImbibe'
)

function Get-AttributeValue([string]$Element, [string]$Name) {
    $match = [regex]::Match(
        $Element,
        ('(?<=\s){0}="([^"]*)"' -f [regex]::Escape($Name))
    )
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Set-AttributeValue([string]$Element, [string]$Name, [string]$Value) {
    $pattern = '(?<=\s){0}="[^"]*"' -f [regex]::Escape($Name)
    $match = [regex]::Match($Element, $pattern)
    if (-not $match.Success) { throw "Required attribute '$Name' is missing." }
    $replacement = $Name + '="' + $Value + '"'
    return $Element.Substring(0, $match.Index) + $replacement +
        $Element.Substring($match.Index + $match.Length)
}

function Extend-ByRepeatingLast([string]$Value, [int]$TargetCount) {
    $parts = @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
    if ($parts.Count -gt $TargetCount) {
        throw "Refusing to shrink a $($parts.Count)-entry mount vector to $TargetCount."
    }
    if ($parts.Count -eq 0) {
        throw 'Cannot extend an empty mount vector.'
    }
    while ($parts.Count -lt $TargetCount) {
        $parts += $parts[-1]
    }
    return ($parts -join ',')
}

function Patch-ItemBaseText([string]$Text, [string]$Locale) {
    $state = @{ Rows = 0; Changed = 0; Vectors = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $type = Get-AttributeValue $element 'Type'
        if ($null -eq $type -or $mountTypes -notcontains $type) {
            return $element
        }

        $state.Rows++
        $updated = $element
        $foundVector = $false
        foreach ($name in $qualityVectors) {
            $value = Get-AttributeValue $updated $name
            if ($null -eq $value) { continue }
            $foundVector = $true
            $state.Vectors++
            $updated = Set-AttributeValue $updated $name (
                Extend-ByRepeatingLast $value $targetQualityCount
            )
        }
        if (-not $foundVector) {
            $id = Get-AttributeValue $element 'ID'
            throw "Mount item $id in $Locale has no quality-indexed stat vector."
        }
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })

    if ($state.Rows -ne $expectedItemCount) {
        throw "Expected $expectedItemCount mount/mount-gear rows for $Locale; found $($state.Rows)."
    }

    [xml]$document = $patched
    $xpath = '//*[@Type="mount" or @Type="mounthead" or @Type="mountarmor" or @Type="mountsoul" or @Type="mountornament" or @Type="mountamulet"]'
    $nodes = @($document.SelectNodes($xpath))
    if ($nodes.Count -ne $expectedItemCount) {
        throw "Mount validation count mismatch for $Locale."
    }
    foreach ($node in $nodes) {
        $foundVector = $false
        foreach ($name in $qualityVectors) {
            if (-not $node.HasAttribute($name)) { continue }
            $foundVector = $true
            $count = @(
                $node.GetAttribute($name).Split(
                    ',',
                    [StringSplitOptions]::RemoveEmptyEntries
                )
            ).Count
            if ($count -ne $targetQualityCount) {
                throw "$Locale item $($node.ID) $name has $count entries after patching."
            }
        }
        if (-not $foundVector) {
            throw "$Locale item $($node.ID) has no validated quality vector."
        }
    }

    return [pscustomobject]@{
        Text = $patched
        Rows = $state.Rows
        ChangedRows = $state.Changed
        Vectors = $state.Vectors
    }
}

function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $rootPath,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

$paths = @{}
$results = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $path = Join-Path $ClientRoot (
        "Localization\$locale\Settings\Sys\ItemBaseAttribute.xml"
    )
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client file was not found: $path"
    }
    $paths[$locale] = $path
    $results[$locale] = Patch-ItemBaseText (
        [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    ) $locale
}

$changedLocales = @(
    $results.Keys | Where-Object { $results[$_].ChangedRows -gt 0 }
)
$backupPath = $null
if ($changedLocales.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot (
        'mount-q20-vectors-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    )
    foreach ($locale in $changedLocales) {
        $source = $paths[$locale]
        $relative = Get-ClientRelativePath $ClientRoot $source
        $destination = Join-Path $backupPath $relative
        $destinationDirectory = Split-Path -Parent $destination
        [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
        [IO.File]::Copy($source, $destination, $false)
    }
    foreach ($locale in $changedLocales) {
        [IO.File]::WriteAllText(
            $paths[$locale],
            $results[$locale].Text,
            $utf8Bom
        )
    }
}

foreach ($locale in @('en_us', 'zh_cn')) {
    $postWrite = Patch-ItemBaseText (
        [IO.File]::ReadAllText($paths[$locale], [Text.Encoding]::UTF8)
    ) $locale
    if ($postWrite.ChangedRows -ne 0) {
        throw "Mount quality-vector patch is not idempotent for $locale."
    }
}

[pscustomobject]@{
    ClientRoot = [IO.Path]::GetFullPath($ClientRoot)
    BackupPath = $backupPath
    EnUsRows = $results.en_us.Rows
    EnUsChangedRows = $results.en_us.ChangedRows
    ZhCnRows = $results.zh_cn.Rows
    ZhCnChangedRows = $results.zh_cn.ChangedRows
    QualityVectorLength = $targetQualityCount
}
