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
        throw 'Malformed bag-consumable cooldown native bytes.'
    }
    [byte[]]$result = for ($index = 0; $index -lt $compact.Length;
        $index += 2) {
        [Convert]::ToByte($compact.Substring($index, 2), 16)
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
        if ($Data[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Copy-Bytes(
    [byte[]]$Source,
    [byte[]]$Destination,
    [int]$Offset
) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-RelativeTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$CodeVa,
    [byte]$Opcode
) {
    if ($Offset -lt 0 -or $Offset + 5 -gt $Code.Length -or
        $Code[$Offset] -ne $Opcode) {
        throw 'Malformed bag-consumable cooldown relative instruction.'
    }
    [int64]$CodeVa + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-RelativeCaveXrefs(
    [byte[]]$Data,
    [object]$Pe,
    [uint64]$StartVa,
    [uint64]$EndVa
) {
    $result = @()
    foreach ($section in $Pe.Sections) {
        if (($section.Characteristics -band 0x20000000) -eq 0) {
            continue
        }
        $first = [int]$section.RawOffset
        $last = [int]($section.RawOffset + $section.RawSize - 5)
        for ($offset = $first; $offset -le $last; $offset++) {
            if ($Data[$offset] -ne 0xE8 -and
                $Data[$offset] -ne 0xE9) {
                continue
            }
            [int64]$instructionVa = [int64]$Pe.ImageBase +
                $section.VirtualAddress + ($offset - $first)
            [int64]$target = $instructionVa + 5 +
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
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionTable + ($index * 40)
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString(
                $Data[$offset..($offset + 7)]).Trim([char]0)
            VirtualSize = [BitConverter]::ToUInt32($Data, $offset + 8)
            VirtualAddress = [BitConverter]::ToUInt32($Data, $offset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $offset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $offset + 20)
            Characteristics =
                [BitConverter]::ToUInt32($Data, $offset + 36)
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
    [int]$Length
) {
    foreach ($section in $Pe.Sections) {
        if ($FileOffset -lt $section.RawOffset -or
            $FileOffset + $Length -gt
                $section.RawOffset + $section.RawSize) {
            continue
        }
        if ($section.Name -ne '.text' -or
            ($section.Characteristics -band 0x20000000) -eq 0) {
            throw 'Native cooldown range is outside executable .text.'
        }
        return [uint64]$Pe.ImageBase + $section.VirtualAddress +
            ([uint64]$FileOffset - $section.RawOffset)
    }
    throw 'Native cooldown range is outside the audited PE sections.'
}

function Assert-ZeroInitializedState(
    [object]$Pe,
    [uint64]$StateVa,
    [int]$Length
) {
    foreach ($section in $Pe.Sections) {
        $start = [uint64]$Pe.ImageBase + $section.VirtualAddress
        $rawEnd = $start + $section.RawSize
        $virtualEnd = $start + $section.VirtualSize
        if ($StateVa -lt $start -or $StateVa + $Length -gt $virtualEnd) {
            continue
        }
        if ($section.Name -ne '.data' -or
            ($section.Characteristics -band 0x80000000) -eq 0 -or
            $StateVa -lt $rawEnd) {
            throw 'Cooldown state is not in writable zero-initialized .data.'
        }
        return
    }
    throw 'Cooldown state is outside the audited zero-initialized .data.'
}

function Assert-OriginClosed([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $live = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            try { $processPath = $process.Path }
            catch { $processPath = $null }
            $matches = $processPath -and [string]::Equals(
                [IO.Path]::GetFullPath($processPath),
                $resolved,
                [StringComparison]::OrdinalIgnoreCase)
            $hiddenLive = -not $processPath -and [string]::Equals(
                $resolved,
                $live,
                [StringComparison]::OrdinalIgnoreCase)
            if ($matches -or $hiddenLive) {
                throw 'Close Origin.exe before changing the cooldown clock.'
            }
        }
        finally { $process.Dispose() }
    }
}

$expectedLength = 6676480
$sourceSha256 =
    '00ED99F0EADB605059CB7A0FA476922EC6EA9E3EAE9218710C20299992706BDB'
$patchedSha256 =
    '7D1F17A21B0D34DA8BE61C639D72BFB4A518A2F3B0B3B0001699ADC560FA0021'

$requestHookOffset = 0x17428E
$requestHookVa = 0x0057428E
$responseHookOffset = 0x0EB968
$responseHookVa = 0x004EB968
$caveOffset = 0x51BF67
$caveVa = 0x0091BF67
$caveLength = 153
$responseWrapperOffset = 0x29
$responseWrapperVa = $caveVa + $responseWrapperOffset
$coolingVa = 0x00573D80
$bagRefreshVa = 0x00574FF0
$stateVa = 0x00A40010

$sourceRequestHook = Convert-HexBytes 'E8 ED FA FF FF'
$patchedRequestHook = Convert-HexBytes 'E8 D4 7C 3A 00'
$sourceResponseHook = Convert-HexBytes 'E8 83 96 08 00'
$patchedResponseHook = Convert-HexBytes 'E8 23 06 43 00'
$sourceCave = [byte[]]::new($caveLength)
$patchedCave = [byte[]]::new($caveLength)
$captureWrapper = Convert-HexBytes @'
8B 44 24 08 83 3D 10 00 A4 00 00 75 07 A3 10 00
A4 00 EB 05 A3 14 00 A4 00 E9 FB 7D C5 FF
'@
$responseWrapper = Convert-HexBytes @'
55 56 8B F1 33 ED 8B 44 24 38 85 C0 74 23 80 78
14 03 75 1D 80 78 15 0C 75 17 8B 2D 10 00 A4 00
A1 14 00 A4 00 A3 10 00 A4 00 83 25 14 00 A4 00
00 8B CE E8 28 90 C5 FF 85 ED 7E 07 55 56 E8 AD
7D C5 FF 5E 5D C3
'@
Copy-Bytes $captureWrapper $patchedCave 0
Copy-Bytes $responseWrapper $patchedCave $responseWrapperOffset

if ($captureWrapper.Length -ne 30 -or $responseWrapper.Length -ne 70 -or
    (Get-RelativeTarget $sourceRequestHook 0 $requestHookVa 0xE8) -ne
        $coolingVa -or
    (Get-RelativeTarget $patchedRequestHook 0 $requestHookVa 0xE8) -ne
        $caveVa -or
    (Get-RelativeTarget $captureWrapper 25 $caveVa 0xE9) -ne
        $coolingVa -or
    (Get-RelativeTarget $sourceResponseHook 0 $responseHookVa 0xE8) -ne
        $bagRefreshVa -or
    (Get-RelativeTarget $patchedResponseHook 0 $responseHookVa 0xE8) -ne
        $responseWrapperVa -or
    (Get-RelativeTarget $responseWrapper 51 $responseWrapperVa 0xE8) -ne
        $bagRefreshVa -or
    (Get-RelativeTarget $responseWrapper 62 $responseWrapperVa 0xE8) -ne
        $coolingVa) {
    throw 'Internal bag-consumable cooldown branch validation failed.'
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
    (Resolve-ExecutableVa $pe $requestHookOffset 5) -ne $requestHookVa -or
    (Resolve-ExecutableVa $pe $responseHookOffset 5) -ne $responseHookVa -or
    (Resolve-ExecutableVa $pe $caveOffset $caveLength) -ne $caveVa) {
    throw 'Origin.exe is not the audited fixed-base x86 PE32 layout.'
}
Assert-ZeroInitializedState $pe $stateVa 8

$useAndGroupGate = Convert-HexBytes @'
8B 83 F4 00 00 00 80 78 2E 00 74 6F 83 78 74 FF
74 69
'@
$requestStackFrame = Convert-HexBytes @'
8B 93 F4 00 00 00 8B 42 74 50 57
'@
$stockCoolingEntry = Convert-HexBytes '8B 54 24 08 83 EC 0C'
$stockCoolingReturn = Convert-HexBytes @'
5F 5E 5D 5B 83 C4 0C C2 08 00
'@
$dispatchPacketLocal = Convert-HexBytes '89 7C 24 2C'
$detailPageHalfRead = Convert-HexBytes @'
8B 44 24 2C 0F BE 48 15 0F BE 40 14
'@
$finalDetailRefreshPrefix = Convert-HexBytes 'E8 4A 7D 08 00 8B C8'
$finalDetailRefreshSuffix = Convert-HexBytes 'E8 9E F0 08 00 8B C8'
if (-not (Test-Bytes $bytes 0x174232 $useAndGroupGate) -or
    -not (Test-Bytes $bytes 0x174283 $requestStackFrame) -or
    -not (Test-Bytes $bytes 0x173D80 $stockCoolingEntry) -or
    -not (Test-Bytes $bytes 0x173F12 $stockCoolingReturn) -or
    -not (Test-Bytes $bytes 0x0EA41E $dispatchPacketLocal) -or
    -not (Test-Bytes $bytes 0x0EB8F8 $detailPageHalfRead) -or
    -not (Test-Bytes $bytes 0x0EB961 $finalDetailRefreshPrefix) -or
    -not (Test-Bytes $bytes 0x0EB96D $finalDetailRefreshSuffix)) {
    throw 'Origin.exe failed the audited cooldown stack/frame prerequisite guard.'
}

$hash = Get-Sha256 $resolvedClient
$isSource = $hash -eq $sourceSha256 -and
    (Test-Bytes $bytes $requestHookOffset $sourceRequestHook) -and
    (Test-Bytes $bytes $responseHookOffset $sourceResponseHook) -and
    (Test-Bytes $bytes $caveOffset $sourceCave)
$isPatched = $hash -eq $patchedSha256 -and
    (Test-Bytes $bytes $requestHookOffset $patchedRequestHook) -and
    (Test-Bytes $bytes $responseHookOffset $patchedResponseHook) -and
    (Test-Bytes $bytes $caveOffset $patchedCave)
if (-not $isSource -and -not $isPatched) {
    throw "Unsupported or partial cooldown-clock state (SHA-256 $hash)."
}

$caveXrefs = @(Get-RelativeCaveXrefs $bytes $pe $caveVa (
        $caveVa + $caveLength))
if (($isSource -and $caveXrefs.Count -ne 0) -or
    ($isPatched -and ($caveXrefs.Count -ne 2 -or
        $caveXrefs[0].Offset -ne $responseHookOffset -or
        $caveXrefs[0].Target -ne $responseWrapperVa -or
        $caveXrefs[1].Offset -ne $requestHookOffset -or
        $caveXrefs[1].Target -ne $caveVa))) {
    $xrefSummary = ($caveXrefs | ForEach-Object {
            '0x{0:X}->0x{1:X}' -f $_.Offset, $_.Target
        }) -join ', '
    throw "Origin.exe failed the exact cooldown cave xref audit " +
        "(source=$isSource, patched=$isPatched, count=" +
        "$($caveXrefs.Count), refs=$xrefSummary)."
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($isPatched) { 'Patched' } else { 'Ready to apply' }
        Hash = $hash
        GenericUseGate = $true
        RequestSideCalls = 1
        PostProjectionReapply = $isPatched
        PendingGroups = 2
        FinalDetailPage = 3
        FinalDetailHalf = 12
        RequestStack = '[ret, bagUI, group]; stock callee ret 8'
        ResponsePacket = '[wrapper esp+0x38] => handler [esp+0x2C]'
        PreservedRegisters = 'EBP/ESI saved; EBX/EDI untouched'
        CaveInboundRelativeXrefs = $caveXrefs.Count
        Cave = '0x51BF67-0x51C000 (exclusive)'
        CaveMapping = 'final executable .text page; ends at 0x0091C000'
        RuntimeState = '0x00A40010-0x00A40018 (zero-initialized .data)'
    }
    return
}

Assert-OriginClosed $resolvedClient
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

$targetRequest = if ($wantPatched) {
    $patchedRequestHook
}
else {
    $sourceRequestHook
}
$targetResponse = if ($wantPatched) {
    $patchedResponseHook
}
else {
    $sourceResponseHook
}
$targetCave = if ($wantPatched) { $patchedCave } else { $sourceCave }
$targetHash = if ($wantPatched) { $patchedSha256 } else { $sourceSha256 }

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-bag-consumable-cooldown-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backup = Join-Path $backupDirectory 'Origin.exe'
$stage = "$resolvedClient.$([guid]::NewGuid().ToString('N')).stage"
Copy-Item -LiteralPath $resolvedClient -Destination $backup
if ((Get-Sha256 $backup) -ne $hash) {
    throw 'Cooldown-clock backup failed SHA-256 verification.'
}

[byte[]]$output = $bytes.Clone()
Copy-Bytes $targetRequest $output $requestHookOffset
Copy-Bytes $targetResponse $output $responseHookOffset
Copy-Bytes $targetCave $output $caveOffset
$changed = 0
for ($offset = 0; $offset -lt $output.Length; $offset++) {
    if ($bytes[$offset] -eq $output[$offset]) { continue }
    $changed++
    $allowed = ($offset -ge $requestHookOffset -and
            $offset -lt $requestHookOffset + 5) -or
        ($offset -ge $responseHookOffset -and
            $offset -lt $responseHookOffset + 5) -or
        ($offset -ge $caveOffset -and
            $offset -lt $caveOffset + $caveLength)
    if (-not $allowed) {
        throw "Unexpected cooldown mutation at 0x$($offset.ToString('X'))."
    }
}

try {
    [IO.File]::WriteAllBytes($stage, $output)
    if ((Get-Sha256 $stage) -ne $targetHash) {
        throw 'Staged cooldown clock failed exact SHA-256 verification.'
    }
    Assert-OriginClosed $resolvedClient
    [IO.File]::Copy($stage, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $targetHash) {
        throw 'Installed cooldown clock failed exact state verification.'
    }
}
catch {
    $installError = $_
    [IO.File]::Copy($backup, $resolvedClient, $true)
    if ((Get-Sha256 $resolvedClient) -ne $hash) {
        throw "Cooldown install and rollback failed: $installError"
    }
    throw "Cooldown install failed; predecessor restored: $installError"
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
    GenericUseGate = $true
    RequestSideCalls = 1
    PostProjectionReapply = $wantPatched
}
