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
    if (($compact.Length % 2) -ne 0) {
        throw 'Malformed pet appearance-refresh native bytes.'
    }
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
    if ($optionalSize -lt 0xE0 -or
        $optionalOffset + $optionalSize -gt $Data.Length) {
        throw 'Origin.exe has an unsupported optional header.'
    }
    $sectionTable = $optionalOffset + $optionalSize
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
        FileCharacteristics =
            [BitConverter]::ToUInt16($Data, $peOffset + 22)
        OptionalMagic = [BitConverter]::ToUInt16($Data, $optionalOffset)
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalOffset + 28)
        DllCharacteristics =
            [BitConverter]::ToUInt16($Data, $optionalOffset + 70)
        BaseRelocationRva =
            [BitConverter]::ToUInt32($Data, $optionalOffset + 136)
        BaseRelocationSize =
            [BitConverter]::ToUInt32($Data, $optionalOffset + 140)
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

function Get-ShortTarget([byte[]]$Code, [int]$Offset, [uint64]$Va) {
    $displacement = if ($Code[$Offset + 1] -ge 0x80) {
        [int]$Code[$Offset + 1] - 0x100
    }
    else {
        [int]$Code[$Offset + 1]
    }
    [int64]$Va + $Offset + 2 + $displacement
}

function Assert-ClientClosed([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $live = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            try { $processPath = $process.Path } catch { $processPath = $null }
            $matches = $processPath -and [string]::Equals(
                [IO.Path]::GetFullPath($processPath),
                $resolved,
                [StringComparison]::OrdinalIgnoreCase)
            $hiddenLive = -not $processPath -and [string]::Equals(
                $resolved,
                $live,
                [StringComparison]::OrdinalIgnoreCase)
            if ($matches -or $hiddenLive) {
                throw 'Close Origin.exe before changing the pet refresh.'
            }
        }
        finally { $process.Dispose() }
    }
}

$expectedLength = 6676480
$sourceSha256 =
    'F8D832D97A1C910AF31645DBD8B6FC2BDADF4AD30196470553A8668DB81A1D17'
$patchedSha256 =
    'C1CE0273504AB3E8020FD2EB2692351FFA0094F6A103719EB8970FD98C3DB2B6'

$dispatcherOffset = 0x5C3480
$dispatcherVa = 0x009C3480
$tailOffset = 0x5C38B1
$tailVa = 0x009C38B1
$tailLength = 47
$finalOffset = 0x5C3692
$finalVa = 0x009C3692
$redrawVa = 0x009C3658
$copyVa = 0x009C3492

$sourceDispatcher = Convert-HexBytes @'
9C 66 83 3E 44 75 16 51 56 57 83 C6 14 81 C7 84
00 00 00 B9 0C 00 00 00 F3 A5 5F 5E 59 9D C7 84
24 E4 00 00 00 07 00 00 00 E9 AA 01 00 00 00 00
'@
$patchedDispatcher = Convert-HexBytes @'
9C 66 83 3E 44 74 0B 66 83 3E 48 75 1E
E9 1F 04 00 00
51 56 57 83 C6 14 81 C7 84 00 00 00 6A 0C 59
F3 A5 5F 5E 59 E9 E7 01 00 00 E9 E2 01 00 00
'@
$emptyTail = [byte[]]::new($tailLength)
$patchedTail = Convert-HexBytes @'
50 8B 46 44 3D 2D 01 00 00 77 17 84 C0 74 13
3C 2D 77 0F 88 47 3C 88 A7 BC 00 00 00 58 E9
BF FB FF FF 58 E9 B9 FD FF FF 00 00 00 00 00 00 00
'@
$emptyFinal = [byte[]]::new(14)
$patchedFinal = Convert-HexBytes @'
9D C7 84 24 E4 00 00 00 07 00 00 00 EB B8
'@
$refreshHook = Convert-HexBytes @'
E9 1F 1B 32 00 90 90 90 90 90 90
'@
$redrawPrefix = Convert-HexBytes @'
9C 50 51 52 8B 0D 84 D0 5A 01 85 C9 74 05 E8 F5
17 C0 FF A1 98 D0 5A 01 85 C0 74 15 8B 48 04 85
C9 74 0E 80 B9 0D 01 00 00 00 90 90 E8 D7 A3 BA
FF 5A 59 58 9D E9 D5 E2 CD FF
'@
$tailPrefix = Convert-HexBytes 'E9 17 E8 A7 FF'
$tailSuffix = Convert-HexBytes @'
81 7F 6C 47 57 41 32 0F 84 82 00 00 00 8B 47 64
'@
$speciesCopyEvidence = Convert-HexBytes @'
0F B6 57 24 8D 4F 04 8B C1 88 56 3C
'@
$boundCopyEvidence = Convert-HexBytes @'
0F B6 87 A4 00 00 00 83 C4 30 88 86 BC 00 00 00
'@

if ($sourceDispatcher.Length -ne 48 -or
    $patchedDispatcher.Length -ne 48 -or
    $patchedTail.Length -ne $tailLength -or
    $patchedFinal.Length -ne 14 -or
    $redrawPrefix.Length -ne 58) {
    throw 'Internal pet appearance-refresh code lengths are invalid.'
}
if ($patchedDispatcher[0] -ne 0x9C -or
    -not (Test-Bytes $patchedDispatcher 1 (
        Convert-HexBytes '66 83 3E 44')) -or
    -not (Test-Bytes $patchedDispatcher 7 (
        Convert-HexBytes '66 83 3E 48')) -or
    (Get-ShortTarget $patchedDispatcher 5 $dispatcherVa) -ne $copyVa -or
    (Get-ShortTarget $patchedDispatcher 11 $dispatcherVa) -ne
        ($dispatcherVa + 43) -or
    (Get-NearTarget $patchedDispatcher 13 $dispatcherVa) -ne $tailVa -or
    (Get-NearTarget $patchedDispatcher 38 $dispatcherVa) -ne $finalVa -or
    (Get-NearTarget $patchedDispatcher 43 $dispatcherVa) -ne $finalVa) {
    throw 'Internal exact 68/72 dispatcher validation failed.'
}
if (-not (Test-Bytes $patchedTail 1 (
        Convert-HexBytes '8B 46 44 3D 2D 01 00 00')) -or
    (Get-ShortTarget $patchedTail 9 $tailVa) -ne ($tailVa + 34) -or
    (Get-ShortTarget $patchedTail 13 $tailVa) -ne ($tailVa + 34) -or
    (Get-ShortTarget $patchedTail 17 $tailVa) -ne ($tailVa + 34) -or
    -not (Test-Bytes $patchedTail 19 (
        Convert-HexBytes '88 47 3C 88 A7 BC 00 00 00')) -or
    (Get-NearTarget $patchedTail 29 $tailVa) -ne $copyVa -or
    (Get-NearTarget $patchedTail 35 $tailVa) -ne $finalVa -or
    (Get-ShortTarget $patchedFinal 12 $finalVa) -ne $redrawVa) {
    throw 'Internal species/bound tail validation failed.'
}

if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
    throw "Origin client was not found: $ClientExe"
}
$resolvedClient = [IO.Path]::GetFullPath($ClientExe)
[byte[]]$bytes = [IO.File]::ReadAllBytes($resolvedClient)
if ($bytes.Length -ne $expectedLength) {
    throw "Unsupported Origin.exe length $($bytes.Length)."
}
$pe = Get-PeMetadata $bytes
if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
    $pe.ImageBase -ne 0x00400000 -or
    ($pe.FileCharacteristics -band 0x0001) -eq 0 -or
    ($pe.DllCharacteristics -band 0x0040) -ne 0 -or
    $pe.BaseRelocationRva -ne 0 -or $pe.BaseRelocationSize -ne 0 -or
    (Resolve-ExecutableVa $pe $dispatcherOffset 48 '.rdata') -ne
        $dispatcherVa -or
    (Resolve-ExecutableVa $pe $tailOffset $tailLength '.rdata') -ne
        $tailVa -or
    (Resolve-ExecutableVa $pe $finalOffset 14 '.rdata') -ne $finalVa) {
    throw 'Origin.exe is not the audited x86 PE32 layout.'
}
if (-not (Test-Bytes $bytes 0x2A195C $refreshHook) -or
    -not (Test-Bytes $bytes 0x5C3658 $redrawPrefix) -or
    -not (Test-Bytes $bytes ($tailOffset - 5) $tailPrefix) -or
    -not (Test-Bytes $bytes ($tailOffset + $tailLength) $tailSuffix) -or
    -not (Test-Bytes $bytes 0x2A63BA $speciesCopyEvidence) -or
    -not (Test-Bytes $bytes 0x2A649E $boundCopyEvidence)) {
    throw 'Origin.exe failed the exact pet refresh prerequisite guard.'
}

$hash = Get-Sha256 $resolvedClient
$isSource = $hash -eq $sourceSha256 -and
    (Test-Bytes $bytes $dispatcherOffset $sourceDispatcher) -and
    (Test-Bytes $bytes $tailOffset $emptyTail) -and
    (Test-Bytes $bytes $finalOffset $emptyFinal)
$isPatched = $hash -eq $patchedSha256 -and
    (Test-Bytes $bytes $dispatcherOffset $patchedDispatcher) -and
    (Test-Bytes $bytes $tailOffset $patchedTail) -and
    (Test-Bytes $bytes $finalOffset $patchedFinal)
if (-not $isSource -and -not $isPatched) {
    throw "Unsupported or partial pet appearance-refresh state (SHA-256 $hash)."
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Patched' } else { 'Ready to apply' }
        ProgressionPacketLength = 68
        AppearancePacketLength = if ($isPatched) { 72 } else { $null }
        SpeciesRefresh = $isPatched
        BoundRefresh = $isPatched
        PetDetailRedraw = $true
        PetMergeRedraw = $true
        Hash = $hash
        TailCave = '0x5C38B1-0x5C38DF (exclusive)'
    }
    return
}

Assert-ClientClosed $resolvedClient
$wantPatched = $Mode -eq 'Apply'
if (($wantPatched -and $isPatched) -or (-not $wantPatched -and $isSource)) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($wantPatched) {
            'Already patched'
        }
        else {
            'Already reverted'
        }
        Hash = $hash
    }
    return
}

$targetDispatcher = if ($wantPatched) {
    $patchedDispatcher
}
else {
    $sourceDispatcher
}
$targetTail = if ($wantPatched) { $patchedTail } else { $emptyTail }
$targetFinal = if ($wantPatched) { $patchedFinal } else { $emptyFinal }
$targetHash = if ($wantPatched) { $patchedSha256 } else { $sourceSha256 }

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-appearance-refresh-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backup = Join-Path $backupDirectory 'Origin.exe'
$stage = "$resolvedClient.$([guid]::NewGuid().ToString('N')).stage"
Copy-Item -LiteralPath $resolvedClient -Destination $backup
if ((Get-Sha256 $backup) -ne $hash) {
    throw 'Pet appearance-refresh backup failed SHA-256 verification.'
}

[byte[]]$output = $bytes.Clone()
Copy-Bytes $targetDispatcher $output $dispatcherOffset
Copy-Bytes $targetTail $output $tailOffset
Copy-Bytes $targetFinal $output $finalOffset
$changed = 0
for ($offset = 0; $offset -lt $output.Length; $offset++) {
    if ($bytes[$offset] -eq $output[$offset]) { continue }
    $changed++
    $allowed = ($offset -ge $dispatcherOffset -and
            $offset -lt $dispatcherOffset + 48) -or
        ($offset -ge $tailOffset -and
            $offset -lt $tailOffset + $tailLength) -or
        ($offset -ge $finalOffset -and
            $offset -lt $finalOffset + 14)
    if (-not $allowed) {
        throw "Unexpected pet refresh mutation at 0x$($offset.ToString('X'))."
    }
}

try {
    [IO.File]::WriteAllBytes($stage, $output)
    if ((Get-Sha256 $stage) -ne $targetHash) {
        throw 'Staged pet refresh failed exact SHA-256 verification.'
    }
    Assert-ClientClosed $resolvedClient
    [IO.File]::Copy($stage, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $targetHash) {
        throw 'Installed pet refresh failed exact state verification.'
    }
}
catch {
    $installError = $_
    [IO.File]::Copy($backup, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $hash) {
        throw "Pet refresh install and rollback failed: $installError"
    }
    throw "Pet refresh install failed; predecessor restored: $installError"
}
finally {
    Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    ChangedBytes = $changed
    Backup = $backupDirectory
    ProgressionPacketLength = 68
    AppearancePacketLength = if ($wantPatched) { 72 } else { $null }
    SpeciesRefresh = $wantPatched
    BoundRefresh = $wantPatched
    Hash = Get-Sha256 $resolvedClient
}
