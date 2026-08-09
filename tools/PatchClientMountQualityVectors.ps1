param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8Bom = [Text.UTF8Encoding]::new($true)
$corePath = Join-Path $PSScriptRoot 'PatchClientMountQualityVectors.Core.ps1'
. $corePath
$nativeQualityCount = $script:NativeMountQualityCount
$targetQualityCount = $script:TargetMountQualityCount
$allowedItemCounts = @(385, 395)
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

function Patch-ItemBaseText([string]$Text, [string]$Locale) {
    $state = @{ Rows = 0; Changed = 0; Vectors = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $familySamples = @{}
    $globalSamples = @{}
    $nativePrefixes = @{}
    foreach ($match in [regex]::Matches($Text, $pattern)) {
        $element = $match.Value
        $type = Get-AttributeValue $element 'Type'
        if ($null -eq $type -or $mountTypes -notcontains $type) {
            continue
        }

        $id = [int](Get-AttributeValue $element 'ID')
        foreach ($name in $qualityVectors) {
            $value = Get-AttributeValue $element $name
            if ($null -eq $value) { continue }
            $parts = @(
                $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
            )
            if ($parts.Count -lt $nativeQualityCount) {
                throw "$Locale mount $id $name has only $($parts.Count) values."
            }
            $nativePrefixes["$id|$name"] = (
                $parts |
                    Select-Object -First $nativeQualityCount |
                    ForEach-Object { $_.Trim() }
            ) -join ','

            if ($type -cne 'mount') {
                continue
            }

            $familyId = [int](($id - ($id % 10)) / 10)
            $sample = [decimal]::Parse(
                $parts[0].Trim(),
                [Globalization.CultureInfo]::InvariantCulture
            )
            $familyKey = "$familyId|$name"
            if (-not $familySamples.ContainsKey($familyKey)) {
                $familySamples[$familyKey] =
                    [Collections.Generic.List[decimal]]::new()
            }
            if (-not $globalSamples.ContainsKey($name)) {
                $globalSamples[$name] =
                    [Collections.Generic.List[decimal]]::new()
            }
            $familySamples[$familyKey].Add($sample)
            $globalSamples[$name].Add($sample)
        }
    }

    $familyDeltas = @{}
    foreach ($key in $familySamples.Keys) {
        $delta = Get-MinimumPositiveDelta @($familySamples[$key])
        if ($delta -gt 0) {
            $familyDeltas[$key] = $delta
        }
    }
    $globalDeltas = @{}
    foreach ($name in $globalSamples.Keys) {
        $delta = Get-MinimumPositiveDelta @($globalSamples[$name])
        if ($delta -gt 0) {
            $globalDeltas[$name] = $delta
        }
    }

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
            $extended = if ($type -ceq 'mount' -and $name -ceq 'Speed') {
                Set-MountSpeedQualityCurve $value $targetQualityCount
            }
            elseif ($type -ceq 'mount') {
                $id = [int](Get-AttributeValue $element 'ID')
                $familyId = [int](($id - ($id % 10)) / 10)
                $familyKey = "$familyId|$name"
                $delta = if ($familyDeltas.ContainsKey($familyKey)) {
                    [decimal]$familyDeltas[$familyKey]
                }
                elseif ($globalDeltas.ContainsKey($name)) {
                    [decimal]$globalDeltas[$name]
                }
                else {
                    [decimal]0
                }
                Extend-FlatMountByFamilyDelta $value $targetQualityCount $delta
            }
            else {
                Extend-ByNativeAverageSlope $value $targetQualityCount
            }
            $updated = Set-AttributeValue $updated $name $extended
        }
        if (-not $foundVector) {
            $id = Get-AttributeValue $element 'ID'
            throw "Mount item $id in $Locale has no quality-indexed stat vector."
        }
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })

    if ($allowedItemCounts -notcontains $state.Rows) {
        throw "Expected a supported mount/mount-gear row count ($($allowedItemCounts -join ' or ')) for $Locale; found $($state.Rows)."
    }

    [xml]$document = $patched
    $xpath = '//*[@Type="mount" or @Type="mounthead" or @Type="mountarmor" or @Type="mountsoul" or @Type="mountornament" or @Type="mountamulet"]'
    $nodes = @($document.SelectNodes($xpath))
    if ($nodes.Count -ne $state.Rows) {
        throw "Mount validation count mismatch for $Locale."
    }
    foreach ($node in $nodes) {
        $foundVector = $false
        foreach ($name in $qualityVectors) {
            if (-not $node.HasAttribute($name)) { continue }
            $foundVector = $true
            $parts = @(
                $node.GetAttribute($name).Split(
                    ',',
                    [StringSplitOptions]::RemoveEmptyEntries
                ) | ForEach-Object { $_.Trim() }
            )
            if ($parts.Count -ne $targetQualityCount) {
                throw "$Locale item $($node.ID) $name has $($parts.Count) entries after patching."
            }

            $nativePrefix = $nativePrefixes["$($node.ID)|$name"]
            $prefix = ($parts | Select-Object -First $nativeQualityCount) -join ','
            if ($node.Type -ceq 'mount' -and $name -ceq 'Speed') {
                $nativeCommon = ($nativePrefix -split ',')[0]
                if ([decimal]$parts[0] -ne [decimal]$nativeCommon) {
                    throw "$Locale mount $($node.ID) Speed changed its Common value."
                }
            }
            elseif ($prefix -cne $nativePrefix) {
                throw "$Locale item $($node.ID) $name changed its native Q1-Q10 prefix."
            }

            $numbers = @(
                $parts |
                    ForEach-Object {
                        [decimal]::Parse(
                            $_,
                            [Globalization.CultureInfo]::InvariantCulture
                        )
                    }
            )
            for ($qualityIndex = 1; $qualityIndex -lt $numbers.Count; $qualityIndex++) {
                if ($numbers[$qualityIndex] -lt $numbers[$qualityIndex - 1]) {
                    $quality = $qualityIndex + 1
                    throw "$Locale item $($node.ID) $name decreases at Q$quality."
                }
            }

            if ($node.Type -ceq 'mount') {
                if ($name -ceq 'Speed') {
                    for ($qualityIndex = 0;
                         $qualityIndex -lt $targetQualityCount;
                         $qualityIndex++) {
                        $expected = $numbers[0] +
                            $script:MountSpeedQualityBonuses[$qualityIndex]
                        if ($numbers[$qualityIndex] -ne $expected) {
                            $quality = $qualityIndex + 1
                            throw "$Locale mount $($node.ID) Speed Q$quality does not match the reviewed additive profile."
                        }
                    }
                }
                elseif (@($numbers[0..9] | Sort-Object -Unique).Count -eq 1) {
                    $id = [int]$node.ID
                    $familyId = [int](($id - ($id % 10)) / 10)
                    $familyKey = "$familyId|$name"
                    $delta = if ($familyDeltas.ContainsKey($familyKey)) {
                        [decimal]$familyDeltas[$familyKey]
                    }
                    elseif ($globalDeltas.ContainsKey($name)) {
                        [decimal]$globalDeltas[$name]
                    }
                    else {
                        [decimal]0
                    }
                    if ($numbers[0] -gt 0 -and
                        $delta -gt 0 -and
                        $numbers[-1] -ne $numbers[0] + $delta) {
                        throw "$Locale mount $($node.ID) $name does not end one family tier above Q1."
                    }
                }
            }
        }
        if (-not $foundVector) {
            throw "$Locale item $($node.ID) has no validated quality vector."
        }
    }

    $mountGearNodes = @($nodes | Where-Object { $_.Type -cne 'mount' })
    foreach ($kind in ($mountGearNodes | Group-Object Type)) {
        $orderedNodes = @($kind.Group | Sort-Object { [int]$_.ID })
        if ($orderedNodes.Count -ne 9) {
            throw "$Locale $($kind.Name) expected 9 level tiers; found $($orderedNodes.Count)."
        }

        foreach ($name in $qualityVectors) {
            $members = @(
                $orderedNodes |
                    Where-Object { $_.HasAttribute($name) } |
                    ForEach-Object {
                        [pscustomobject]@{
                            Id = [int]$_.ID
                            Values = @(
                                $_.GetAttribute($name).Split(
                                    ',',
                                    [StringSplitOptions]::RemoveEmptyEntries
                                ) | ForEach-Object {
                                    [decimal]::Parse(
                                        $_.Trim(),
                                        [Globalization.CultureInfo]::InvariantCulture
                                    )
                                }
                            )
                        }
                    }
            )
            if ($members.Count -eq 0) { continue }
            if ($members.Count -ne $orderedNodes.Count) {
                throw "$Locale $($kind.Name) $name is missing from one or more level tiers."
            }

            for ($memberIndex = 1; $memberIndex -lt $members.Count; $memberIndex++) {
                for (
                    $qualityIndex = $nativeQualityCount;
                    $qualityIndex -lt $targetQualityCount;
                    $qualityIndex++
                ) {
                    if ($members[$memberIndex].Values[$qualityIndex] -lt
                        $members[$memberIndex - 1].Values[$qualityIndex]) {
                        $quality = $qualityIndex + 1
                        throw "$Locale $($kind.Name) $name tier $($members[$memberIndex].Id) is below $($members[$memberIndex - 1].Id) at Q$quality."
                    }
                }
            }
        }
    }

    $mountNodes = @($nodes | Where-Object { $_.Type -ceq 'mount' })
    foreach ($family in ($mountNodes | Group-Object {
                $id = [int]$_.ID
                [int](($id - ($id % 10)) / 10)
            })) {
        foreach ($name in $qualityVectors) {
            $ordered = @(
                $family.Group |
                    Where-Object { $_.HasAttribute($name) } |
                    ForEach-Object {
                        $values = @(
                            $_.GetAttribute($name).Split(
                                ',',
                                [StringSplitOptions]::RemoveEmptyEntries
                            ) | ForEach-Object {
                                [decimal]::Parse(
                                    $_.Trim(),
                                    [Globalization.CultureInfo]::InvariantCulture
                                )
                            }
                        )
                        [pscustomobject]@{
                            Id = [int]$_.ID
                            Quality1 = $values[0]
                            Quality20 = $values[-1]
                        }
                    } |
                    Sort-Object Quality1, Id
            )
            for ($index = 1; $index -lt $ordered.Count; $index++) {
                if ($ordered[$index].Quality20 -lt
                    $ordered[$index - 1].Quality20) {
                    throw "$Locale mount family $($family.Name) $name reverses its authored tier ordering at Q20."
                }
            }
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
