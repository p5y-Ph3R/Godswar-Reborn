[CmdletBinding()]
param(
    [string]$AssetRoot = '',
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($AssetRoot)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $AssetRoot = Join-Path $repositoryRoot 'assets\holy-spirits'
}

$assetRootPath = [IO.Path]::GetFullPath($AssetRoot)
$manifestPath = Join-Path $assetRootPath 'manifest.json'
$sourceRoot = Join-Path $assetRootPath 'source'
$iconRoot = Join-Path $assetRootPath 'icons'
$contactSheetPath = Join-Path $assetRootPath 'contact-sheet.png'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Holy Spirit icon manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json
if ($manifest.schema_version -ne 1) {
    throw "Unsupported Holy Spirit icon manifest version: $($manifest.schema_version)"
}

$spriteWidth = [int]$manifest.sprite_width
$spriteHeight = [int]$manifest.sprite_height
if ($spriteWidth -ne 36 -or $spriteHeight -ne 36) {
    throw "The stock client requires 36x36 Holy Spirit icons."
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
        Hash.ToLowerInvariant()
}

function New-ScaledBitmap {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height
    )

    $source = [Drawing.Image]::FromFile($SourcePath)
    try {
        $target = [Drawing.Bitmap]::new(
            $Width,
            $Height,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($target)
        try {
            $graphics.CompositingMode =
                [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality =
                [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode =
                [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode =
                [Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode =
                [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $Width, $Height)
        }
        finally {
            $graphics.Dispose()
        }
        return $target
    }
    finally {
        $source.Dispose()
    }
}

function Test-BitmapPixelsEqual {
    param(
        [Parameter(Mandatory)][Drawing.Bitmap]$Expected,
        [Parameter(Mandatory)][string]$ActualPath
    )

    if (-not (Test-Path -LiteralPath $ActualPath -PathType Leaf)) {
        return $false
    }

    $actual = [Drawing.Bitmap]::new($ActualPath)
    try {
        if ($actual.Width -ne $Expected.Width -or
            $actual.Height -ne $Expected.Height) {
            return $false
        }

        for ($y = 0; $y -lt $Expected.Height; $y++) {
            for ($x = 0; $x -lt $Expected.Width; $x++) {
                if ($actual.GetPixel($x, $y).ToArgb() -ne
                    $Expected.GetPixel($x, $y).ToArgb()) {
                    return $false
                }
            }
        }
        return $true
    }
    finally {
        $actual.Dispose()
    }
}

function Save-PngAtomically {
    param(
        [Parameter(Mandatory)][Drawing.Bitmap]$Bitmap,
        [Parameter(Mandatory)][string]$Path
    )

    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp.png" -f
            ([IO.Path]::GetFileName($Path)),
            ([Guid]::NewGuid().ToString('N')))
    try {
        $Bitmap.Save($temporaryPath, [Drawing.Imaging.ImageFormat]::Png)
        $verification = [Drawing.Bitmap]::new($temporaryPath)
        try {
            if ($verification.Width -ne $Bitmap.Width -or
                $verification.Height -ne $Bitmap.Height) {
                throw "Prepared PNG failed dimension validation: $Path"
            }
        }
        finally {
            $verification.Dispose()
        }
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function New-ContactSheet {
    param([Parameter(Mandatory)][object[]]$PreparedEntries)

    $columns = 5
    $tileWidth = 200
    $tileHeight = 190
    $rows = [Math]::Ceiling($PreparedEntries.Count / $columns)
    $sheet = [Drawing.Bitmap]::new(
        $columns * $tileWidth,
        $rows * $tileHeight,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($sheet)
    $font = [Drawing.Font]::new('Segoe UI', 10)
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(245, 238, 220))
    $format = [Drawing.StringFormat]::new()
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(20, 17, 27))
        $graphics.TextRenderingHint =
            [Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        $graphics.InterpolationMode =
            [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $format.Alignment = [Drawing.StringAlignment]::Center
        $format.LineAlignment = [Drawing.StringAlignment]::Near

        for ($index = 0; $index -lt $PreparedEntries.Count; $index++) {
            $entry = $PreparedEntries[$index]
            $column = $index % $columns
            $row = [Math]::Floor($index / $columns)
            $left = $column * $tileWidth
            $top = $row * $tileHeight
            $graphics.DrawImage(
                $entry.Bitmap,
                $left + 28,
                $top + 8,
                144,
                144)
            $labelBounds = [Drawing.RectangleF]::new(
                $left + 5,
                $top + 155,
                $tileWidth - 10,
                32)
            $graphics.DrawString(
                [string]$entry.DisplayName,
                $font,
                $brush,
                $labelBounds,
                $format)
        }
        return $sheet
    }
    finally {
        $format.Dispose()
        $brush.Dispose()
        $font.Dispose()
        $graphics.Dispose()
    }
}

$preparedEntries = [Collections.Generic.List[object]]::new()
try {
    foreach ($entry in $manifest.entries) {
        $sourcePath = Join-Path $sourceRoot ($entry.slug + '.png')
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Holy Spirit source image is missing: $sourcePath"
        }
        $actualSourceHash = Get-Sha256 -Path $sourcePath
        if ($actualSourceHash -ne [string]$entry.source_sha256) {
            throw "Source SHA256 mismatch for $($entry.slug): $actualSourceHash"
        }

        $bitmap = New-ScaledBitmap `
            -SourcePath $sourcePath `
            -Width $spriteWidth `
            -Height $spriteHeight
        $iconPath = Join-Path $iconRoot ($entry.slug + '-36.png')

        if ($Check) {
            if (-not (Test-BitmapPixelsEqual -Expected $bitmap -ActualPath $iconPath)) {
                throw "Prepared icon differs from its source: $iconPath"
            }
            Write-Host "Verified $($entry.slug): $(Get-Sha256 -Path $iconPath)"
        }
        else {
            Save-PngAtomically -Bitmap $bitmap -Path $iconPath
            Write-Host "Prepared $($entry.slug): $(Get-Sha256 -Path $iconPath)"
        }

        $preparedEntries.Add([pscustomobject]@{
            DisplayName = [string]$entry.display_name
            Bitmap = $bitmap
        })
    }

    if (-not $Check) {
        $sheet = New-ContactSheet -PreparedEntries $preparedEntries.ToArray()
        try {
            Save-PngAtomically -Bitmap $sheet -Path $contactSheetPath
        }
        finally {
            $sheet.Dispose()
        }
        Write-Host "Prepared contact sheet: $contactSheetPath"
    }
    elseif (-not (Test-Path -LiteralPath $contactSheetPath -PathType Leaf)) {
        throw "Holy Spirit contact sheet is missing: $contactSheetPath"
    }
}
finally {
    foreach ($entry in $preparedEntries) {
        $entry.Bitmap.Dispose()
    }
}
