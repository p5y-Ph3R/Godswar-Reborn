param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [decimal]$GapGrowth = 0.25
)

$ErrorActionPreference = "Stop"
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

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
    for ($level = 1; $level -le 12; $level++) {
        $scale = Get-DecimalScale $levels[$level]
        $maxScale = [Math]::Max($maxScale, $scale)
        if ($scale -gt 0) {
            $integerOnly = $false
        }
    }

    $maxScale = if ($integerOnly) { 0 } else { [Math]::Max($maxScale + 2, 5) }
    $previous = [decimal]::Parse($levels[12], $invariantCulture)
    $baseGap = [decimal]::Parse($levels[12], $invariantCulture) - [decimal]::Parse($levels[11], $invariantCulture)

    for ($level = 13; $level -le 25; $level++) {
        $gapMultiplier = 1 + ($GapGrowth * ($level - 12))
        $previous += $baseGap * $gapMultiplier
        $levels[$level] = Format-DecimalValue $previous $maxScale $integerOnly
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

foreach ($path in $paths) {
    if (-not (Test-Path $path)) {
        throw "ItemAppendAttribute.xml not found: $path"
    }

    $backup = "$path.pre-progressive-l13.bak"
    if (-not (Test-Path $backup)) {
        Copy-Item -LiteralPath $path -Destination $backup
    }

    $lines = Get-Content -LiteralPath $path
    $updated = foreach ($line in $lines) {
        Update-AttributeLine $line
    }

    Set-Content -LiteralPath $path -Value $updated -Encoding UTF8
    Write-Host "Updated $path"
}
