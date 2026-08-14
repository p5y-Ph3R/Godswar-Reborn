function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Utf8BomBytes([string]$Text) {
    $content = $Text.TrimStart([char]0xFEFF)
    return [byte[]]($encoding.GetPreamble() + $encoding.GetBytes($content))
}

function Get-LayoutRectangle([string]$Text, [string]$ElementName) {
    $pattern = '<' + [regex]::Escape($ElementName) +
        '\b[^>]*Rectangle="(?<x1>\d+)\s*,\s*(?<y1>\d+)\s*,\s*' +
        '(?<x2>\d+)\s*,\s*(?<y2>\d+)"'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        throw "Pet Growth layout has no $ElementName rectangle."
    }
    $x1 = [int]$match.Groups['x1'].Value
    $y1 = [int]$match.Groups['y1'].Value
    $x2 = [int]$match.Groups['x2'].Value
    $y2 = [int]$match.Groups['y2'].Value
    return [pscustomobject]@{
        Width = $x2 - $x1
        Height = $y2 - $y1
    }
}

function Assert-SupportedLayout([string]$Path, [string]$Locale) {
    $hash = Get-FileSha256 $Path
    if ($hash -ne $supportedLayoutSha256) {
        throw "Unsupported $Locale Pet Growth layout SHA-256: $hash"
    }

    $text = [IO.File]::ReadAllText($Path, $encoding)
    $window = Get-LayoutRectangle $text 'FirstWin'
    $ok = Get-LayoutRectangle $text 'FirstWin_ButtonA1'
    $cancel = Get-LayoutRectangle $text 'FirstWin_ButtonA2'
    $reset = Get-LayoutRectangle $text 'FirstWin_ButtonA3'
    if ($window.Width -ne 600 -or $window.Height -ne 290 -or
        $ok.Width -ne 60 -or $ok.Height -ne 27 -or
        $cancel.Width -ne 60 -or $cancel.Height -ne 27 -or
        $reset.Width -ne 152 -or $reset.Height -ne 27 -or
        $phoenixResetX + $reset.Width -ge $okX -or
        $okX + $ok.Width -ge $cancelX -or
        $cancelX + $cancel.Width -gt $window.Width -or
        $phoenixResetY + $reset.Height -gt $window.Height -or
        $okY + $ok.Height -gt $window.Height -or
        $cancelY + $cancel.Height -gt $window.Height) {
        throw "Unsupported $Locale Pet Growth dialogue geometry."
    }
    return $hash
}

function Assert-ClientClosed([string]$Root) {
    $origin = Join-Path $Root 'Origin.exe'
    $running = @(Get-Process Origin -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                [string]::Equals(
                    $_.Path,
                    $origin,
                    [StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $true
            }
        })
    if ($running.Count -gt 0) {
        throw 'Origin.exe is running. Close it before patching dialogue files.'
    }
}
