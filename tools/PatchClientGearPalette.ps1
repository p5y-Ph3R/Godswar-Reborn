[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'backups'),
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$helperRoot = Join-Path $PSScriptRoot 'PatchClientGearPalette'
. (Join-Path $helperRoot 'Palette.ps1')
. (Join-Path $helperRoot 'Transforms.ps1')

$utf8Bom = [Text.UTF8Encoding]::new($true)
$gb2312 = [Text.Encoding]::GetEncoding(936)

function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $rootPath,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Client palette path is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

function Assert-ExpectedEncoding([string]$Path, [string]$Kind) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 3) {
        throw "Client palette file is unexpectedly short: $Path"
    }
    $hasUtf8Bom = $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $hasUtf16Bom = ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
        ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF)

    if ($Kind -eq 'ItemColor' -and ($hasUtf8Bom -or $hasUtf16Bom)) {
        throw "ItemColor.xml must remain BOM-less GB2312: $Path"
    }
    if ($Kind -eq 'Font' -and -not $hasUtf8Bom) {
        throw "font.lua must remain UTF-8 with BOM: $Path"
    }
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

Assert-PaletteDefinitions
$resolvedClientRoot = (Resolve-Path -LiteralPath $ClientRoot).Path

if ($Apply) {
    $originPath = [IO.Path]::GetFullPath((Join-Path $resolvedClientRoot 'Origin.exe'))
    $runningOrigin = @(Get-Process -Name Origin -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                [IO.Path]::GetFullPath($_.Path) -ieq $originPath
            }
            catch { $false }
        })
    if ($runningOrigin.Count -gt 0) {
        throw 'Origin.exe is running. Close this client before applying its palette.'
    }
}

$files = [Collections.Generic.List[hashtable]]::new()
foreach ($locale in @('en_us', 'zh_cn')) {
    $localeRoot = Join-Path $resolvedClientRoot "Localization\$locale"
    $files.Add(@{
            Locale = $locale
            Kind = 'ItemColor'
            Path = Join-Path $localeRoot 'Settings\Sys\ItemColor.xml'
            Encoding = $gb2312
        })
    $files.Add(@{
            Locale = $locale
            Kind = 'Font'
            Path = Join-Path $localeRoot 'UI\Base\font.lua'
            Encoding = $utf8Bom
        })
}

foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.Path -PathType Leaf)) {
        throw "Required client palette file was not found: $($file.Path)"
    }
    Assert-ExpectedEncoding $file.Path $file.Kind
    $file.Text = [IO.File]::ReadAllText($file.Path, $file.Encoding)
    $file.Sha256 = Get-FileSha256 $file.Path
    $file.Output = if ($file.Kind -eq 'ItemColor') {
        Convert-ItemColorPaletteText $file.Text $file.Locale
    }
    else {
        Convert-FontPaletteText $file.Text $file.Locale
    }

    $secondPass = if ($file.Kind -eq 'ItemColor') {
        Convert-ItemColorPaletteText $file.Output $file.Locale
    }
    else {
        Convert-FontPaletteText $file.Output $file.Locale
    }
    if ($secondPass -cne $file.Output) {
        throw "$($file.Kind) transform is not idempotent for $($file.Locale)."
    }
}

$changedFiles = @($files | Where-Object { $_.Output -cne $_.Text })
$backupPath = $null
if ($Apply -and $changedFiles.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot (
        'client-gear-palette-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    )
    [IO.Directory]::CreateDirectory($backupPath) | Out-Null

    foreach ($file in $changedFiles) {
        $relative = Get-ClientRelativePath $resolvedClientRoot $file.Path
        $destination = Join-Path $backupPath $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
            Out-Null
        Copy-Item -LiteralPath $file.Path -Destination $destination
        $file.BackupPath = $destination
    }

    try {
        foreach ($file in $changedFiles) {
            if ((Get-FileSha256 $file.Path) -cne $file.Sha256) {
                throw "Client palette file changed during staging: $($file.Path)"
            }
        }
        foreach ($file in $changedFiles) {
            [IO.File]::WriteAllText(
                $file.Path,
                $file.Output,
                $file.Encoding
            )
        }

        foreach ($file in $files) {
            Assert-ExpectedEncoding $file.Path $file.Kind
            $writtenText = [IO.File]::ReadAllText($file.Path, $file.Encoding)
            $validated = if ($file.Kind -eq 'ItemColor') {
                Convert-ItemColorPaletteText $writtenText $file.Locale
            }
            else {
                Convert-FontPaletteText $writtenText $file.Locale
            }
            if ($validated -cne $writtenText) {
                throw "Post-write validation is not idempotent: $($file.Path)"
            }
        }
    }
    catch {
        $writeError = $_
        foreach ($file in $changedFiles) {
            if ($file.ContainsKey('BackupPath') -and
                (Test-Path -LiteralPath $file.BackupPath -PathType Leaf)) {
                Copy-Item -LiteralPath $file.BackupPath `
                    -Destination $file.Path -Force
            }
        }
        throw $writeError
    }
}

[pscustomobject]@{
    Mode = $(if ($Apply) { 'Apply' } else { 'Plan' })
    ClientRoot = $resolvedClientRoot
    WouldChangeFiles = $changedFiles.Count
    ChangedFiles = $(if ($Apply) { $changedFiles.Count } else { 0 })
    BackupPath = $backupPath
    QualityMappings = $script:QualityPalette.Count
    GradeMappings = $script:GradePalette.Count
    ElementalSentinelsPreserved = $script:ElementalSentinels.Count
}
