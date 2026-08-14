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
        throw 'Malformed Soul/owner-Merge refresh hex.'
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
    $sectionTable = $optionalOffset + $optionalSize
    if ($optionalSize -lt 0xE0 -or
        $sectionTable + $sectionCount * 40 -gt $Data.Length) {
        throw 'Origin.exe section table is invalid.'
    }
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
        Characteristics = [BitConverter]::ToUInt16($Data, $peOffset + 22)
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

function Get-RelativeTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va,
    [byte]$Opcode
) {
    if ($Code[$Offset] -ne $Opcode) {
        throw 'Internal Soul/owner-Merge relative opcode is malformed.'
    }
    [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-RelativeRangeXrefs(
    [byte[]]$Data,
    [object]$Pe,
    [uint64]$StartVa,
    [uint64]$EndVa
) {
    $result = @()
    foreach ($section in $Pe.Sections | Where-Object {
            ($_.Characteristics -band 0x20000000) -ne 0
        }) {
        for ($offset = [int]$section.RawOffset;
            $offset -le [int]($section.RawOffset + $section.RawSize - 5);
            $offset++) {
            if ($Data[$offset] -ne 0xE8 -and $Data[$offset] -ne 0xE9) {
                continue
            }
            $sourceVa = [uint64]$Pe.ImageBase + $section.VirtualAddress +
                ([uint64]$offset - $section.RawOffset)
            $target = [int64]$sourceVa + 5 +
                [BitConverter]::ToInt32($Data, $offset + 1)
            if ($target -ge $StartVa -and $target -lt $EndVa) {
                $result += [pscustomobject]@{
                    Offset = $offset
                    Target = $target
                }
            }
        }
    }
    return $result
}

function Get-AbsoluteRangeReferences(
    [byte[]]$Data,
    [uint32]$StartVa,
    [uint32]$EndVa
) {
    $result = @()
    for ($offset = 0; $offset -le $Data.Length - 4; $offset++) {
        $value = [BitConverter]::ToUInt32($Data, $offset)
        if ($value -ge $StartVa -and $value -lt $EndVa) {
            $result += [pscustomobject]@{ Offset = $offset; Target = $value }
        }
    }
    return $result
}

function Assert-OriginClosed([string]$Path) {
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
                throw 'Close Origin.exe before changing the Soul refresh.'
            }
        }
        finally { $process.Dispose() }
    }
}

$expectedLength = 6676480
$sourceSha256 =
    '7D1F17A21B0D34DA8BE61C639D72BFB4A518A2F3B0B3B0001699ADC560FA0021'
$previousPatchedSha256 =
    'D6472178B58B75334E344EEA3AFF4884D350C62B4A0F082F41ECCA1489A29FF8'
$patchedSha256 =
    '48420B7AE83AD3DE17E33E22D270FC30B7E3656D6F070BADDD52761AAB4418BB'

$hookOffset = 0x2A11B4
$hookVa = 0x006A11B4
$caveOffset = 0x5C3366
$caveVa = 0x009C3366
$caveLength = 154
$ownerMergeSetterVa = 0x005C6A10

$sourceHook = Convert-HexBytes '88 90 B9 00 00 00'
$patchedHook = Convert-HexBytes 'E8 AD 21 32 00 90'
$sourceCave = [byte[]]::new($caveLength)
$previousPatchedCave = [byte[]]::new($caveLength)
$previousWrapper = Convert-HexBytes @'
88 90 B9 00 00 00 9C 60 8B E8 A1 A4 D0 5A 01 85
C0 74 0E 39 68 04 75 09 8B C8 8B C5 E8 89 36 C0
FF 61 9D C3
'@
Copy-Bytes $previousWrapper $previousPatchedCave 0
$patchedCave = [byte[]]::new($caveLength)
$wrapper = Convert-HexBytes @'
88 90 B9 00 00 00 9C 60 8B E8 A1 A4 D0 5A 01 85
C0 74 11 8B 55 00 39 50 04 75 09 8B C8 8B C2 E8
86 36 C0 FF 61 9D C3
'@
Copy-Bytes $wrapper $patchedCave 0

if ($wrapper.Length -ne 39 -or
    (Get-RelativeTarget $patchedHook 0 $hookVa 0xE8) -ne $caveVa -or
    (Get-RelativeTarget $wrapper 31 $caveVa 0xE8) -ne
        $ownerMergeSetterVa -or
    $caveVa + 19 + [int][sbyte]$wrapper[18] -ne $caveVa + 36 -or
    $caveVa + 27 + [int][sbyte]$wrapper[26] -ne $caveVa + 36 -or
    -not (Test-Bytes $wrapper 0 $sourceHook) -or
    -not (Test-Bytes $wrapper 6 (Convert-HexBytes '9C 60')) -or
    -not (Test-Bytes $wrapper 10 (
        Convert-HexBytes 'A1 A4 D0 5A 01')) -or
    -not (Test-Bytes $wrapper 19 (
        Convert-HexBytes '8B 55 00 39 50 04')) -or
    -not (Test-Bytes $wrapper 36 (Convert-HexBytes '61 9D C3'))) {
    throw 'Internal Soul/owner-Merge refresh invariants are invalid.'
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
    ($pe.Characteristics -band 0x0001) -eq 0 -or
    ($pe.DllCharacteristics -band 0x0040) -ne 0 -or
    $pe.BaseRelocationRva -ne 0 -or $pe.BaseRelocationSize -ne 0 -or
    (Resolve-ExecutableVa $pe $hookOffset $sourceHook.Length '.text') -ne
        $hookVa -or
    (Resolve-ExecutableVa $pe $caveOffset $caveLength '.rdata') -ne
        $caveVa) {
    throw 'Origin.exe is not the audited fixed-base x86 PE32 layout.'
}

$handlerPrefix = Convert-HexBytes @'
8B C1 8D 48 08 8B 40 1C E8 B3 4F 00 00 8B 4C 24
04 8A 51 04
'@
$handlerReturn = Convert-HexBytes 'C2 04 00 CC CC CC'
$cavePrefix = Convert-HexBytes '57 01 85 C0 74 0B 8B 44 24 50 33 DB E9 67 D2 C2 FF E9 5D DA C2 FF'
$caveSuffix = Convert-HexBytes 'E8 CB 69 C1 FF 85 C0 74 11'
$ownerMergeSetter = Convert-HexBytes @'
51 89 41 04 8B 01 85 C0 74 0F 80 B8 0D 01 00 00
00 74 06 51 E8 57 FC FF FF 59 C3
'@
$petIdLookup = Convert-HexBytes @'
8B 4C 24 28 3B 08 74 09 05 C0 00 00 00 3B C7 75 EF
'@
$ownerMergeOpen = Convert-HexBytes @'
E8 45 BB EC FF 8B C8 8B 43 6C E8 2B FF 00 00
'@
if (-not (Test-Bytes $bytes `
        ($hookOffset - $handlerPrefix.Length) $handlerPrefix) -or
    -not (Test-Bytes $bytes `
        ($hookOffset + $sourceHook.Length) $handlerReturn) -or
    -not (Test-Bytes $bytes `
        ($caveOffset - $cavePrefix.Length) $cavePrefix) -or
    -not (Test-Bytes $bytes ($caveOffset + $caveLength) $caveSuffix) -or
    -not (Test-Bytes $bytes 0x1C6A10 $ownerMergeSetter) -or
    -not (Test-Bytes $bytes 0x2A6231 $petIdLookup) -or
    -not (Test-Bytes $bytes 0x1B6AD6 $ownerMergeOpen)) {
    throw 'Origin.exe failed the audited Soul handler/cave boundaries.'
}

$hash = Get-Sha256 $resolvedClient
$isSource = $hash -eq $sourceSha256 -and
    (Test-Bytes $bytes $hookOffset $sourceHook) -and
    (Test-Bytes $bytes $caveOffset $sourceCave)
$isPreviousPatch = $hash -eq $previousPatchedSha256 -and
    (Test-Bytes $bytes $hookOffset $patchedHook) -and
    (Test-Bytes $bytes $caveOffset $previousPatchedCave)
$isPatched = $hash -eq $patchedSha256 -and
    (Test-Bytes $bytes $hookOffset $patchedHook) -and
    (Test-Bytes $bytes $caveOffset $patchedCave)
if (-not $isSource -and -not $isPreviousPatch -and -not $isPatched) {
    throw "Unsupported or partial Soul refresh state (SHA-256 $hash)."
}

$caveXrefs = @(Get-RelativeRangeXrefs $bytes $pe $caveVa (
        $caveVa + $caveLength))
$absoluteCaveRefs = @(Get-AbsoluteRangeReferences $bytes $caveVa (
        $caveVa + $caveLength))
if (($isSource -and $caveXrefs.Count -ne 0) -or
    (($isPreviousPatch -or $isPatched) -and
        ($caveXrefs.Count -ne 1 -or
        $caveXrefs[0].Offset -ne $hookOffset -or
        $caveXrefs[0].Target -ne $caveVa)) -or
    $absoluteCaveRefs.Count -ne 0) {
    throw 'Origin.exe failed the exact Soul refresh cave xref audit.'
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Patched' }
            elseif ($isPreviousPatch) { 'Previous patch; ready to upgrade' }
            else { 'Ready to apply' }
        Hash = $hash
        ResultOpcode = 10271
        ActivePetStageStore = $true
        SamePetGuard = $isPatched
        VisibleOwnerMergeRefresh = $isPatched
        PreservedState = 'pushfd/pushad; popad/popfd'
        CaveInboundRelativeXrefs = $caveXrefs.Count
        Cave = '0x5C3366-0x5C3400 (exclusive)'
    }
    return
}

Assert-OriginClosed $resolvedClient
$wantPatched = $Mode -eq 'Apply'
if (($wantPatched -and $isPatched) -or (-not $wantPatched -and $isSource)) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($wantPatched) { 'Already patched' } else { 'Already reverted' }
        Hash = $hash
    }
    return
}

$targetHook = if ($wantPatched) { $patchedHook } else { $sourceHook }
$targetCave = if ($wantPatched) { $patchedCave } else { $sourceCave }
$targetHash = if ($wantPatched) { $patchedSha256 } else { $sourceSha256 }
$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-soul-owner-merge-refresh-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backup = Join-Path $backupDirectory 'Origin.exe'
$stage = "$resolvedClient.$([guid]::NewGuid().ToString('N')).stage"
Copy-Item -LiteralPath $resolvedClient -Destination $backup
if ((Get-Sha256 $backup) -ne $hash) {
    throw 'Soul refresh backup failed SHA-256 verification.'
}

[byte[]]$output = $bytes.Clone()
Copy-Bytes $targetHook $output $hookOffset
Copy-Bytes $targetCave $output $caveOffset
$changed = 0
for ($offset = 0; $offset -lt $output.Length; $offset++) {
    if ($bytes[$offset] -eq $output[$offset]) { continue }
    $changed++
    $allowed = ($offset -ge $hookOffset -and
            $offset -lt $hookOffset + $sourceHook.Length) -or
        ($offset -ge $caveOffset -and
            $offset -lt $caveOffset + $caveLength)
    if (-not $allowed) {
        throw "Unexpected Soul refresh mutation at 0x$($offset.ToString('X'))."
    }
}

try {
    [IO.File]::WriteAllBytes($stage, $output)
    if ((Get-Sha256 $stage) -ne $targetHash) {
        throw 'Staged Soul refresh failed exact SHA-256 verification.'
    }
    Assert-OriginClosed $resolvedClient
    [IO.File]::Copy($stage, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $targetHash) {
        throw 'Installed Soul refresh failed exact SHA-256 verification.'
    }
}
catch {
    $installError = $_
    [IO.File]::Copy($backup, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $hash) {
        throw "Soul refresh install and rollback failed: $installError"
    }
    throw "Soul refresh failed; verified predecessor restored: $installError"
}
finally {
    Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    ChangedBytes = $changed
    Backup = $backupDirectory
    Hash = Get-Sha256 $resolvedClient
    ResultOpcode = 10271
    SamePetGuard = $wantPatched
    VisibleOwnerMergeRefresh = $wantPatched
}
