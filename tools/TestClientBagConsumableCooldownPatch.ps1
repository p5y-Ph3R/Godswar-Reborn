[CmdletBinding()]
param([string]$FixtureExe = 'C:\Godswar Origin\Origin.exe')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientBagConsumableCooldown.ps1'
$genderPatcher = Join-Path $PSScriptRoot 'PatchClientPetGenderRefresh.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'godswar-bag-consumable-cooldown-' + [guid]::NewGuid().ToString('N'))
$client = Join-Path $testRoot 'Origin.exe'
$partial = Join-Path $testRoot 'Origin-partial.exe'
$backups = Join-Path $testRoot 'backups'
$c1ceHash =
    'C1CE0273504AB3E8020FD2EB2692351FFA0094F6A103719EB8970FD98C3DB2B6'
$sourceHash =
    '00ED99F0EADB605059CB7A0FA476922EC6EA9E3EAE9218710C20299992706BDB'
$patchedHash =
    '7D1F17A21B0D34DA8BE61C639D72BFB4A518A2F3B0B3B0001699ADC560FA0021'
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

function Relative-Target(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$Va
) {
    [int64]$Va + $Offset + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Count-Sequence([byte[]]$Data, [byte[]]$Sequence) {
    $count = 0
    $cursor = 0
    while ($cursor -le $Data.Length - $Sequence.Length) {
        $hit = [Array]::IndexOf(
            $Data,
            $Sequence[0],
            $cursor,
            $Data.Length - $Sequence.Length - $cursor + 1)
        if ($hit -lt 0) { break }
        if (Test-Bytes $Data $hit $Sequence) { $count++ }
        $cursor = $hit + 1
    }
    return $count
}

function Test-SameBytes([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Get-RelativeCaveXrefs([byte[]]$Data) {
    $result = @()
    for ($offset = 0x1000; $offset -lt 0x51BFFC; $offset++) {
        if ($Data[$offset] -ne 0xE8 -and $Data[$offset] -ne 0xE9) {
            continue
        }
        $target = 0x00400000 + $offset + 5 +
            [BitConverter]::ToInt32($Data, $offset + 1)
        if ($target -ge 0x0091BF67 -and $target -lt 0x0091C000) {
            $result += [pscustomobject]@{
                Offset = $offset
                Target = $target
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
            $result += [pscustomobject]@{
                Offset = $offset
                Target = $value
            }
        }
    }
    return $result
}

function Assert-OnlyCooldownDifferences(
    [byte[]]$Before,
    [byte[]]$After,
    [int]$ExpectedCount
) {
    $count = 0
    for ($offset = 0; $offset -lt $Before.Length; $offset++) {
        if ($Before[$offset] -eq $After[$offset]) { continue }
        $count++
        $allowed = ($offset -ge 0x0EB968 -and $offset -lt 0x0EB96D) -or
            ($offset -ge 0x17428E -and $offset -lt 0x174293) -or
            ($offset -ge 0x51BF67 -and $offset -lt 0x51C000)
        if (-not $allowed) {
            throw "Unexpected mutation at 0x$($offset.ToString('X'))."
        }
    }
    Assert-Equal $count $ExpectedCount 'exact changed-byte count'
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Copy-Item -LiteralPath $FixtureExe -Destination $client
    $fixtureHash = (Get-FileHash $client -Algorithm SHA256).Hash
    if ($fixtureHash -eq $c1ceHash) {
        & $genderPatcher -Mode Apply -ClientExe $client `
            -BackupRoot $backups | Out-Null
    }
    elseif ($fixtureHash -eq $patchedHash) {
        & $patcher -Mode Revert -ClientExe $client `
            -BackupRoot $backups | Out-Null
    }
    elseif ($fixtureHash -ne $sourceHash) {
        throw "Fixture has unsupported SHA-256 $fixtureHash."
    }
    Assert-Equal (Get-FileHash $client -Algorithm SHA256).Hash `
        $sourceHash 'normalized exact S1 predecessor'

    $ready = & $patcher -Mode Status -ClientExe $client
    Assert-Equal $ready.Status 'Ready to apply' 'S1 status'
    Assert-Equal $ready.GenericUseGate $true 'stock Use=1 gate'
    Assert-Equal $ready.RequestSideCalls 1 'request-side timer count'
    Assert-Equal $ready.PostProjectionReapply $false `
        'S1 has no response reapply'
    Assert-Equal $ready.PendingGroups 2 'bounded pending group queue'
    Assert-Equal $ready.FinalDetailPage 3 'final detail page gate'
    Assert-Equal $ready.FinalDetailHalf 12 'final detail half gate'
    Assert-Equal $ready.RequestStack `
        '[ret, bagUI, group]; stock callee ret 8' 'request stack contract'
    Assert-Equal $ready.ResponsePacket `
        '[wrapper esp+0x38] => handler [esp+0x2C]' `
        'response packet-local mapping'
    Assert-Equal $ready.PreservedRegisters `
        'EBP/ESI saved; EBX/EDI untouched' 'register contract'
    Assert-Equal $ready.CaveInboundRelativeXrefs 0 `
        'S1 status cave xref count'

    [byte[]]$before = [IO.File]::ReadAllBytes($client)
    Assert-True (Test-Bytes $before 0x174283 (Hex @'
8B 93 F4 00 00 00 8B 42 74 50 57
'@)) 'request caller pushes group then bag UI'
    Assert-True (Test-Bytes $before 0x173D80 `
        (Hex '8B 54 24 08 83 EC 0C')) `
        'stock StartCooling reads group at entry esp plus 8'
    Assert-True (Test-Bytes $before 0x173F12 `
        (Hex '5F 5E 5D 5B 83 C4 0C C2 08 00')) `
        'stock StartCooling restores registers and returns 8'
    Assert-True (Test-Bytes $before 0x0EA41E `
        (Hex '89 7C 24 2C')) 'dispatcher records packet local'
    Assert-True (Test-Bytes $before 0x0EB8F8 (Hex @'
8B 44 24 2C 0F BE 48 15 0F BE 40 14
'@)) 'detail handler reads page and half from packet local'
    Assert-True (Test-Bytes $before 0x0EB96D `
        (Hex 'E8 9E F0 08 00 8B C8')) `
        'next handler call overwrites volatile return registers'
    Assert-Equal @(Get-RelativeCaveXrefs $before).Count 0 `
        'S1 has no inbound relative cave xrefs'
    Assert-Equal @(Get-AbsoluteRangeReferences $before `
        0x0091BF67 0x0091C000).Count 0 `
        'S1 has no absolute pointer-shaped cave xrefs'
    Assert-Equal (Count-Sequence $before (Hex '10 00 A4 00')) 0 `
        'S1 has no first runtime-queue reference'
    Assert-Equal (Count-Sequence $before (Hex '14 00 A4 00')) 0 `
        'S1 has no second runtime-queue reference'
    $applied = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.Hash $patchedHash 'S2 apply hash'
    Assert-Equal $applied.GenericUseGate $true 'generic metadata path'
    Assert-Equal $applied.RequestSideCalls 1 `
        'request path still starts only once'
    Assert-Equal $applied.PostProjectionReapply $true `
        'authoritative post-refresh reapply enabled'

    [byte[]]$after = [IO.File]::ReadAllBytes($client)
    Assert-OnlyCooldownDifferences $before $after 91
    Assert-True (Test-SameBytes `
        ([byte[]]$before[0x5C341F..0x5C347E]) `
        ([byte[]]$after[0x5C341F..0x5C347E])) `
        'S1 gender cave is unchanged'
    Assert-True (Test-SameBytes `
        ([byte[]]$before[0x5C3480..0x5C3484]) `
        ([byte[]]$after[0x5C3480..0x5C3484])) `
        'S1 gender hook is unchanged'
    Assert-True (Test-SameBytes `
        ([byte[]]$before[0x5C3485..0x5C38E0]) `
        ([byte[]]$after[0x5C3485..0x5C38E0])) `
        'appearance refresh ranges are unchanged'

    $requestHook = $after[0x17428E..0x174292]
    $responseHook = $after[0x0EB968..0x0EB96C]
    Assert-Equal (Relative-Target $requestHook 0 0x0057428E) `
        0x0091BF67 'request hook targets capture wrapper'
    Assert-Equal (Relative-Target $responseHook 0 0x004EB968) `
        0x0091BF90 'detail refresh targets response wrapper'

    $capture = $after[0x51BF67..0x51BF84]
    $response = $after[0x51BF90..0x51BFD5]
    Assert-True (Test-Bytes $capture 0 (Hex @'
8B 44 24 08 83 3D 10 00 A4 00 00 75 07 A3 10 00
A4 00 EB 05 A3 14 00 A4 00 E9 FB 7D C5 FF
'@)) 'capture wrapper exact bytes'
    Assert-Equal (Count-Sequence $capture (Hex 'E9')) 1 `
        'capture has one stock StartCooling transfer'
    Assert-Equal (Relative-Target $capture 25 0x0091BF67) `
        0x00573D80 'capture tail-calls stock StartCooling'
    Assert-True (Test-Bytes $capture 0 (Hex '8B 44 24 08')) `
        'capture uses runtime metadata group argument'
    Assert-Equal (Count-Sequence $capture (Hex '71 12 00 00')) 0 `
        'Morning Dew group 4721 is not hardcoded'

    Assert-True (Test-Bytes $response 12 (Hex @'
74 23 80 78 14 03 75 1D 80 78 15 0C 75 17
'@)) 'reapply waits for page 3 half 12'
    Assert-True (Test-Bytes $response 0 `
        (Hex '55 56 8B F1 33 ED 8B 44 24 38')) `
        'response saves nonvolatiles and maps packet stack frame'
    Assert-True (Test-Bytes $response 60 (Hex '55 56 E8')) `
        'response pushes group then bag UI for StartCooling'
    Assert-True (Test-Bytes $response 67 (Hex '5E 5D C3')) `
        'response restores nonvolatiles and caller stack'
    Assert-Equal (Relative-Target $response 51 0x0091BF90) `
        0x00574FF0 'response calls stock bag refresh first'
    Assert-Equal (Relative-Target $response 62 0x0091BF90) `
        0x00573D80 'response reapplies stock cooldown second'
    Assert-True (Test-Bytes $response 42 (Hex '83 25 14 00 A4 00 00')) `
        'response consumes pending queue entry once'
    $xrefs = @(Get-RelativeCaveXrefs $after)
    Assert-Equal $xrefs.Count 2 'S2 exact inbound relative cave xrefs'
    Assert-Equal $xrefs[0].Offset 0x0EB968 'response cave xref origin'
    Assert-Equal $xrefs[0].Target 0x0091BF90 'response cave xref target'
    Assert-Equal $xrefs[1].Offset 0x17428E 'request cave xref origin'
    Assert-Equal $xrefs[1].Target 0x0091BF67 'request cave xref target'
    Assert-Equal @(Get-AbsoluteRangeReferences $after `
        0x0091BF67 0x0091C000).Count 0 `
        'S2 has no absolute pointer-shaped cave xrefs'
    Assert-Equal (Count-Sequence $after (Hex '10 00 A4 00')) 4 `
        'S2 exact first runtime-queue xrefs'
    Assert-Equal (Count-Sequence $after (Hex '14 00 A4 00')) 3 `
        'S2 exact second runtime-queue xrefs'
    Assert-True (Test-SameBytes `
        ([byte[]]$after[0x51BFD6..0x51BFFF]) `
        ([byte[]]::new(42))) `
        'remaining audited cave is still zero'

    $patchedStatus = & $patcher -Mode Status -ClientExe $client
    Assert-Equal $patchedStatus.CaveInboundRelativeXrefs 2 `
        'S2 status exact cave xref count'

    $again = & $patcher -Mode Apply -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'
    Assert-Equal $again.Hash $patchedHash 'idempotent hash'

    Copy-Item -LiteralPath $client -Destination $partial
    [byte[]]$partialBytes = [IO.File]::ReadAllBytes($partial)
    $partialBytes[0x51BFAA] = $partialBytes[0x51BFAA] -bxor 1
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    $partialRefused = $false
    try { & $patcher -Mode Status -ClientExe $partial | Out-Null }
    catch { $partialRefused = $_.Exception.Message.Contains('partial') }
    Assert-True $partialRefused 'partial cave is refused'

    $reverted = & $patcher -Mode Revert -ClientExe $client `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    Assert-Equal $reverted.Hash $sourceHash 'byte-exact S1 revert hash'
    Assert-True (Test-SameBytes `
        $before ([IO.File]::ReadAllBytes($client))) `
        'round trip restores exact S1 predecessor'

    Write-Host (
        "Bag-consumable cooldown patch checks passed: " +
        "$assertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
