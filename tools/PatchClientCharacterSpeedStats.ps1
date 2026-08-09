[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

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
        $offset = $sectionTable + ($index * 40)
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString(
                $Data[$offset..($offset + 7)]).Trim([char]0)
            VirtualAddress = [BitConverter]::ToUInt32($Data, $offset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $offset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $offset + 20)
            Characteristics = [BitConverter]::ToUInt32($Data, $offset + 36)
        }
    }
    return [pscustomobject]@{
        Machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
        OptionalMagic = [BitConverter]::ToUInt16($Data, $optionalOffset)
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalOffset + 28)
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
            throw "Origin.exe offset 0x$('{0:X}' -f $FileOffset) is not in the audited executable $ExpectedSection section."
        }
        return [uint64]$Pe.ImageBase + $section.VirtualAddress +
            ([uint64]$FileOffset - $section.RawOffset)
    }
    throw "Origin.exe offset 0x$('{0:X}' -f $FileOffset) is outside a PE section."
}

function Get-NearBranchTarget(
    [byte[]]$Code,
    [int]$InstructionOffset,
    [uint64]$CodeVa
) {
    return [int64]$CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
}

. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Core.ps1')

function Test-TargetClientRunning([string]$ExecutablePath) {
    $target = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try { $path = $process.Path } catch { $path = $null }
        if ($path -and [string]::Equals(
                [IO.Path]::GetFullPath($path), $target,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Write-BytesAtomic([string]$Path, [byte[]]$Data) {
    $temporary = "$Path.speed-stats-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $Data)
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Write-Utf8Atomic([string]$Path, [string]$Text) {
    $temporary = "$Path.speed-stats-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporary, $Text, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

$expectedLength = 6676480
$hookOffset = 0x1B5B97
$hookVa = 0x005B5B97
$caveOffset = 0x5C3F20
$caveVa = 0x009C3F20
$caveReserveLength = 0x80
$epilogueVa = 0x005B5BD4
$originalHook = Convert-HexBytes 'A1 AC 5E 57 01'
$patchedHook = Convert-HexBytes 'E9 84 E3 40 00'
$caveCode = Convert-HexBytes @'
A1 AC 5E 57 01 6A 64 D9 80 8C 02 00 00 DA 0C 24
DB 1C 24 8D 54 24 2C 68 3C 44 95 00 52 FF 15 D0
C3 91 00 83 C4 0C 8B 8E 80 01 00 00 8B 11 8B 92
84 00 00 00 8D 44 24 28 50 FF D2 A1 AC 5E 57 01
6A 64 D9 80 90 02 00 00 DA 0C 24 DB 1C 24 8D 54
24 2C 68 3C 44 95 00 52 FF 15 D0 C3 91 00 83 C4
0C 8B 8E 7C 01 00 00 8B 11 8B 92 84 00 00 00 8D
44 24 28 50 FF D2 E9 39 1C BF FF
'@
$emptyCave = [byte[]]::new($caveReserveLength)
$patchedCave = [byte[]]::new($caveReserveLength)
Copy-Bytes $caveCode $patchedCave 0
$nativePrefix = Convert-HexBytes @'
68 3C 44 95 00 52 FF D7 8B 8E 5C 01 00 00 8B 01
8B 80 84 00 00 00 83 C4 0C 8D 54 24 28 52 FF D0
'@
$nativeSuffix = Convert-HexBytes @'
80 B8 8C 07 00 00 02 75 44 68 08 02 00 00 8D 4C
24 2C 51 6A FF 05 8D 07 00 00 50 6A 00 6A 00 FF
'@
$questEmpty = [byte[]]::new(0x20)
$questPatched = [byte[]]::new(0x20)
Copy-Bytes (Convert-HexBytes @'
85 F6 74 14 8B 4E 08 85 C9 74 0D 83 7E 0C 00 74
07 8B 01 E9 AD 65 C1 FF C3
'@) $questPatched 0

$clientRootPath = [IO.Path]::GetFullPath($ClientRoot)
$clientExe = Join-Path $clientRootPath 'Origin.exe'
$xmlPaths = [ordered]@{
    en_us = Join-Path $clientRootPath 'Localization\en_us\UI\XML\PersonalInfoUI.xml'
    zh_cn = Join-Path $clientRootPath 'Localization\zh_cn\UI\XML\PersonalInfoUI.xml'
}
$luaPaths = [ordered]@{
    en_us = Join-Path $clientRootPath 'Localization\en_us\UI\XML\PersonalInfoSpeedStats.lua'
    zh_cn = Join-Path $clientRootPath 'Localization\zh_cn\UI\XML\PersonalInfoSpeedStats.lua'
}
if (-not (Test-Path -LiteralPath $clientExe -PathType Leaf)) {
    throw "Origin.exe is missing: $clientExe"
}
foreach ($entry in $xmlPaths.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "PersonalInfoUI.xml is missing for $($entry.Key): $($entry.Value)"
    }
}
if ($Mode -ne 'Status' -and (Test-TargetClientRunning $clientExe)) {
    throw 'Close Origin.exe before changing the character speed-stat UI.'
}

[byte[]]$data = [IO.File]::ReadAllBytes($clientExe)
if ($data.Length -ne $expectedLength) {
    throw "Unexpected Origin.exe length: $($data.Length)."
}
$pe = Get-PeMetadata $data
if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
    $pe.ImageBase -ne 0x00400000 -or
    (Resolve-ExecutableVa $pe $hookOffset 5 '.text') -ne $hookVa -or
    (Resolve-ExecutableVa $pe $caveOffset $caveReserveLength '.rdata') -ne
        $caveVa) {
    throw 'Origin.exe is not the audited x86 PE32 build.'
}
if (-not (Test-Bytes $data ($hookOffset - $nativePrefix.Length) $nativePrefix) -or
    -not (Test-Bytes $data ($hookOffset + 5) $nativeSuffix)) {
    throw 'Origin.exe PersonalInfo update boundaries do not match the audited build.'
}
if (-not (Test-Bytes $data 0x5C3F00 $questEmpty) -and
    -not (Test-Bytes $data 0x5C3F00 $questPatched)) {
    throw 'The shared client cave has an unknown QuestView-owner state.'
}
if ($caveCode.Length -ne 123 -or
    (Get-NearBranchTarget $patchedHook 0 $hookVa) -ne $caveVa -or
    (Get-NearBranchTarget $caveCode 118 $caveVa) -ne $epilogueVa -or
    -not (Test-Bytes $caveCode 7 (Convert-HexBytes 'D9 80 8C 02 00 00')) -or
    -not (Test-Bytes $caveCode 66 (Convert-HexBytes 'D9 80 90 02 00 00'))) {
    throw 'Character speed-stat trampoline invariants are invalid.'
}

$hasOriginalBinary =
    (Test-Bytes $data $hookOffset $originalHook) -and
    (Test-Bytes $data $caveOffset $emptyCave)
$hasPatchedBinary =
    (Test-Bytes $data $hookOffset $patchedHook) -and
    (Test-Bytes $data $caveOffset $patchedCave)
if (-not $hasOriginalBinary -and -not $hasPatchedBinary) {
    throw 'Origin.exe has an unknown or partially applied character speed-stat state.'
}
$binaryState = if ($hasPatchedBinary) { 'Patched' } else { 'Original' }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$xmlText = [ordered]@{}
$xmlStates = @()
$luaStates = @()
foreach ($entry in $xmlPaths.GetEnumerator()) {
    $text = [IO.File]::ReadAllText($entry.Value, $utf8)
    $xmlText[$entry.Key] = $text
    $xmlState = Get-PersonalInfoXmlState $text
    if ($xmlState -eq 'PatchedV1') {
        $movementFull = Get-SpeedFullLabel $entry.Key $true
        $ridingFull = Get-SpeedFullLabel $entry.Key $false
        if (-not $text.Contains("Text=`"$movementFull`" Visible=`"1`"") -or
            -not $text.Contains("Text=`"$ridingFull`" />")) {
            throw "PersonalInfoUI.xml has the wrong v1 labels for $($entry.Key)."
        }
    }
    if ($xmlState -in 'PatchedV2', 'PatchedV3') {
        $movementCompact = Get-SpeedCompactLabel $entry.Key $true
        $ridingCompact = Get-SpeedCompactLabel $entry.Key $false
        if (-not $text.Contains("Text=`"$movementCompact`" Visible=`"1`" CanHovered=`"1`"") -or
            -not $text.Contains("Text=`"$ridingCompact`" CanHovered=`"1`"") -or
            -not $text.Contains(
                "./Localization/$($entry.Key)/UI/XML/PersonalInfoSpeedStats.lua")) {
            throw "PersonalInfoUI.xml has the wrong localized speed labels for $($entry.Key)."
        }
    }
    $xmlStates += $xmlState
    $luaPath = $luaPaths[$entry.Key]
    if (-not (Test-Path -LiteralPath $luaPath -PathType Leaf)) {
        $luaStates += 'Original'
        continue
    }
    $actualLua = [IO.File]::ReadAllText($luaPath, $utf8)
    if ($actualLua -ne (Get-PersonalInfoSpeedLua $entry.Key)) {
        throw "PersonalInfoSpeedStats.lua has unknown content for $($entry.Key)."
    }
    $luaStates += 'Patched'
}
if (@($xmlStates | Select-Object -Unique).Count -ne 1 -or
    @($luaStates | Select-Object -Unique).Count -ne 1) {
    throw 'The character speed-stat binary and localized UI files are partially applied.'
}
$state = if ($binaryState -eq 'Original' -and
    $xmlStates[0] -eq 'Original' -and $luaStates[0] -eq 'Original') {
    'Original'
} elseif ($binaryState -eq 'Patched' -and
    $xmlStates[0] -eq 'PatchedV1' -and $luaStates[0] -eq 'Original') {
    'PatchedV1'
} elseif ($binaryState -eq 'Patched' -and
    $xmlStates[0] -eq 'PatchedV2' -and $luaStates[0] -eq 'Patched') {
    'PatchedV2'
} elseif ($binaryState -eq 'Patched' -and
    $xmlStates[0] -eq 'PatchedV3' -and $luaStates[0] -eq 'Patched') {
    'PatchedV3'
} else {
    throw 'The character speed-stat binary, XML, and hover scripts are partially applied.'
}

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Changed = $false
        State = $state
        Sha256 = (Get-FileHash $clientExe -Algorithm SHA256).Hash
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveVa = ('0x{0:X8}' -f $caveVa)
        CaveReserveBytes = $caveReserveLength
        WindowRectangle = switch ($state) {
            'PatchedV2' { '100,100,363,692' }
            'PatchedV3' { '100,100,363,652' }
            default { '100,100,363,626' }
        }
        HoverImplementation = if ($state -in 'PatchedV2', 'PatchedV3') {
            'localized Lua helper'
        } else { 'none' }
        MovementWireOffset = 56
        RidingWireOffset = 60
        Locales = @($xmlPaths.Keys)
    }
    return
}

$targetPatched = $Mode -eq 'Apply'
if (($targetPatched -and $state -eq 'PatchedV3') -or
    (-not $targetPatched -and $state -eq 'Original')) {
    [pscustomobject]@{ Mode = $Mode; Changed = $false; State = $state }
    return
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'backups'
}
$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-character-speed-stats-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backupExe = Join-Path $backupDirectory 'Origin.exe'
Copy-Item -LiteralPath $clientExe -Destination $backupExe
$backupXml = [ordered]@{}
$backupLua = [ordered]@{}
foreach ($entry in $xmlPaths.GetEnumerator()) {
    $backup = Join-Path $backupDirectory (
        'PersonalInfoUI.' + $entry.Key + '.xml')
    Copy-Item -LiteralPath $entry.Value -Destination $backup
    $backupXml[$entry.Key] = $backup
    $luaPath = $luaPaths[$entry.Key]
    if (Test-Path -LiteralPath $luaPath -PathType Leaf) {
        $luaBackup = Join-Path $backupDirectory (
            'PersonalInfoSpeedStats.' + $entry.Key + '.lua')
        Copy-Item -LiteralPath $luaPath -Destination $luaBackup
        $backupLua[$entry.Key] = $luaBackup
    } else {
        $backupLua[$entry.Key] = $null
    }
}

if ($targetPatched) {
    Copy-Bytes $patchedHook $data $hookOffset
    Copy-Bytes $patchedCave $data $caveOffset
} else {
    Copy-Bytes $originalHook $data $hookOffset
    Copy-Bytes $emptyCave $data $caveOffset
}
$targetXml = [ordered]@{}
foreach ($entry in $xmlPaths.GetEnumerator()) {
    $targetXml[$entry.Key] = Convert-PersonalInfoXml (
        $xmlText[$entry.Key]) $entry.Key $targetPatched
    $expectedXmlState = if ($targetPatched) { 'PatchedV3' } else { 'Original' }
    if ((Get-PersonalInfoXmlState $targetXml[$entry.Key]) -ne
        $expectedXmlState) {
        throw "Generated $($entry.Key) UI state is invalid."
    }
}

try {
    Write-BytesAtomic $clientExe $data
    foreach ($entry in $xmlPaths.GetEnumerator()) {
        Write-Utf8Atomic $entry.Value $targetXml[$entry.Key]
        $luaPath = $luaPaths[$entry.Key]
        if ($targetPatched) {
            Write-Utf8Atomic $luaPath (Get-PersonalInfoSpeedLua $entry.Key)
        } elseif (Test-Path -LiteralPath $luaPath -PathType Leaf) {
            Remove-Item -LiteralPath $luaPath -Force
        }
    }
    $verify = & $PSCommandPath -ClientRoot $clientRootPath -Mode Status
    $expectedState = if ($targetPatched) { 'PatchedV3' } else { 'Original' }
    if ($verify.State -ne $expectedState) {
        throw 'Character speed-stat post-write verification failed.'
    }
}
catch {
    Copy-Item -LiteralPath $backupExe -Destination $clientExe -Force
    foreach ($entry in $xmlPaths.GetEnumerator()) {
        Copy-Item -LiteralPath $backupXml[$entry.Key] `
            -Destination $entry.Value -Force
        $luaPath = $luaPaths[$entry.Key]
        if ($null -ne $backupLua[$entry.Key]) {
            Copy-Item -LiteralPath $backupLua[$entry.Key] `
                -Destination $luaPath -Force
        } elseif (Test-Path -LiteralPath $luaPath -PathType Leaf) {
            Remove-Item -LiteralPath $luaPath -Force
        }
    }
    throw
}

[pscustomobject]@{
    Mode = $Mode
    Changed = $true
    State = if ($targetPatched) { 'PatchedV3' } else { 'Original' }
    Backup = $backupDirectory
    Sha256 = (Get-FileHash $clientExe -Algorithm SHA256).Hash
    MovementDisplay = 'current authoritative locomotion multiplier'
    RidingDisplay = 'equipped mount bonus'
}
