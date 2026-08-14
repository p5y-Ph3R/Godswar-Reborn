[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-HexBytes([string]$Hex) {
    $compact = $Hex -replace '[^0-9A-Fa-f]', ''
    if (($compact.Length % 2) -ne 0) {
        throw 'Internal Phoenix refresh hex contains an incomplete byte.'
    }
    [byte[]]$result = for ($offset = 0; $offset -lt $compact.Length; $offset += 2) {
        [Convert]::ToByte($compact.Substring($offset, 2), 16)
    }
    return ,$result
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
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

function Assert-OriginClosed([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $liveDefault = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $null
            try {
                $processPath = $process.Path
            }
            catch {
                # An elevated Origin can hide its image path from this shell.
            }
            if (-not [string]::IsNullOrWhiteSpace($processPath)) {
                if ([string]::Equals(
                        [IO.Path]::GetFullPath($processPath),
                        $resolved,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Close Origin.exe before changing the Phoenix refresh.'
                }
            }
            elseif ([string]::Equals(
                    $resolved,
                    $liveDefault,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Close Origin.exe before changing the Phoenix refresh.'
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

function Get-RelativeTarget(
    [byte[]]$Code,
    [int]$InstructionOffset,
    [uint64]$CodeVa,
    [byte]$Opcode
) {
    if ($InstructionOffset -lt 0 -or
        $InstructionOffset + 5 -gt $Code.Length -or
        $Code[$InstructionOffset] -ne $Opcode) {
        throw 'Internal Phoenix refresh relative instruction is malformed.'
    }
    return $CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
}

$sourceSha256 =
    '9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728'
$modelOnlySha256 =
    '31B4CE0E0445958C7814BCD2572381F9115DE194E0E13CB3ED7502F02C9FB9B2'
$detailOnlySha256 =
    'C642C3F9F4F3458BC4DBAD126E06C1661C7F1C418FB63BD037543CA1892D5656'
$visibleOnlyPatchedSha256 =
    '7B837397F5387186001B7CB155FBADD2B3AA2CA425B7568A21F9C66EDA90A8DA'
$patchedSha256 =
    '39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B'
$expectedLength = 6676480

$hookOffset = 0x2A195C
$oldCaveOffset = 0x5C3480
$oldCaveVa = 0x009C3480
$redrawCaveOffset = 0x5C3658
$redrawCaveVa = 0x009C3658
$redrawReserveLength = 72
$continuationVa = 0x006A1967
$redrawWrapperOffset = 0x1C4E60
$redrawWrapperVa = 0x005C4E60
$mergeRefreshOffset = 0x16DA60
$mergeRefreshVa = 0x0056DA60

$hook = Convert-HexBytes @'
E9 1F 1B 32 00 90 90 90 90 90 90
'@
$sourceCave = Convert-HexBytes @'
9C 66 83 3E 2C 75 16 51 56 57 83 C6 14 81 C7 84
00 00 00 B9 06 00 00 00 F3 A5 5F 5E 59 9D C7 84
24 E4 00 00 00 07 00 00 00 E9 B9 E4 CD FF 00 00
'@
$modelOnlyCave = Convert-HexBytes @'
9C 66 83 3E 44 75 16 51 56 57 83 C6 14 81 C7 84
00 00 00 B9 0C 00 00 00 F3 A5 5F 5E 59 9D C7 84
24 E4 00 00 00 07 00 00 00 E9 B9 E4 CD FF 00 00
'@
$patchedCave = Convert-HexBytes @'
9C 66 83 3E 44 75 16 51 56 57 83 C6 14 81 C7 84
00 00 00 B9 0C 00 00 00 F3 A5 5F 5E 59 9D C7 84
24 E4 00 00 00 07 00 00 00 E9 AA 01 00 00 00 00
'@
$detailOnlyRedrawCode = Convert-HexBytes @'
9C 50 51 52 8B 0D 84 D0 5A 01 85 C9 74 05 E8 F5
17 C0 FF 5A 59 58 9D E9 F3 E2 CD FF
'@
$visibleOnlyRedrawCode = Convert-HexBytes @'
9C 50 51 52 8B 0D 84 D0 5A 01 85 C9 74 05 E8 F5
17 C0 FF A1 98 D0 5A 01 85 C0 74 15 8B 48 04 85
C9 74 0E 80 B9 0D 01 00 00 00 74 05 E8 D7 A3 BA
FF 5A 59 58 9D E9 D5 E2 CD FF
'@
$redrawCode = Convert-HexBytes @'
9C 50 51 52 8B 0D 84 D0 5A 01 85 C9 74 05 E8 F5
17 C0 FF A1 98 D0 5A 01 85 C0 74 15 8B 48 04 85
C9 74 0E 80 B9 0D 01 00 00 00 90 90 E8 D7 A3 BA
FF 5A 59 58 9D E9 D5 E2 CD FF
'@
$emptyRedrawCave = [byte[]]::new($redrawReserveLength)
$detailOnlyRedrawCave = [byte[]]::new($redrawReserveLength)
[Array]::Copy(
    $detailOnlyRedrawCode,
    $detailOnlyRedrawCave,
    $detailOnlyRedrawCode.Length)
$visibleOnlyRedrawCave = [byte[]]::new($redrawReserveLength)
Copy-Bytes $visibleOnlyRedrawCode $visibleOnlyRedrawCave 0
$installedRedrawCave = [byte[]]::new($redrawReserveLength)
Copy-Bytes $redrawCode $installedRedrawCave 0
$nextNativeCode = Convert-HexBytes @'
0F B7 46 3A 3D E0 01 00 00 0F 82 6C 00 00 00 3D
'@
$redrawWrapper = Convert-HexBytes @'
8B 41 04 85 C0 74 0F 80 B8 0D 01 00 00 00 74 06
51 E8 4A F8 FF FF C3
'@
$mergeRefresh = Convert-HexBytes @'
55 8B EC 83 E4 F8 51 57 8B F8 E8 21 00 00 00 57
E8 2B 05 00 00 57 E8 C5 08 00 00 8B C7 E8 AE 0B
00 00
'@

if ($hook.Length -ne 11 -or
    $sourceCave.Length -ne 48 -or
    $modelOnlyCave.Length -ne 48 -or
    $patchedCave.Length -ne 48 -or
    $detailOnlyRedrawCode.Length -ne 28 -or
    $visibleOnlyRedrawCode.Length -ne 58 -or
    $redrawCode.Length -ne 58) {
    throw 'Internal Phoenix refresh code length validation failed.'
}
if ((Get-RelativeTarget $patchedCave 41 $oldCaveVa 0xE9) -ne
        $redrawCaveVa -or
    (Get-RelativeTarget $redrawCode 14 $redrawCaveVa 0xE8) -ne
        $redrawWrapperVa -or
    (Get-RelativeTarget $redrawCode 44 $redrawCaveVa 0xE8) -ne
        $mergeRefreshVa -or
    (Get-RelativeTarget $redrawCode 53 $redrawCaveVa 0xE9) -ne
        $continuationVa -or
    $redrawCaveVa + 14 + [int][sbyte]$redrawCode[13] -ne
        $redrawCaveVa + 19 -or
    $redrawCaveVa + 28 + [int][sbyte]$redrawCode[27] -ne
        $redrawCaveVa + 49 -or
    $redrawCaveVa + 35 + [int][sbyte]$redrawCode[34] -ne
        $redrawCaveVa + 49) {
    throw 'Internal Phoenix refresh branch validation failed.'
}

function Get-State([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Origin client was not found: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $expectedLength) {
        throw "Unsupported Origin.exe length $($file.Length)."
    }

    $hash = Get-Sha256 $Path
    $identity = switch ($hash) {
        $sourceSha256 {
            [pscustomobject]@{
                State = 'Ready'; PetDetailRedraw = $false
                PetMergeRedraw = $false; HiddenPetMergeRefresh = $false
                Cave = $sourceCave
                Redraw = $emptyRedrawCave
            }
            break
        }
        $modelOnlySha256 {
            [pscustomobject]@{
                State = 'Model refresh only'; PetDetailRedraw = $false
                PetMergeRedraw = $false; HiddenPetMergeRefresh = $false
                Cave = $modelOnlyCave
                Redraw = $emptyRedrawCave
            }
            break
        }
        $detailOnlySha256 {
            [pscustomobject]@{
                State = 'Pet Detail redraw only'; PetDetailRedraw = $true
                PetMergeRedraw = $false; HiddenPetMergeRefresh = $false
                Cave = $patchedCave
                Redraw = $detailOnlyRedrawCave
            }
            break
        }
        $visibleOnlyPatchedSha256 {
            [pscustomobject]@{
                State = 'Visible Pet Merge redraw only'
                PetDetailRedraw = $true; PetMergeRedraw = $true
                HiddenPetMergeRefresh = $false; Cave = $patchedCave
                Redraw = $visibleOnlyRedrawCave
            }
            break
        }
        $patchedSha256 {
            [pscustomobject]@{
                State = 'Patched'; PetDetailRedraw = $true
                PetMergeRedraw = $true; HiddenPetMergeRefresh = $true
                Cave = $patchedCave
                Redraw = $installedRedrawCave
            }
            break
        }
        default { throw "Unsupported Origin.exe SHA-256/state: $hash" }
    }

    $bytes = [IO.File]::ReadAllBytes($Path)
    if (-not (Test-Bytes $bytes $hookOffset $hook) -or
        -not (Test-Bytes $bytes $oldCaveOffset $identity.Cave) -or
        -not (Test-Bytes $bytes $redrawCaveOffset $identity.Redraw) -or
        -not (Test-Bytes $bytes 0x5C34B0 $nextNativeCode) -or
        -not (Test-Bytes $bytes $redrawWrapperOffset $redrawWrapper) -or
        -not (Test-Bytes $bytes $mergeRefreshOffset $mergeRefresh)) {
        throw 'Origin.exe failed the exact Phoenix refresh layout guard.'
    }

    [pscustomobject]@{
        State = $identity.State
        PetDetailRedraw = $identity.PetDetailRedraw
        PetMergeRedraw = $identity.PetMergeRedraw
        HiddenPetMergeRefresh = $identity.HiddenPetMergeRefresh
        HasAnyRedraw = $identity.PetDetailRedraw -or
            $identity.PetMergeRedraw
        Cave = $identity.Cave
        Redraw = $identity.Redraw
        Hash = $hash
        Bytes = $bytes
    }
}

function Assert-OnlyPatchRanges(
    [byte[]]$Before,
    [byte[]]$After,
    [int]$ExpectedMutationCount
) {
    $mutationCount = 0
    for ($offset = 0; $offset -lt $Before.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) {
            continue
        }
        $mutationCount++
        $inOldCave = $offset -ge $oldCaveOffset -and
            $offset -lt $oldCaveOffset + $patchedCave.Length
        $inRedrawCave = $offset -ge $redrawCaveOffset -and
            $offset -lt $redrawCaveOffset + $redrawReserveLength
        if (-not $inOldCave -and -not $inRedrawCave) {
            throw "Phoenix refresh changed unexpected offset 0x$($offset.ToString('X'))."
        }
    }
    if ($mutationCount -ne $ExpectedMutationCount) {
        throw "Phoenix refresh changed $mutationCount bytes; expected $ExpectedMutationCount."
    }
}

$resolvedClient = [IO.Path]::GetFullPath($ClientExe)
$current = Get-State $resolvedClient
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Status = $current.State
        PacketLength = if ($current.State -eq 'Ready') { 44 } else { 68 }
        RefreshedFields = if ($current.State -eq 'Ready') {
            '6 Basic Savvy'
        }
        else {
            '6 Basic Savvy + 6 Added Value'
        }
        PetDetailRedraw = $current.PetDetailRedraw
        PetMergeRedraw = $current.PetMergeRedraw
        HiddenPetMergeRefresh = $current.HiddenPetMergeRefresh
        Hash = $current.Hash
    }
    return
}

Assert-OriginClosed $resolvedClient
$wantPatched = $Mode -eq 'Apply'
if (($wantPatched -and $current.HiddenPetMergeRefresh) -or
    (-not $wantPatched -and -not $current.HasAnyRedraw)) {
    [pscustomobject]@{
        Mode = $Mode
        Status = if ($wantPatched) { 'Already patched' } else { 'Already reverted' }
        Hash = $current.Hash
    }
    return
}

$targetHash = if ($wantPatched) { $patchedSha256 } else { $modelOnlySha256 }
$targetCave = if ($wantPatched) { $patchedCave } else { $modelOnlyCave }
$targetRedraw = if ($wantPatched) {
    $installedRedrawCave
}
else {
    $emptyRedrawCave
}
$expectedMutationCount = 0
foreach ($pair in @(
        @($current.Cave, $targetCave),
        @($current.Redraw, $targetRedraw))) {
    for ($offset = 0; $offset -lt $pair[0].Length; $offset++) {
        if ($pair[0][$offset] -ne $pair[1][$offset]) {
            $expectedMutationCount++
        }
    }
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-pet-savvy-growth-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$backup = Join-Path $backupDirectory 'Origin.exe'
$stage = "$resolvedClient.$([guid]::NewGuid().ToString('N')).stage"

Copy-Item -LiteralPath $resolvedClient -Destination $backup
if ((Get-Sha256 $backup) -ne $current.Hash) {
    throw 'Phoenix refresh backup failed SHA-256 verification.'
}

try {
    $output = [byte[]]$current.Bytes.Clone()
    Copy-Bytes $targetCave $output $oldCaveOffset
    Copy-Bytes $targetRedraw $output $redrawCaveOffset
    Assert-OnlyPatchRanges $current.Bytes $output $expectedMutationCount

    [IO.File]::WriteAllBytes($stage, $output)
    if ((Get-Sha256 $stage) -ne $targetHash) {
        throw 'Staged Phoenix refresh failed exact SHA-256 verification.'
    }

    Assert-OriginClosed $resolvedClient
    [IO.File]::Copy($stage, $resolvedClient, $true)
    $installed = Get-State $resolvedClient
    if ($installed.Hash -ne $targetHash) {
        throw 'Installed Phoenix refresh failed exact state verification.'
    }
}
catch {
    $installError = $_
    try {
        [IO.File]::Copy($backup, $resolvedClient, $true)
        if ((Get-Sha256 $resolvedClient) -ne $current.Hash) {
            throw 'restored Origin.exe differs from its verified backup'
        }
    }
    catch {
        throw "Phoenix refresh and rollback failed: $installError; $_"
    }
    throw "Phoenix refresh failed; verified predecessor restored: $installError"
}
finally {
    Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    Mode = $Mode
    Status = if ($wantPatched) { 'Patched' } else { 'Reverted' }
    PacketLength = 68
    RefreshedFields = '6 Basic Savvy + 6 Added Value'
    PetDetailRedraw = $wantPatched
    PetMergeRedraw = $wantPatched
    HiddenPetMergeRefresh = $wantPatched
    Backup = $backupDirectory
    Hash = Get-Sha256 $resolvedClient
}
