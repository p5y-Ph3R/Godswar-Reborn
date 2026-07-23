param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$BackupRoot = "C:\Reborn\backups"
)

$ErrorActionPreference = "Stop"
$utf8Bom = [Text.UTF8Encoding]::new($true)
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
$expectedAttributeCount = 195
$gradeProfile = @(
    100, 116, 133, 151, 170, 190, 211, 233,
    256, 280, 305, 332, 365, 400
)

function Get-DecimalScale([string]$value) {
    $parts = $value.Split('.', 2)
    if ($parts.Count -lt 2) {
        return 0
    }

    return $parts[1].TrimEnd('0').Length
}

function Format-DecimalValue([decimal]$value, [int]$scale, [bool]$integerOnly) {
    if ($integerOnly) {
        return ([decimal]::Round($value, 0, [System.MidpointRounding]::AwayFromZero)).ToString("0", $invariantCulture)
    }

    $format = "0." + ("#" * [Math]::Max($scale, 1))
    return $value.ToString($format, $invariantCulture)
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

function Update-AttributeLine([string]$line) {
    $matches = [regex]::Matches($line, '\sL(\d+)="([^"]+)"')
    if ($matches.Count -lt 12) {
        return $line
    }

    $levels = @{}
    foreach ($match in $matches) {
        $level = [int]$match.Groups[1].Value
        $levels[$level] = $match.Groups[2].Value
    }

    for ($level = 1; $level -le 12; $level++) {
        if (-not $levels.ContainsKey($level)) {
            return $line
        }
    }

    $integerOnly = $true
    $maxScale = 0
    for ($level = 1; $level -le 25; $level++) {
        if (-not $levels.ContainsKey($level)) {
            continue
        }

        $scale = Get-DecimalScale $levels[$level]
        $maxScale = [Math]::Max($maxScale, $scale)
        if ($scale -gt 0) {
            $integerOnly = $false
        }
    }

    $maxScale = if ($integerOnly) { 0 } else { [Math]::Max($maxScale + 2, 6) }
    $anchor = [decimal]::Parse($levels[12], $invariantCulture)
    $previous = $anchor
    for ($level = 13; $level -le 25; $level++) {
        $profile = [decimal]$gradeProfile[$level - 12]
        $value = $anchor * ($profile / [decimal]100)

        # Some native tails deliberately grow faster than the ordinary
        # G12-to-G25 score profile. Never weaken an authored value, and keep
        # the resulting extension monotonic even when native rounding has a
        # short plateau.
        if ($levels.ContainsKey($level)) {
            $authored = [decimal]::Parse($levels[$level], $invariantCulture)
            $value = [Math]::Max($value, $authored)
        }
        $value = [Math]::Max($value, $previous)

        $formatted = Format-DecimalValue $value $maxScale $integerOnly
        $levels[$level] = $formatted
        $previous = [decimal]::Parse($formatted, $invariantCulture)
    }

    return [regex]::Replace(
        $line,
        '\sL(\d+)="([^"]+)"',
        {
            param($match)
            $level = [int]$match.Groups[1].Value
            if ($levels.ContainsKey($level)) {
                return ' L' + $level.ToString($invariantCulture) + '="' + $levels[$level] + '"'
            }

            return $match.Value
        })
}

$paths = @(
    Join-Path $ClientRoot "Localization\en_us\Settings\Sys\ItemAppendAttribute.xml"
    Join-Path $ClientRoot "Localization\zh_cn\Settings\Sys\ItemAppendAttribute.xml"
)

$results = @{}
foreach ($path in $paths) {
    if (-not (Test-Path $path)) {
        throw "ItemAppendAttribute.xml not found: $path"
    }

    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $lines = $text -split '(?<=\n)'
    $updatedLines = foreach ($line in $lines) {
        Update-AttributeLine $line
    }
    $updated = $updatedLines -join ''
    [xml]$originalDocument = $text
    [xml]$document = $updated
    $nodes = @($document.SelectNodes('/ItemAppendAttribute/*[@ID]'))
    if ($nodes.Count -ne $expectedAttributeCount) {
        throw "Expected $expectedAttributeCount ItemAppendAttribute rows in $path; found $($nodes.Count)."
    }
    foreach ($node in $nodes) {
        $originalNode = $originalDocument.SelectSingleNode(
            "/ItemAppendAttribute/*[@ID='$($node.ID)']"
        )
        if ($null -eq $originalNode) {
            throw "Attribute $($node.ID) did not exist before the progression update."
        }

        $previous = $null
        for ($level = 1; $level -le 25; $level++) {
            if (-not $node.HasAttribute("L$level")) {
                throw "Attribute $($node.ID) lacks L$level after progression update."
            }

            if ($level -lt 13) {
                if ($level -eq 12) {
                    $previous = [decimal]::Parse(
                        $node.GetAttribute("L12"),
                        $invariantCulture
                    )
                }
                continue
            }

            $value = [decimal]::Parse($node.GetAttribute("L$level"), $invariantCulture)
            if ($originalNode.HasAttribute("L$level")) {
                $authored = [decimal]::Parse(
                    $originalNode.GetAttribute("L$level"),
                    $invariantCulture
                )
                if ($value -lt $authored) {
                    throw "Attribute $($node.ID) L$level regressed from $authored to $value."
                }
            }
            if ($null -ne $previous -and $value -lt $previous) {
                throw "Attribute $($node.ID) decreases from L$($level - 1) to L$level."
            }
            $previous = $value
        }
    }
    $results[$path] = [pscustomobject]@{
        Original = $text
        Updated = $updated
        Rows = $nodes.Count
        Changed = $text -cne $updated
    }
}

$changedPaths = @(
    $paths | Where-Object { $results[$_].Changed }
)
$backupPath = $null
if ($changedPaths.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot (
        'item-append-g25-profile-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    )
    foreach ($path in $changedPaths) {
        $relative = Get-ClientRelativePath $ClientRoot $path
        $destination = Join-Path $backupPath $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
            Out-Null
        [IO.File]::Copy($path, $destination, $false)
    }
    foreach ($path in $changedPaths) {
        [IO.File]::WriteAllText($path, $results[$path].Updated, $utf8Bom)
    }
}

foreach ($path in $paths) {
    $written = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $lines = $written -split '(?<=\n)'
    $secondPass = (@($lines | ForEach-Object { Update-AttributeLine $_ }) -join '')
    if ($written -cne $secondPass) {
        throw "Attribute progression update is not idempotent: $path"
    }
}

[pscustomobject]@{
    ClientRoot = [IO.Path]::GetFullPath($ClientRoot)
    BackupPath = $backupPath
    ChangedFiles = $changedPaths.Count
    EnUsRows = $results[$paths[0]].Rows
    ZhCnRows = $results[$paths[1]].Rows
    Grade25Profile = $gradeProfile[-1]
}
