[CmdletBinding()]
param(
    [string]$FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot `
    'PatchClientPetSoulOwnerMergeRefresh.ps1'
$genderPatcher = Join-Path $PSScriptRoot `
    'PatchClientPetGenderRefresh.ps1'
$cooldownPatcher = Join-Path $PSScriptRoot `
    'PatchClientBagConsumableCooldown.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'godswar-pet-soul-owner-merge-refresh-' +
    [guid]::NewGuid().ToString('N'))
$client = Join-Path $testRoot 'Origin.exe'
$partial = Join-Path $testRoot 'Origin-partial.exe'
$backups = Join-Path $testRoot 'backups'
$sourceHash =
    '7D1F17A21B0D34DA8BE61C639D72BFB4A518A2F3B0B3B0001699ADC560FA0021'
$previousPatchedHash =
    'D6472178B58B75334E344EEA3AFF4884D350C62B4A0F082F41ECCA1489A29FF8'
$patchedHash =
    '48420B7AE83AD3DE17E33E22D270FC30B7E3656D6F070BADDD52761AAB4418BB'
$c1ceHash =
    'C1CE0273504AB3E8020FD2EB2692351FFA0094F6A103719EB8970FD98C3DB2B6'
$s1Hash =
    '00ED99F0EADB605059CB7A0FA476922EC6EA9E3EAE9218710C20299992706BDB'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Hex([string]$Value) {
    $compact = $Value -replace '[^0-9A-Fa-f]', ''
    [byte[]]$bytes = for ($index = 0; $index -lt $compact.Length;
        $index += 2) {
        [Convert]::ToByte($compact.Substring($index, 2), 16)
    }
    return ,$bytes
}

function Test-Bytes([byte[]]$Data, [int]$Offset, [byte[]]$Expected) {
    if ($Offset + $Expected.Length -gt $Data.Length) { return $false }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Data[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Test-SameBytes([byte[]]$Left, [byte[]]$Right) {
    [Linq.Enumerable]::SequenceEqual($Left, $Right)
}

function Relative-Target(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va
) {
    [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-RelativeCaveXrefs([byte[]]$Data) {
    $result = @()
    # This fixed-base image maps raw offsets directly to RVA. Both .text
    # (through 0x51BFFF) and .rdata (through 0x5C3FFF) are executable.
    for ($offset = 0x1000; $offset -le 0x5C3FFB; $offset++) {
        if ($Data[$offset] -ne 0xE8 -and $Data[$offset] -ne 0xE9) {
            continue
        }
        $target = 0x00400000 + $offset + 5 +
            [BitConverter]::ToInt32($Data, $offset + 1)
        if ($target -ge 0x009C3366 -and $target -lt 0x009C3400) {
            $result += [pscustomobject]@{ Offset = $offset; Target = $target }
        }
    }
    return $result
}

function Get-AbsoluteCaveReferences([byte[]]$Data) {
    $result = @()
    for ($offset = 0; $offset -le $Data.Length - 4; $offset++) {
        $value = [BitConverter]::ToUInt32($Data, $offset)
        if ($value -ge 0x009C3366 -and $value -lt 0x009C3400) {
            $result += [pscustomobject]@{ Offset = $offset; Target = $value }
        }
    }
    return $result
}

function Assert-OnlyS3Differences(
    [byte[]]$Before,
    [byte[]]$After,
    [int]$ExpectedCount
) {
    $count = 0
    for ($offset = 0; $offset -lt $Before.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) { continue }
        $count++
        $allowed = ($offset -ge 0x2A11B4 -and
                $offset -lt 0x2A11BA) -or
            ($offset -ge 0x5C3366 -and $offset -lt 0x5C3400)
        if (-not $allowed) {
            throw "Unexpected S3 mutation at 0x$($offset.ToString('X'))."
        }
    }
    Assert-Equal $count $ExpectedCount 'exact S2-to-S3 changed-byte count'
}

function New-PreviousPatchFixture([byte[]]$Source) {
    [byte[]]$result = $Source.Clone()
    [byte[]]$previousWrapper = Hex @'
88 90 B9 00 00 00 9C 60 8B E8 A1 A4 D0 5A 01 85
C0 74 0E 39 68 04 75 09 8B C8 8B C5 E8 89 36 C0
FF 61 9D C3
'@
    [Array]::Copy((Hex 'E8 AD 21 32 00 90'), 0, $result, 0x2A11B4, 6)
    [Array]::Clear($result, 0x5C3366, 154)
    [Array]::Copy($previousWrapper, 0, $result, 0x5C3366,
        $previousWrapper.Length)
    return ,$result
}

function Get-ByteSha256([byte[]]$Data) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Data)).Replace('-', '') }
    finally { $sha.Dispose() }
}

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Supported Origin.exe fixture not found: $FixtureExe"
    }
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Copy-Item -LiteralPath $FixtureExe -Destination $client
    $fixtureHash = (Get-FileHash $client -Algorithm SHA256).Hash
    if ($fixtureHash -eq $patchedHash) {
        & $patcher -Mode Revert -ClientExe $client `
            -BackupRoot $backups | Out-Null
        $fixtureHash = $sourceHash
    }
    if ($fixtureHash -eq $previousPatchedHash) {
        & $patcher -Mode Revert -ClientExe $client `
            -BackupRoot $backups | Out-Null
        $fixtureHash = $sourceHash
    }
    if ($fixtureHash -eq $c1ceHash) {
        & $genderPatcher -Mode Apply -ClientExe $client `
            -BackupRoot $backups | Out-Null
        $fixtureHash = $s1Hash
    }
    if ($fixtureHash -eq $s1Hash) {
        & $cooldownPatcher -Mode Apply -ClientExe $client `
            -BackupRoot $backups | Out-Null
        $fixtureHash = $sourceHash
    }
    if ($fixtureHash -ne $sourceHash) {
        throw "Fixture has unsupported SHA-256 $fixtureHash."
    }
    Assert-Equal (Get-FileHash $client -Algorithm SHA256).Hash `
        $sourceHash 'exact S2 predecessor'

    $ready = & $patcher -Mode Status -ClientExe $client
    Assert-Equal $ready.Status 'Ready to apply' 'S2 status'
    Assert-Equal $ready.ResultOpcode 10271 'result opcode'
    Assert-Equal $ready.ActivePetStageStore $true 'stage store retained'
    Assert-Equal $ready.SamePetGuard $false `
        'S2 has no owner-Merge same-pet guard'
    Assert-Equal $ready.VisibleOwnerMergeRefresh $false `
        'S2 has no owner-Merge refresh'
    Assert-Equal $ready.CaveInboundRelativeXrefs 0 `
        'S2 has no inbound S3 cave xrefs'

    [byte[]]$before = [IO.File]::ReadAllBytes($client)
    [byte[]]$previousFixture = New-PreviousPatchFixture $before
    Assert-Equal (Get-ByteSha256 $previousFixture) `
        $previousPatchedHash 'exact previous-patch fixture hash'
    Assert-True (Test-Bytes $before 0x2A11A0 (Hex @'
8B C1 8D 48 08 8B 40 1C E8 B3 4F 00 00 8B 4C 24
04 8A 51 04 88 90 B9 00 00 00 C2 04 00
'@)) 'stock 10271 handler resolves pet, stores stage, and returns 4'
    Assert-True (Test-Bytes $before 0x5C3366 ([byte[]]::new(154))) `
        'S3 cave starts empty'
    Assert-True (Test-Bytes $before 0x1C6A10 (Hex @'
51 89 41 04 8B 01 85 C0 74 0F 80 B8 0D 01 00 00
00 74 06 51 E8 57 FC FF FF 59 C3
'@)) 'stock setter stores pet and only recomputes a visible Unite window'
    Assert-True (Test-Bytes $before 0x2A6231 (Hex @'
8B 4C 24 28 3B 08 74 09 05 C0 00 00 00 3B C7 75 EF
'@)) 'native pet lookup proves record offset zero is the pet ID'
    Assert-True (Test-Bytes $before 0x1B6AD6 (Hex @'
E8 45 BB EC FF 8B C8 8B 43 6C E8 2B FF 00 00
'@)) 'Unite open passes the Pet Detail selected pet ID to its setter'
    Assert-Equal @(Get-RelativeCaveXrefs $before).Count 0 `
        'S2 has no relative cave xrefs'
    Assert-Equal @(Get-AbsoluteCaveReferences $before).Count 0 `
        'S2 has no absolute pointer-shaped cave references'

    [byte[]]$s1Gender = $before[0x5C341F..0x5C3484]
    [byte[]]$s1Appearance = $before[0x5C3485..0x5C38E0]
    [byte[]]$s2ResponseHook = $before[0x0EB968..0x0EB96C]
    [byte[]]$s2RequestHook = $before[0x17428E..0x174292]
    [byte[]]$s2Cave = $before[0x51BF67..0x51BFFF]

    $applied = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.Hash $patchedHash 'exact S3 successor'
    Assert-Equal $applied.ChangedBytes 40 'minimal changed-byte count'
    Assert-Equal $applied.VisibleOwnerMergeRefresh $true `
        'owner-Merge refresh enabled'

    [byte[]]$after = [IO.File]::ReadAllBytes($client)
    Assert-OnlyS3Differences $before $after 40
    Assert-True (Test-SameBytes $s1Gender `
        ([byte[]]$after[0x5C341F..0x5C3484])) `
        'S1 gender ranges unchanged'
    Assert-True (Test-SameBytes $s1Appearance `
        ([byte[]]$after[0x5C3485..0x5C38E0])) `
        'S1 appearance ranges unchanged'
    Assert-True (Test-SameBytes $s2ResponseHook `
        ([byte[]]$after[0x0EB968..0x0EB96C])) `
        'S2 response hook unchanged'
    Assert-True (Test-SameBytes $s2RequestHook `
        ([byte[]]$after[0x17428E..0x174292])) `
        'S2 request hook unchanged'
    Assert-True (Test-SameBytes $s2Cave `
        ([byte[]]$after[0x51BF67..0x51BFFF])) `
        'S2 cooldown cave unchanged'

    $hook = $after[0x2A11B4..0x2A11B9]
    Assert-True (Test-Bytes $hook 0 (Hex 'E8 AD 21 32 00 90')) `
        '10271 stage-store hook exact bytes'
    Assert-Equal (Relative-Target $hook 0 0x006A11B4) 0x009C3366 `
        '10271 hook targets isolated S3 cave'

    $wrapper = $after[0x5C3366..0x5C338C]
    Assert-True (Test-Bytes $wrapper 0 (Hex @'
88 90 B9 00 00 00 9C 60 8B E8 A1 A4 D0 5A 01 85
C0 74 11 8B 55 00 39 50 04 75 09 8B C8 8B C2 E8
86 36 C0 FF 61 9D C3
'@)) 'wrapper exact bytes'
    Assert-True (Test-Bytes $wrapper 0 (Hex '88 90 B9 00 00 00')) `
        'wrapper replays the exact native stage store first'
    Assert-True (Test-Bytes $wrapper 6 (Hex '9C 60')) `
        'wrapper saves flags and every general register'
    Assert-True (Test-Bytes $wrapper 10 (Hex 'A1 A4 D0 5A 01')) `
        'wrapper loads existing Unite singleton without lazy allocation'
    Assert-True (Test-Bytes $wrapper 15 (Hex @'
85 C0 74 11 8B 55 00 39 50 04 75 09
'@)) 'null guard and exact selected-ID/active-ID guard reach cleanup'
    Assert-True (Test-Bytes $wrapper 27 (Hex '8B C8 8B C2 E8')) `
        'setter receives existing manager and matching active pet ID'
    Assert-Equal (Relative-Target $wrapper 31 0x009C3366) `
        0x005C6A10 'wrapper calls stock visibility-gated setter'
    Assert-True (Test-Bytes $wrapper 36 (Hex '61 9D C3')) `
        'wrapper restores every register and original flags'
    Assert-True (Test-Bytes $after 0x5C338D `
        ([byte[]]::new(0x5C3400 - 0x5C338D))) `
        'unused S3 cave remainder stays zero'

    $xrefs = @(Get-RelativeCaveXrefs $after)
    Assert-Equal $xrefs.Count 1 'S3 has one inbound cave xref'
    Assert-Equal $xrefs[0].Offset 0x2A11B4 'S3 xref source'
    Assert-Equal $xrefs[0].Target 0x009C3366 'S3 xref target'
    Assert-Equal @(Get-AbsoluteCaveReferences $after).Count 0 `
        'S3 has no absolute pointer-shaped cave references'

    $status = & $patcher -Mode Status -ClientExe $client
    Assert-Equal $status.Status 'Patched' 'S3 status'
    Assert-Equal $status.CaveInboundRelativeXrefs 1 `
        'S3 status xref count'
    $again = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    [IO.File]::WriteAllBytes($client, $previousFixture)
    $upgradeReady = & $patcher -Mode Status -ClientExe $client
    Assert-Equal $upgradeReady.Status 'Previous patch; ready to upgrade' `
        'previous patch status'
    Assert-Equal $upgradeReady.SamePetGuard $false `
        'previous patch does not claim a valid same-pet guard'
    $upgraded = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $upgraded.Status 'Patched' 'previous patch direct upgrade status'
    Assert-Equal $upgraded.Hash $patchedHash `
        'previous patch direct upgrade exact successor'
    Assert-Equal $upgraded.ChangedBytes 21 `
        'previous patch direct upgrade changed-byte count'
    Assert-Equal $upgraded.SamePetGuard $true `
        'direct upgrade enables the exact pet-ID guard'
    Assert-True (Test-SameBytes $after `
        ([IO.File]::ReadAllBytes($client))) `
        'previous patch direct upgrade is byte-exact S3'

    Copy-Item -LiteralPath $client -Destination $partial
    [byte[]]$partialBytes = [IO.File]::ReadAllBytes($partial)
    $partialBytes[0x5C3370] = $partialBytes[0x5C3370] -bxor 1
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    $partialRefused = $false
    try { & $patcher -Mode Status -ClientExe $partial | Out-Null }
    catch { $partialRefused = $_.Exception.Message.Contains('partial') }
    Assert-True $partialRefused 'partial S3 cave is refused'

    $reverted = & $patcher -Mode Revert -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    Assert-Equal $reverted.Hash $sourceHash 'revert exact S2 hash'
    Assert-True (Test-SameBytes $before `
        ([IO.File]::ReadAllBytes($client))) 'byte-exact S2 round trip'

    Write-Host (
        "Pet Soul/owner-Merge refresh checks passed: " +
        "$assertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
