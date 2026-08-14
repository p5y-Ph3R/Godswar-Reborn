[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Convert-HexBytes([string]$Hex) {
    $compact = $Hex -replace '[^0-9A-Fa-f]', ''
    if (($compact.Length % 2) -ne 0) { throw 'Malformed native-patch hex.' }
    [byte[]]$result = for ($index = 0; $index -lt $compact.Length;
        $index += 2) {
        [Convert]::ToByte($compact.Substring($index, 2), 16)
    }
    return ,$result
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Data.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Copy-Bytes([byte[]]$Source, [byte[]]$Destination, [int]$Offset) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-PeMetadata([byte[]]$Data) {
    if ($Data.Length -lt 0x100 -or $Data[0] -ne 0x4D -or
        $Data[1] -ne 0x5A) {
        throw 'Origin.exe does not have a valid DOS header.'
    }
    $peOffset = [BitConverter]::ToInt32($Data, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 24 -gt $Data.Length -or
        [BitConverter]::ToUInt32($Data, $peOffset) -ne 0x00004550) {
        throw 'Origin.exe does not have a valid PE header.'
    }
    $optionalSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $sectionTable = $optionalOffset + $optionalSize
    if ($sectionCount -le 0 -or
        $sectionTable + $sectionCount * 40 -gt $Data.Length) {
        throw 'Origin.exe section table is invalid.'
    }
    $sections = @()
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount;
        $sectionIndex++) {
        $offset = $sectionTable + $sectionIndex * 40
        $name = [Text.Encoding]::ASCII.GetString(
            $Data[$offset..($offset + 7)]).Trim([char]0)
        $sections += [pscustomobject]@{
            Name = $name
            VirtualAddress = [BitConverter]::ToUInt32($Data, $offset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $offset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $offset + 20)
            Characteristics = [BitConverter]::ToUInt32($Data, $offset + 36)
        }
    }
    [pscustomobject]@{
        Machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
        Characteristics = [BitConverter]::ToUInt16($Data, $peOffset + 22)
        OptionalMagic = [BitConverter]::ToUInt16($Data, $optionalOffset)
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalOffset + 28)
        DllCharacteristics =
            [BitConverter]::ToUInt16($Data, $optionalOffset + 70)
        Sections = $sections
    }
}

function Resolve-ExecutableVa(
    [object]$Pe,
    [int]$FileOffset,
    [int]$Length,
    [string]$ExpectedSection
) {
    foreach ($section in $Pe.Sections) {
        if ($FileOffset -lt $section.RawOffset -or
            $FileOffset + $Length -gt $section.RawOffset + $section.RawSize) {
            continue
        }
        if ($section.Name -ne $ExpectedSection -or
            ($section.Characteristics -band 0x20000000) -eq 0) {
            throw "Native range is not in executable $ExpectedSection."
        }
        return [uint64]$Pe.ImageBase + $section.VirtualAddress +
            ([uint64]$FileOffset - $section.RawOffset)
    }
    throw 'Native range is outside an audited PE section.'
}

function Get-NearTarget([byte[]]$Code, [int]$Offset, [uint64]$Va) {
    [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-ShortTarget([byte[]]$Code, [int]$Offset) {
    $Offset + 2 + [int][sbyte]$Code[$Offset + 1]
}

function Assert-ClientClosed([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $live = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            try { $processPath = $process.Path } catch { $processPath = $null }
            $matches = $processPath -and [string]::Equals(
                [IO.Path]::GetFullPath($processPath), $resolved,
                [StringComparison]::OrdinalIgnoreCase)
            $hiddenLive = -not $processPath -and [string]::Equals(
                $resolved, $live, [StringComparison]::OrdinalIgnoreCase)
            if ($matches -or $hiddenLive) {
                throw 'Close Origin.exe before changing Merge guidance.'
            }
        }
        finally { $process.Dispose() }
    }
}

$expectedLength = 6676480
$sourceSha256 =
    '39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B'
# Filled from the exact staged output below and pinned by the disposable test.
$patchedSha256 =
    'F8D832D97A1C910AF31645DBD8B6FC2BDADF4AD30196470553A8668DB81A1D17'

$hookOffset = 0x16EF44
$hookVa = 0x0056EF44
$caveOffset = 0x5C320F
$caveVa = 0x009C320F
$caveReserveLength = 97
$continuationVa = 0x0056EF4B
$lookupCallVa = 0x0049E8C0

$originalHook = Convert-HexBytes '50 51 E8 75 F9 F2 FF'
$patchedHook = Convert-HexBytes 'E9 C6 42 45 00 90 90'
$caveCode = Convert-HexBytes @'
51 BA 60 F0 FF FF 2B D0 0F B6 4E 3C 80 F9 02 74
0F 80 F9 03 74 0A 80 F9 06 74 05 80 F9 0A 75 03
83 C2 0A 6B D2 0A 8D 54 3A 01 F7 DA 89 54 24 20
59 50 51 B8 C0 E8 49 00 FF D0 68 4B EF 56 00 C3
'@
$emptyCave = [byte[]]::new($caveReserveLength)
$patchedCave = [byte[]]::new($caveReserveLength)
Copy-Bytes $caveCode $patchedCave 0

$formulaPrefix = Convert-HexBytes @'
0F B6 88 94 00 00 00 8B 84 BE 9C 00 00 00 33 D2
F7 F1 2B 84 BB 84 00 00 00 03 84 BE 84 00 00 00
'@
$formulaSuffix = Convert-HexBytes @'
83 C4 04 50 E8 BC C3 13 00 0F B6 56 3C 52 51 8B
F8 E8 5F F9 F2 FF 83 C4 04 E8 A7 E4 13 00
'@
$failureCallback = Convert-HexBytes @'
8B 54 24 1C 68 60 9E 95 00 51 52 51 8D 84 24 FC
00 00 00 68 64 9E 95 00 50
'@

if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
    throw "Origin client was not found: $ClientExe"
}
[byte[]]$data = [IO.File]::ReadAllBytes($ClientExe)
if ($data.Length -ne $expectedLength) {
    throw "Unsupported Origin.exe length $($data.Length)."
}
$pe = Get-PeMetadata $data
if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
    $pe.ImageBase -ne 0x00400000 -or
    ($pe.Characteristics -band 0x0001) -eq 0 -or
    ($pe.DllCharacteristics -band 0x0040) -ne 0 -or
    (Resolve-ExecutableVa $pe $hookOffset $patchedHook.Length '.text') -ne
        $hookVa -or
    (Resolve-ExecutableVa $pe $caveOffset $caveReserveLength '.rdata') -ne
        $caveVa) {
    throw 'Origin.exe is not the audited x86 PE32 layout.'
}
if (-not (Test-Bytes $data `
        ($hookOffset - $formulaPrefix.Length) $formulaPrefix) -or
    -not (Test-Bytes $data `
        ($hookOffset + $originalHook.Length) $formulaSuffix) -or
    -not (Test-Bytes $data 0x16F176 $failureCallback)) {
    throw 'Origin.exe Merge formula/callback boundaries are not audited.'
}
if ($caveCode.Length -ne 64 -or
    (Get-NearTarget $patchedHook 0 $hookVa) -ne $caveVa -or
    (Get-ShortTarget $caveCode 15) -ne 32 -or
    (Get-ShortTarget $caveCode 20) -ne 32 -or
    (Get-ShortTarget $caveCode 25) -ne 32 -or
    (Get-ShortTarget $caveCode 30) -ne 35 -or
    -not (Test-Bytes $caveCode 44 (Convert-HexBytes '89 54 24 20')) -or
    -not (Test-Bytes $caveCode 48 (Convert-HexBytes '59 50 51')) -or
    [BitConverter]::ToUInt32($caveCode, 52) -ne $lookupCallVa -or
    [BitConverter]::ToUInt32($caveCode, 59) -ne $continuationVa -or
    $caveCode[63] -ne 0xC3) {
    throw 'Merge remaining-Savvy trampoline invariants are invalid.'
}

$hash = Get-Sha256 $ClientExe
$hasOriginal = Test-Bytes $data $hookOffset $originalHook
$hasPatched = Test-Bytes $data $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $data $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $data $caveOffset $patchedCave
$isOriginal = $hasOriginal -and $hasEmptyCave -and $hash -eq $sourceSha256
$isPatched = $hasPatched -and $hasPatchedCave -and
    $hash -eq $patchedSha256
if (-not $isOriginal -and -not $isPatched) {
    throw "Unsupported or partial Merge guidance state (SHA-256 $hash)."
}
$state = if ($isPatched) { 'Patched' } else { 'Ready to apply' }
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = $state
        ExactRemainingSavvy = $isPatched
        Encoding = '-(remaining hundredths * 10 + stat marker)'
        Hash = $hash
    }
    return
}

Assert-ClientClosed $ClientExe
$wantPatched = $Mode -eq 'Apply'
if (($wantPatched -and $isPatched) -or (-not $wantPatched -and $isOriginal)) {
    [pscustomobject]@{ Mode = $Mode; Status = 'Already ' + $state; Hash = $hash }
    return
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-merge-remaining-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backup = Join-Path $backupDirectory 'Origin.exe'
$stage = "$([IO.Path]::GetFullPath($ClientExe)).$([guid]::NewGuid().ToString('N')).stage"
Copy-Item -LiteralPath $ClientExe -Destination $backup
if ((Get-Sha256 $backup) -ne $hash) { throw 'Backup SHA-256 verification failed.' }

$targetHook = if ($wantPatched) { $patchedHook } else { $originalHook }
$targetCave = if ($wantPatched) { $patchedCave } else { $emptyCave }
$targetHash = if ($wantPatched) { $patchedSha256 } else { $sourceSha256 }
[byte[]]$output = $data.Clone()
Copy-Bytes $targetHook $output $hookOffset
Copy-Bytes $targetCave $output $caveOffset
$changed = 0
for ($offset = 0; $offset -lt $output.Length; $offset++) {
    if ($data[$offset] -eq $output[$offset]) { continue }
    $changed++
    $allowed = ($offset -ge $hookOffset -and
            $offset -lt $hookOffset + $patchedHook.Length) -or
        ($offset -ge $caveOffset -and
            $offset -lt $caveOffset + $caveReserveLength)
    if (-not $allowed) { throw "Unexpected mutation at 0x$($offset.ToString('X'))." }
}

try {
    [IO.File]::WriteAllBytes($stage, $output)
    if ((Get-Sha256 $stage) -ne $targetHash) {
        throw 'Staged Origin.exe failed exact SHA-256 verification.'
    }
    Assert-ClientClosed $ClientExe
    [IO.File]::Copy($stage, $ClientExe, $true)
    if ((Get-Sha256 $ClientExe) -ne $targetHash) {
        throw 'Installed Origin.exe failed exact SHA-256 verification.'
    }
}
catch {
    $installError = $_
    [IO.File]::Copy($backup, $ClientExe, $true)
    if ((Get-Sha256 $ClientExe) -ne $hash) {
        throw "Install and rollback failed: $installError"
    }
    throw "Install failed; verified predecessor restored: $installError"
}
finally {
    Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    ChangedBytes = $changed
    Backup = $backupDirectory
    Hash = Get-Sha256 $ClientExe
}
