[CmdletBinding()]
param(
    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',

    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Convert-HexBytes([string]$Hex) {
    $normalized = $Hex -replace '\s', ''
    if (($normalized.Length -band 1) -ne 0) {
        throw 'Hex text must contain an even number of digits.'
    }

    [byte[]]$result = for ($index = 0; $index -lt $normalized.Length;
        $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $result
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

    $optionalHeaderSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $optionalHeaderOffset = $peOffset + 24
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $sectionTableOffset = $optionalHeaderOffset + $optionalHeaderSize
    if ($sectionCount -le 0 -or
        $sectionTableOffset + ($sectionCount * 40) -gt $Data.Length) {
        throw 'Origin.exe section table is invalid.'
    }

    $sections = @()
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount;
        $sectionIndex++) {
        $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
        $nameBytes = $Data[$sectionOffset..($sectionOffset + 7)]
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString($nameBytes).Trim([char]0)
            VirtualAddress = [BitConverter]::ToUInt32(
                $Data, $sectionOffset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $sectionOffset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $sectionOffset + 20)
            Characteristics = [BitConverter]::ToUInt32(
                $Data, $sectionOffset + 36)
        }
    }

    return [pscustomobject]@{
        Machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
        OptionalMagic = [BitConverter]::ToUInt16(
            $Data, $optionalHeaderOffset)
        ImageBase = [BitConverter]::ToUInt32(
            $Data, $optionalHeaderOffset + 28)
        Sections = $sections
    }
}

function Resolve-ExecutableSection(
    [object]$Pe,
    [int]$FileOffset,
    [int]$Length
) {
    foreach ($section in $Pe.Sections) {
        if ($FileOffset -lt $section.RawOffset -or
            $FileOffset + $Length -gt $section.RawOffset + $section.RawSize) {
            continue
        }
        if (($section.Characteristics -band 0x20000000) -eq 0) {
            throw "File offset 0x$('{0:X}' -f $FileOffset) is not executable."
        }
        return $section
    }
    throw "File offset 0x$('{0:X}' -f $FileOffset) is outside a PE section."
}

function Resolve-ExecutableVa(
    [object]$Pe,
    [int]$FileOffset,
    [int]$Length
) {
    $section = Resolve-ExecutableSection $Pe $FileOffset $Length
    return [uint64]$Pe.ImageBase + $section.VirtualAddress +
        ([uint64]$FileOffset - $section.RawOffset)
}

function Get-NearBranchTarget(
    [byte[]]$Code,
    [int]$InstructionOffset,
    [uint64]$CodeVa
) {
    return [int64]$CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
}

function Get-ShortBranchTarget(
    [byte[]]$Code,
    [int]$InstructionOffset
) {
    return $InstructionOffset + 2 +
        [int][sbyte]$Code[$InstructionOffset + 1]
}

function Test-AllowedDifference([int]$Offset, [object[]]$Ranges) {
    foreach ($range in $Ranges) {
        if ($Offset -ge $range.Offset -and
            $Offset -lt $range.Offset + $range.Length) {
            return $true
        }
    }
    return $false
}

function Test-TargetClientRunning([string]$ExecutablePath) {
    $target = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in @(Get-Process -Name 'Origin' -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $process.Path
        }
        catch {
            $processPath = $null
        }
        if ($processPath -and [string]::Equals(
                [IO.Path]::GetFullPath($processPath),
                $target,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

$expectedLength = 6676480
$expectedMachine = 0x014C
$expectedOptionalMagic = 0x010B
$expectedImageBase = 0x00400000

# QuestView's frame/update entry dereferences the owning object and both UI
# roots before it has any opportunity to reject a partially initialized
# lifecycle state. Guard the function itself so every caller is protected.
# The safe path returns before the native stack frame is allocated; the ready
# path replays all five displaced bytes and resumes the byte-identical function
# at +5.
$hookOffset = 0x1DA4C0
$hookVa = 0x005DA4C0
$caveOffset = 0x5C3F00
$caveVa = 0x009C3F00
$sharedCaveLength = 0x100
$caveReserveLength = 0x20
$continuationVa = 0x005DA4C5

$originalHook = Convert-HexBytes '8B 4E 08 8B 01'
$patchedHook = Convert-HexBytes 'E9 3B 9A 3E 00'
$caveCode = Convert-HexBytes @'
85 F6
74 14
8B 4E 08
85 C9
74 0D
83 7E 0C 00
74 07
8B 01
E9 AD 65 C1 FF
C3
'@
$emptyCave = [byte[]]::new($caveReserveLength)
$patchedCave = [byte[]]::new($caveReserveLength)
Copy-Bytes $caveCode $patchedCave 0

$nativePrefix = Convert-HexBytes @'
8B 4F 0C 8B 01 8B 50 60 56 FF D2 5E C2 04 00 CC
'@
$nativeSuffix = Convert-HexBytes @'
8B 50 60 83 EC 18 57 53 FF D2 8B 4E 0C 8B 01 8B
50 60 53 FF D2 84 DB 0F 84 22 01 00 00 8B 46 08
'@

if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
    throw "Client executable not found: $ClientExe"
}
if ($Mode -ne 'Status' -and (Test-TargetClientRunning $ClientExe)) {
    throw 'Close Origin.exe before applying or reverting the QuestView frame guard.'
}

[byte[]]$data = [IO.File]::ReadAllBytes($ClientExe)
if ($data.Length -ne $expectedLength) {
    throw "Unexpected Origin.exe length: $($data.Length)."
}

$pe = Get-PeMetadata $data
if ($pe.Machine -ne $expectedMachine -or
    $pe.OptionalMagic -ne $expectedOptionalMagic -or
    $pe.ImageBase -ne $expectedImageBase) {
    throw 'Origin.exe is not the audited x86 PE32 build.'
}

$hookSection = Resolve-ExecutableSection $pe $hookOffset $patchedHook.Length
$caveSection = Resolve-ExecutableSection $pe $caveOffset $caveReserveLength
if ((Resolve-ExecutableVa $pe $hookOffset $patchedHook.Length) -ne $hookVa -or
    (Resolve-ExecutableVa $pe $caveOffset $caveReserveLength) -ne $caveVa -or
    $caveSection.Name -ne '.rdata' -or
    $caveOffset + $sharedCaveLength -ne
        $caveSection.RawOffset + $caveSection.RawSize) {
    throw 'Origin.exe hook or reserved cave mapping does not match the audited build.'
}
if ($hookSection.Name -ne '.text' -or
    -not (Test-Bytes $data ($hookOffset - $nativePrefix.Length) $nativePrefix) -or
    -not (Test-Bytes $data ($hookOffset + $originalHook.Length) $nativeSuffix)) {
    throw 'Origin.exe QuestView function boundaries do not match the audited build.'
}
if ($caveCode.Length -ne 25 -or
    $patchedHook[0] -ne 0xE9 -or
    (Get-NearBranchTarget $patchedHook 0 $hookVa) -ne $caveVa -or
    -not (Test-Bytes $caveCode 0 (Convert-HexBytes '85 F6')) -or
    (Get-ShortBranchTarget $caveCode 2) -ne 24 -or
    (Get-ShortBranchTarget $caveCode 9) -ne 24 -or
    (Get-ShortBranchTarget $caveCode 15) -ne 24 -or
    $caveCode[24] -ne 0xC3 -or
    $caveCode[19] -ne 0xE9 -or
    (Get-NearBranchTarget $caveCode 19 $caveVa) -ne $continuationVa -or
    -not (Test-Bytes $caveCode 4 (Convert-HexBytes '8B 4E 08')) -or
    -not (Test-Bytes $caveCode 17 (Convert-HexBytes '8B 01'))) {
    throw 'QuestView frame-guard trampoline invariants are invalid.'
}

$hasOriginalHook = Test-Bytes $data $hookOffset $originalHook
$hasPatchedHook = Test-Bytes $data $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $data $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $data $caveOffset $patchedCave
$isOriginal = $hasOriginalHook -and $hasEmptyCave
$isPatched = $hasPatchedHook -and $hasPatchedCave
if (-not $isOriginal -and -not $isPatched) {
    throw 'Origin.exe has an unknown or partially applied QuestView frame-guard state.'
}

$currentState = if ($isPatched) { 'Patched' } else { 'Original' }
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Changed = $false
        State = $currentState
        Sha256 = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveVa = ('0x{0:X8}' -f $caveVa)
        CaveReserveBytes = $caveReserveLength
        GuardedObject = $true
        GuardedRoots = 2
    }
    return
}

$targetIsPatched = $Mode -eq 'Apply'
if (($targetIsPatched -and $isPatched) -or
    (-not $targetIsPatched -and $isOriginal)) {
    [pscustomobject]@{
        Mode = $Mode
        Changed = $false
        State = $currentState
        Sha256 = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveVa = ('0x{0:X8}' -f $caveVa)
        CaveReserveBytes = $caveReserveLength
        GuardedObject = $true
        GuardedRoots = 2
    }
    return
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'backups'
}
$backupDirectory = Join-Path $BackupRoot (
    "origin-quest-view-frame-guard-$Mode-$timestamp")
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backupPath = Join-Path $backupDirectory 'Origin.exe'
if (Test-Path -LiteralPath $backupPath) {
    throw "Backup already exists: $backupPath"
}
Copy-Item -LiteralPath $ClientExe -Destination $backupPath

$beforeHash = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash
$backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
if ($beforeHash -ne $backupHash) {
    throw 'Backup hash verification failed.'
}

[byte[]]$before = $data.Clone()
if ($targetIsPatched) {
    Copy-Bytes $patchedHook $data $hookOffset
    Copy-Bytes $patchedCave $data $caveOffset
} else {
    Copy-Bytes $originalHook $data $hookOffset
    Copy-Bytes $emptyCave $data $caveOffset
}

$allowedRanges = @(
    [pscustomobject]@{ Offset = $hookOffset; Length = $patchedHook.Length },
    [pscustomobject]@{ Offset = $caveOffset; Length = $caveReserveLength }
)
$changedBytes = 0
for ($index = 0; $index -lt $data.Length; $index++) {
    if ($before[$index] -eq $data[$index]) {
        continue
    }
    $changedBytes++
    if (-not (Test-AllowedDifference $index $allowedRanges)) {
        throw "Unexpected planned mutation at file offset 0x$('{0:X}' -f $index)."
    }
}

try {
    [IO.File]::WriteAllBytes($ClientExe, $data)
    [byte[]]$written = [IO.File]::ReadAllBytes($ClientExe)
    $expectedHook = if ($targetIsPatched) { $patchedHook } else { $originalHook }
    $expectedCave = if ($targetIsPatched) { $patchedCave } else { $emptyCave }
    if ($written.Length -ne $expectedLength -or
        -not (Test-Bytes $written $hookOffset $expectedHook) -or
        -not (Test-Bytes $written $caveOffset $expectedCave)) {
        throw 'Post-write byte verification failed.'
    }
    for ($index = 0; $index -lt $written.Length; $index++) {
        if ($before[$index] -ne $written[$index] -and
            -not (Test-AllowedDifference $index $allowedRanges)) {
            throw "Unexpected post-write mutation at file offset 0x$('{0:X}' -f $index)."
        }
    }
}
catch {
    Copy-Item -LiteralPath $backupPath -Destination $ClientExe -Force
    throw "$($_.Exception.Message) Verified backup restored."
}

[pscustomobject]@{
    Mode = $Mode
    Changed = $true
    State = if ($targetIsPatched) { 'Patched' } else { 'Original' }
    ChangedBytes = $changedBytes
    BeforeSha256 = $beforeHash
    AfterSha256 = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash
    Backup = $backupPath
    HookVa = ('0x{0:X8}' -f $hookVa)
    CaveVa = ('0x{0:X8}' -f $caveVa)
    CaveReserveBytes = $caveReserveLength
    GuardedObject = $true
    GuardedRoots = 2
}
