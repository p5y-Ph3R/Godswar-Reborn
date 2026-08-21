[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $PSScriptRoot '..\backups'
}

function Convert-HexBytes([string]$Hex) {
    $value = $Hex -replace '[^0-9A-Fa-f]', ''
    if (($value.Length % 2) -ne 0) {
        throw 'Malformed owner-Merge palette hex.'
    }
    [byte[]]$result = for ($index = 0; $index -lt $value.Length;
        $index += 2) {
        [Convert]::ToByte($value.Substring($index, 2), 16)
    }
    return ,$result
}

function Test-Bytes(
    [byte[]]$Data,
    [int]$Offset,
    [byte[]]$Expected
) {
    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Data.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Get-BytesSha256([byte[]]$Data) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Data)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-SliceSha256(
    [byte[]]$Data,
    [int]$Offset,
    [int]$Length
) {
    if ($Offset -lt 0 -or $Length -lt 0 -or
        $Offset + $Length -gt $Data.Length) {
        throw 'Owner-Merge palette hash slice is out of bounds.'
    }
    [byte[]]$slice = [byte[]]::new($Length)
    [Array]::Copy($Data, $Offset, $slice, 0, $Length)
    Get-BytesSha256 $slice
}

function Get-EffectStructure([byte[]]$Data) {
    $expectedLength = 43083
    $modelLength = 6931
    $textureLength = 35700
    $textureOffset = 16 + $modelLength
    $tailOffset = $textureOffset + $textureLength
    $header = Convert-HexBytes @'
01 00 00 00 00 00 00 00 13 1B 00 00 74 8B 00 00
'@
    $tgaHeader = Convert-HexBytes @'
00 00 0A 00 00 00 00 00 00 00 00 00 80 00 80 00
20 08
'@
    $tgaFooter = Convert-HexBytes @'
00 00 00 00 00 00 00 00 54 52 55 45 56 49 53 49
4F 4E 2D 58 46 49 4C 45 2E 00
'@
    if ($Data.Length -ne $expectedLength -or
        -not (Test-Bytes $Data 0 $header) -or
        -not (Test-Bytes $Data 16 (
            [Text.Encoding]::ASCII.GetBytes('xof 0303bzip0032'))) -or
        -not (Test-Bytes $Data $textureOffset $tgaHeader) -or
        -not (Test-Bytes $Data ($tailOffset - $tgaFooter.Length) $tgaFooter)) {
        throw 'Effect 0002 is not the audited one-model, one-texture GWM layout.'
    }
    if ((Get-SliceSha256 $Data 0 $textureOffset) -ne
            '5982EBE3843583642752D5549F5F034861A2CDBDDB7B74591CF8FB6584B19BAD' -or
        (Get-SliceSha256 $Data $tailOffset ($Data.Length - $tailOffset)) -ne
            '656DC35EAE5514F38DFBE88D8FA1EF0E57B6A019A0D8B523D026DE50C4B1D009') {
        throw 'Effect 0002 geometry, animation, material, or metadata changed.'
    }

    $cursor = $textureOffset + $tgaHeader.Length
    $footerOffset = $tailOffset - $tgaFooter.Length
    $decodedPixels = 0
    $sampleOffsets = [Collections.Generic.List[int]]::new()
    while ($decodedPixels -lt 128 * 128) {
        if ($cursor -ge $footerOffset) {
            throw 'Effect 0002 has a truncated TGA RLE stream.'
        }
        $packet = $Data[$cursor]
        $cursor++
        $pixelCount = ($packet -band 0x7F) + 1
        if ($decodedPixels + $pixelCount -gt 128 * 128) {
            throw 'Effect 0002 has an overrunning TGA RLE packet.'
        }
        $encodedSamples = if (($packet -band 0x80) -ne 0) {
            1
        }
        else { $pixelCount }
        for ($index = 0; $index -lt $encodedSamples; $index++) {
            if ($cursor + 4 -gt $footerOffset) {
                throw 'Effect 0002 has a truncated BGRA sample.'
            }
            $sampleOffsets.Add($cursor)
            $cursor += 4
        }
        $decodedPixels += $pixelCount
    }
    if ($cursor -ne $footerOffset -or $decodedPixels -ne 128 * 128 -or
        $sampleOffsets.Count -ne 8706) {
        throw 'Effect 0002 TGA packet boundaries differ from the audited atlas.'
    }
    [pscustomobject]@{
        TextureOffset = $textureOffset
        TextureLength = $textureLength
        TailOffset = $tailOffset
        SampleOffsets = [int[]]$sampleOffsets.ToArray()
    }
}

function Get-EffectState([byte[]]$Data) {
    $structure = Get-EffectStructure $Data
    $hash = Get-BytesSha256 $Data
    $state = switch ($hash) {
        '89B98361733C4D127CEE984EACD58D7EE1DA098728672B11CB673AA5BA70A2F2' {
            'Stock'
        }
        '7947392068C9FF1ED3C76973C80D37CA6B214493A8EBB90CD1329D4B5DCA7BE9' {
            'Purple'
        }
        default {
            throw "Unsupported or partial effect 0002 palette (SHA-256 $hash)."
        }
    }
    [pscustomobject]@{ State = $state; Hash = $hash; Structure = $structure }
}

function Convert-EffectPalette([byte[]]$Data) {
    $current = Get-EffectState $Data
    [byte[]]$result = $Data.Clone()
    foreach ($offset in $current.Structure.SampleOffsets) {
        # TGA samples are BGRA. Swapping G and R maps cyan to violet while
        # preserving blue, alpha, luminance structure, RLE packets, and size.
        $green = $result[$offset + 1]
        $result[$offset + 1] = $result[$offset + 2]
        $result[$offset + 2] = $green
    }
    $expectedHash = if ($current.State -eq 'Stock') {
        '7947392068C9FF1ED3C76973C80D37CA6B214493A8EBB90CD1329D4B5DCA7BE9'
    }
    else {
        '89B98361733C4D127CEE984EACD58D7EE1DA098728672B11CB673AA5BA70A2F2'
    }
    $converted = Get-EffectState $result
    if ($converted.Hash -ne $expectedHash -or
        $converted.State -eq $current.State) {
        throw 'Generated effect 0002 palette failed exact validation.'
    }
    return ,$result
}

function Assert-OriginClosed([string]$ExePath) {
    $candidate = [IO.Path]::GetFullPath($ExePath)
    $livePaths = @(
        [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe'),
        [IO.Path]::GetFullPath('C:\Godswar Origin B20H\Origin.exe')
    )
    if (@($livePaths | Where-Object {
                [string]::Equals($_, $candidate,
                    [StringComparison]::OrdinalIgnoreCase)
            }).Count -eq 0) {
        return
    }
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try { throw 'Close Origin.exe before changing effect 0002 color.' }
        finally { $process.Dispose() }
    }
}

$root = [IO.Path]::GetFullPath($ClientRoot)
$exePath = Join-Path $root 'Origin.exe'
$effectPath = Join-Path $root (
    'Characters\PetUniteEffect\e_he_0002_all.gwm')
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Origin client was not found: $exePath"
}
if (-not (Test-Path -LiteralPath $effectPath -PathType Leaf)) {
    throw "Effect 0002 was not found: $effectPath"
}

[byte[]]$source = [IO.File]::ReadAllBytes($effectPath)
$current = Get-EffectState $source
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = $current.State
        Color = if ($current.State -eq 'Purple') {
            'Royal purple/violet with lavender-white highlights'
        }
        else { 'Stock cyan/aqua-blue' }
        Hash = $current.Hash
        GeometryAnimationMaterialPreserved = $true
        AlphaAndRlePreserved = $true
        Texture = '128x128 BGRA RLE TGA'
        EncodedSamples = $current.Structure.SampleOffsets.Count
    }
    return
}

Assert-OriginClosed $exePath
$targetState = if ($Mode -eq 'Apply') { 'Purple' } else { 'Stock' }
if ($current.State -eq $targetState) {
    [pscustomobject]@{
        Mode = $Mode
        Status = "Already $targetState"
        Hash = $current.Hash
    }
    return
}

[byte[]]$target = Convert-EffectPalette $source
$targetStatus = Get-EffectState $target
if ($targetStatus.State -ne $targetState) {
    throw "Effect 0002 conversion did not produce $targetState."
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-owner-merge-color-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backupPath = Join-Path $backupDirectory 'e_he_0002_all.gwm'
[IO.File]::WriteAllBytes($backupPath, $source)
if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne
        $current.Hash) {
    throw 'Effect 0002 backup verification failed.'
}

$stage = "$effectPath.$([guid]::NewGuid().ToString('N')).stage"
[IO.File]::WriteAllBytes($stage, $target)
if ((Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash -ne
        $targetStatus.Hash) {
    throw 'Effect 0002 staging verification failed.'
}

try {
    Move-Item -LiteralPath $stage -Destination $effectPath -Force
    [byte[]]$installed = [IO.File]::ReadAllBytes($effectPath)
    $installedStatus = Get-EffectState $installed
    if ($installedStatus.State -ne $targetState -or
        $installedStatus.Hash -ne $targetStatus.Hash) {
        throw 'Installed effect 0002 did not match the audited target.'
    }
}
catch {
    [IO.File]::WriteAllBytes($effectPath, $source)
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Force
    }
    throw
}

[pscustomobject]@{
    Mode = $Mode
    Status = $targetState
    Hash = $targetStatus.Hash
    BackupDirectory = $backupDirectory
    GeometryAnimationMaterialPreserved = $true
    AlphaAndRlePreserved = $true
}
