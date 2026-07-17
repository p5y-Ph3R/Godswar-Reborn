param(
    [string]$ClientRoot = "C:\Godswar Origin"
)

$ErrorActionPreference = "Stop"

$labels = [ordered]@{
    "EffLv10" = "10"
    "EffLv11" = "11"
    "EffLv12" = "12"
    "EffLv13" = "13"
    "EffLv14" = "14"
}

function Patch-EquipDescription([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "EquipDescription.dat not found: $path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = [System.Text.Encoding]::Unicode
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = [System.Text.Encoding]::BigEndianUnicode
    } else {
        # Latin-1 is a byte-preserving 1:1 roundtrip for non-UTF8 client data files.
        $encoding = [System.Text.Encoding]::GetEncoding(28591)
    }

    $text = $encoding.GetString($bytes)

    $changed = 0
    foreach ($entry in $labels.GetEnumerator()) {
        $pattern = "(?m)^$([regex]::Escape($entry.Key))`t[^\r\n]*"
        $replacement = "$($entry.Key)`t$($entry.Value)"
        if ([regex]::IsMatch($text, $pattern)) {
            $text = [regex]::Replace($text, $pattern, $replacement, 1)
            $changed++
            continue
        }

        $rankNumber = 0
        if (-not [int]::TryParse(($entry.Key -replace '^EffLv', ''), [ref]$rankNumber)) {
            continue
        }

        $previousKey = "EffLv$($rankNumber - 1)"
        $previousPattern = "(?m)^$([regex]::Escape($previousKey))`t[^\r\n]*(\r?\n)?"
        $previousMatch = [regex]::Match($text, $previousPattern)
        if ($previousMatch.Success) {
            $newline = if ($previousMatch.Groups[1].Success -and $previousMatch.Groups[1].Value.Length -gt 0) {
                $previousMatch.Groups[1].Value
            } else {
                "`r`n"
            }
            $inserted = $previousMatch.Value
            if (-not $inserted.EndsWith("`r`n") -and -not $inserted.EndsWith("`n")) {
                $inserted += $newline
            }

            $inserted += "$($entry.Key)`t$($entry.Value)$newline"
            $text = $text.Remove($previousMatch.Index, $previousMatch.Length).Insert($previousMatch.Index, $inserted)
            $changed++
            continue
        }

        $text = $text.TrimEnd("`r", "`n") + "`r`n$($entry.Key)`t$($entry.Value)`r`n"
        $changed++
    }

    [System.IO.File]::WriteAllBytes($path, $encoding.GetBytes($text))
    [pscustomobject]@{
        Path = $path
        PatchedRows = $changed
    }
}

$paths = @(
    (Join-Path $ClientRoot "Localization\en_us\Text\EquipDescription.dat"),
    (Join-Path $ClientRoot "Localization\zh_cn\Text\EquipDescription.dat")
)

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path (Resolve-Path ".").Path "backups\equip-description-rank-labels-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path) {
        $locale = Split-Path (Split-Path (Split-Path $path -Parent) -Parent) -Leaf
        Copy-Item -LiteralPath $path -Destination (Join-Path $backupRoot "$locale.EquipDescription.dat") -Force
    }
}

$paths | ForEach-Object { Patch-EquipDescription $_ }
