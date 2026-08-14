[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Hex([string]$Value) {
    $compact = $Value -replace '[^0-9A-Fa-f]', ''
    if (($compact.Length % 2) -ne 0) {
        throw 'Malformed pet gender-refresh native bytes.'
    }
    [byte[]]$bytes = for ($index = 0; $index -lt $compact.Length;
        $index += 2) {
        [Convert]::ToByte($compact.Substring($index, 2), 16)
    }
    return ,$bytes
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
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionTable + $index * 40
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString(
                $Data[$offset..($offset + 7)]).Trim([char]0)
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
    'C1CE0273504AB3E8020FD2EB2692351FFA0094F6A103719EB8970FD98C3DB2B6'
$patchedSha256 =
    '00ED99F0EADB605059CB7A0FA476922EC6EA9E3EAE9218710C20299992706BDB'
$caveOffset = 0x5C341F
$caveVa = 0x009C341F
$caveLength = 97
$dispatcherOffset = 0x5C3480
$dispatcherVa = 0x009C3480
$copyVa = 0x009C3492
$finalVa = 0x009C3692
$appearanceTailVa = 0x009C38B1

$emptyCave = [byte[]]::new($caveLength)
$genderDecoder = Hex @'
9C 66 83 3E 44 74 6C 66 83 3E 48 0F 84 81 04 00 00
66 83 3E 4C 0F 85 58 02 00 00 50 8B 46 44 3D 2D 01
00 00 77 23 84 C0 74 1F 3C 2D 77 1B 83 7E 48 01 77
15 88 47 3C 88 A7 BC 00 00 00 8A 46 48 88 47 3F 58
E9 2A 00 00 00 58 E9 24 02 00 00
'@
$patchedCave = [byte[]]::new($caveLength)
Copy-Bytes $genderDecoder $patchedCave 0
$sourceDispatcherEntry = Hex '9C 66 83 3E 44'
$patchedDispatcherEntry = Hex 'E9 9A FF FF FF'
$dispatcherRemainder = Hex @'
74 0B 66 83 3E 48 75 1E E9 1F 04 00 00 51 56 57
83 C6 14 81 C7 84 00 00 00 6A 0C 59 F3 A5 5F 5E
59 E9 E7 01 00 00 E9 E2 01 00 00
'@
$appearanceTail = Hex @'
50 8B 46 44 3D 2D 01 00 00 77 17 84 C0 74 13 3C
2D 77 0F 88 47 3C 88 A7 BC 00 00 00 58 E9 BF FB
FF FF 58 E9 B9 FD FF FF 00 00 00 00 00 00 00
'@
$finalizer = Hex '9D C7 84 24 E4 00 00 00 07 00 00 00 EB B8'

if ($genderDecoder.Length -ne 79 -or
    $sourceDispatcherEntry.Length -ne 5 -or
    $patchedDispatcherEntry.Length -ne 5 -or
    $dispatcherRemainder.Length -ne 43) {
    throw 'Internal pet gender-refresh code lengths are invalid.'
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
    (Resolve-ExecutableVa $pe $caveOffset $caveLength '.rdata') -ne
        $caveVa -or
    (Resolve-ExecutableVa $pe $dispatcherOffset 48 '.rdata') -ne
        $dispatcherVa) {
    throw 'Origin.exe is not the audited fixed-base x86 PE32 layout.'
}
$hasDispatcherRemainder = Test-Bytes $bytes `
    ($dispatcherOffset + 5) $dispatcherRemainder
$hasAppearanceTail = Test-Bytes $bytes 0x5C38B1 $appearanceTail
$hasFinalizer = Test-Bytes $bytes 0x5C3692 $finalizer
$hasCaveGuard = Test-Bytes $bytes ($caveOffset - 5) (
    Hex 'E9 55 06 AD FF')
if (-not $hasDispatcherRemainder -or
    -not $hasAppearanceTail -or
    -not $hasFinalizer -or
    -not $hasCaveGuard) {
    throw 'Origin.exe failed the composed appearance-refresh prerequisite.'
}

$hash = Get-Sha256 $resolvedClient
$isSource = $hash -eq $sourceSha256 -and
    (Test-Bytes $bytes $caveOffset $emptyCave) -and
    (Test-Bytes $bytes $dispatcherOffset $sourceDispatcherEntry)
$isPatched = $hash -eq $patchedSha256 -and
    (Test-Bytes $bytes $caveOffset $patchedCave) -and
    (Test-Bytes $bytes $dispatcherOffset $patchedDispatcherEntry)
if (-not $isSource -and -not $isPatched) {
    throw "Unsupported or partial pet gender-refresh state (SHA-256 $hash)."
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Patched' } else { 'Ready to apply' }
        ProgressionPacketLength = 68
        AppearancePacketLength = 72
        GenderPacketLength = if ($isPatched) { 76 } else { $null }
        SexRefresh = $isPatched
        Hash = $hash
        Cave = '0x5C341F-0x5C347F (exclusive)'
    }
    return
}

Assert-ClientClosed $resolvedClient
$wantPatched = $Mode -eq 'Apply'
if (($wantPatched -and $isPatched) -or (-not $wantPatched -and $isSource)) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($wantPatched) { 'Already patched' }
            else { 'Already reverted' }
        Hash = $hash
    }
    return
}

$targetCave = if ($wantPatched) { $patchedCave } else { $emptyCave }
$targetEntry = if ($wantPatched) {
    $patchedDispatcherEntry
}
else {
    $sourceDispatcherEntry
}
$targetHash = if ($wantPatched) { $patchedSha256 } else { $sourceSha256 }
$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-gender-refresh-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backup = Join-Path $backupDirectory 'Origin.exe'
$stage = "$resolvedClient.$([guid]::NewGuid().ToString('N')).stage"
Copy-Item -LiteralPath $resolvedClient -Destination $backup
if ((Get-Sha256 $backup) -ne $hash) {
    throw 'Pet gender-refresh backup failed SHA-256 verification.'
}

[byte[]]$output = $bytes.Clone()
Copy-Bytes $targetCave $output $caveOffset
Copy-Bytes $targetEntry $output $dispatcherOffset
$changed = 0
for ($offset = 0; $offset -lt $output.Length; $offset++) {
    if ($bytes[$offset] -eq $output[$offset]) { continue }
    $changed++
    $allowed = ($offset -ge $caveOffset -and
            $offset -lt $caveOffset + $caveLength) -or
        ($offset -ge $dispatcherOffset -and
            $offset -lt $dispatcherOffset + 5)
    if (-not $allowed) {
        throw "Unexpected pet gender mutation at 0x$($offset.ToString('X'))."
    }
}

try {
    [IO.File]::WriteAllBytes($stage, $output)
    if ((Get-Sha256 $stage) -ne $targetHash) {
        throw 'Staged pet gender refresh failed exact SHA-256 verification.'
    }
    Assert-ClientClosed $resolvedClient
    [IO.File]::Copy($stage, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $targetHash) {
        throw 'Installed pet gender refresh failed exact verification.'
    }
}
catch {
    $installError = $_
    [IO.File]::Copy($backup, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $hash) {
        throw "Pet gender install and rollback failed: $installError"
    }
    throw "Pet gender refresh install failed; predecessor restored: $installError"
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
    AppearancePacketLength = 72
    GenderPacketLength = if ($wantPatched) { 76 } else { $null }
    SexRefresh = $wantPatched
    Hash = Get-Sha256 $resolvedClient
}
