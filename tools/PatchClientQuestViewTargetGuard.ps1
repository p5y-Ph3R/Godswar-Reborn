param(
    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',
    [ValidateSet('Apply', 'Revert')]
    [string]$Mode = 'Apply',
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Convert-HexBytes([string]$Hex) {
    $normalized = $Hex -replace '\s', ''
    if (($normalized.Length -band 1) -ne 0) {
        throw 'Hex text must contain an even number of digits.'
    }

    [byte[]]$result = for ($index = 0; $index -lt $normalized.Length; $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $result
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
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

function Copy-Bytes([byte[]]$Source, [byte[]]$Destination, [int]$Offset) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-PeMetadata([byte[]]$Data) {
    if ($Data.Length -lt 0x100 -or $Data[0] -ne 0x4D -or $Data[1] -ne 0x5A) {
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
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount; $sectionIndex++) {
        $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
        $sections += [pscustomobject]@{
            VirtualAddress = [BitConverter]::ToUInt32($Data, $sectionOffset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $sectionOffset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $sectionOffset + 20)
            Characteristics = [BitConverter]::ToUInt32($Data, $sectionOffset + 36)
        }
    }

    return [pscustomobject]@{
        Machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
        OptionalMagic = [BitConverter]::ToUInt16($Data, $optionalHeaderOffset)
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalHeaderOffset + 28)
        Sections = $sections
    }
}

function Resolve-ExecutableVa([object]$Pe, [int]$FileOffset, [int]$Length) {
    foreach ($section in $Pe.Sections) {
        if ($FileOffset -lt $section.RawOffset -or
            $FileOffset + $Length -gt $section.RawOffset + $section.RawSize) {
            continue
        }
        if (($section.Characteristics -band 0x20000000) -eq 0) {
            throw "File offset 0x$('{0:X}' -f $FileOffset) is not executable."
        }
        return [uint64]$Pe.ImageBase + $section.VirtualAddress +
            ([uint64]$FileOffset - $section.RawOffset)
    }
    throw "File offset 0x$('{0:X}' -f $FileOffset) is outside a PE section."
}

function Get-RelativeTarget([byte[]]$Code, [int]$InstructionOffset, [uint64]$InstructionVa) {
    return [int64]$InstructionVa + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
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

$expectedLength = 6676480
$expectedMachine = 0x014C
$expectedOptionalMagic = 0x010B
$expectedImageBase = 0x00400000

# Target changes call the native QuestView singleton getter and then
# unconditionally dereference both UI roots. A relog/lifecycle race can leave
# either root null. The trampoline preserves the getter call, validates the
# singleton and both roots, and skips only QuestView hide/unregister work when
# it is unsafe. It never tries to load XML from the input/render path.
$hookOffset = 0x093A44
$hookVa = 0x00493A44
$caveOffset = 0x5C3400
$caveVa = 0x009C3400
$normalContinuationVa = 0x00493A49
$safeContinuationVa = 0x00493A74

$originalHook = Convert-HexBytes 'E8 87 63 14 00'
$patchedHook = Convert-HexBytes 'E9 B7 F9 52 00'
$caveCode = Convert-HexBytes @'
E8 CB 69 C1 FF
85 C0
74 11
83 78 08 00
74 0B
83 78 0C 00
74 05
E9 2F 06 AD FF
E9 55 06 AD FF
'@
$emptyCave = [byte[]]::new($caveCode.Length)
$nativeContinuation = Convert-HexBytes @'
8B F0 8B 4E 08 8B 11 8B 42 60 6A 00 FF D0
8B 4E 0C 8B 11 8B 42 60 6A 00 FF D0
8B 0D 54 61 57 01 8B 11 8B 46 08 8B 52 34 50 FF D2
'@
$safeContinuation = Convert-HexBytes 'E8 C7 4A 11 00 8B F0 83 7E 6C 00'

if (Get-Process -Name 'Origin' -ErrorAction SilentlyContinue) {
    throw 'Close Origin.exe before applying or reverting the QuestView guard.'
}
if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
    throw "Client executable not found: $ClientExe"
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
if ((Resolve-ExecutableVa $pe $hookOffset $patchedHook.Length) -ne $hookVa -or
    (Resolve-ExecutableVa $pe $caveOffset $caveCode.Length) -ne $caveVa) {
    throw 'Origin.exe hook or cave mapping does not match the audited build.'
}
if (-not (Test-Bytes $data ($hookOffset + $originalHook.Length) $nativeContinuation) -or
    -not (Test-Bytes $data ($safeContinuationVa - $expectedImageBase) $safeContinuation)) {
    throw 'Origin.exe target-reset continuations do not match the audited build.'
}
if ((Get-RelativeTarget $originalHook 0 $hookVa) -ne 0x005D9DD0 -or
    (Get-RelativeTarget $patchedHook 0 $hookVa) -ne $caveVa -or
    (Get-RelativeTarget $caveCode 0 $caveVa) -ne 0x005D9DD0 -or
    (Get-RelativeTarget $caveCode 21 ($caveVa + 21)) -ne $normalContinuationVa -or
    (Get-RelativeTarget $caveCode 26 ($caveVa + 26)) -ne $safeContinuationVa) {
    throw 'QuestView trampoline targets are invalid.'
}

$hasOriginalHook = Test-Bytes $data $hookOffset $originalHook
$hasPatchedHook = Test-Bytes $data $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $data $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $data $caveOffset $caveCode
$isOriginal = $hasOriginalHook -and $hasEmptyCave
$isPatched = $hasPatchedHook -and $hasPatchedCave
if (-not $isOriginal -and -not $isPatched) {
    throw 'Origin.exe has an unknown or partially applied QuestView guard state.'
}

$targetIsPatched = $Mode -eq 'Apply'
if (($targetIsPatched -and $isPatched) -or (-not $targetIsPatched -and $isOriginal)) {
    [pscustomobject]@{
        Mode = $Mode
        Changed = $false
        State = if ($isPatched) { 'Patched' } else { 'Original' }
        Sha256 = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash
    }
    return
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath "backups\origin-quest-view-target-guard-$Mode-$timestamp"
}
[IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
$backupPath = Join-Path $BackupRoot 'Origin.exe'
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
    Copy-Bytes $caveCode $data $caveOffset
} else {
    Copy-Bytes $originalHook $data $hookOffset
    Copy-Bytes $emptyCave $data $caveOffset
}

$allowedRanges = @(
    [pscustomobject]@{ Offset = $hookOffset; Length = $patchedHook.Length },
    [pscustomobject]@{ Offset = $caveOffset; Length = $caveCode.Length }
)
for ($index = 0; $index -lt $data.Length; $index++) {
    if ($before[$index] -ne $data[$index] -and
        -not (Test-AllowedDifference $index $allowedRanges)) {
        throw "Unexpected planned mutation at file offset 0x$('{0:X}' -f $index)."
    }
}

[IO.File]::WriteAllBytes($ClientExe, $data)
[byte[]]$written = [IO.File]::ReadAllBytes($ClientExe)
$expectedHook = if ($targetIsPatched) { $patchedHook } else { $originalHook }
$expectedCave = if ($targetIsPatched) { $caveCode } else { $emptyCave }
if ($written.Length -ne $expectedLength -or
    -not (Test-Bytes $written $hookOffset $expectedHook) -or
    -not (Test-Bytes $written $caveOffset $expectedCave)) {
    Copy-Item -LiteralPath $backupPath -Destination $ClientExe -Force
    throw 'Post-write verification failed; the verified backup was restored.'
}

for ($index = 0; $index -lt $written.Length; $index++) {
    if ($before[$index] -ne $written[$index] -and
        -not (Test-AllowedDifference $index $allowedRanges)) {
        Copy-Item -LiteralPath $backupPath -Destination $ClientExe -Force
        throw "Unexpected post-write mutation at file offset 0x$('{0:X}' -f $index); backup restored."
    }
}

[pscustomobject]@{
    Mode = $Mode
    Changed = $true
    State = if ($targetIsPatched) { 'Patched' } else { 'Original' }
    BeforeSha256 = $beforeHash
    AfterSha256 = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash
    Backup = $backupPath
    HookVa = ('0x{0:X8}' -f $hookVa)
    CaveVa = ('0x{0:X8}' -f $caveVa)
}
