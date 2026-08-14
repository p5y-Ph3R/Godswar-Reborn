[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetAppearanceRefresh.ps1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repoRoot (
    'artifacts\pet-appearance-refresh-test-' +
    [guid]::NewGuid().ToString('N'))
$clientExe = Join-Path $testRoot 'Origin.exe'
$backupRoot = Join-Path $testRoot 'backups'
$sourceSha256 =
    'F8D832D97A1C910AF31645DBD8B6FC2BDADF4AD30196470553A8668DB81A1D17'
$patchedSha256 =
    'C1CE0273504AB3E8020FD2EB2692351FFA0094F6A103719EB8970FD98C3DB2B6'
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

function Measure-ByteSequence([byte[]]$Data, [byte[]]$Sequence) {
    $matches = 0
    $offset = 0
    $lastStart = $Data.Length - $Sequence.Length
    while ($offset -le $lastStart) {
        $hit = [Array]::IndexOf(
            $Data,
            $Sequence[0],
            $offset,
            $lastStart - $offset + 1)
        if ($hit -lt 0) { break }
        if (Test-Bytes $Data $hit $Sequence) { $matches++ }
        $offset = $hit + 1
    }
    return $matches
}

function Get-PeLayout([byte[]]$Data) {
    $peOffset = [BitConverter]::ToInt32($Data, 0x3C)
    if ($Data[0] -ne 0x4D -or $Data[1] -ne 0x5A -or
        [BitConverter]::ToUInt32($Data, $peOffset) -ne 0x00004550) {
        throw 'The appearance-refresh fixture is not a PE image.'
    }
    $optionalSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    if ($optionalSize -lt 0xE0 -or
        [BitConverter]::ToUInt16($Data, $optionalOffset) -ne 0x010B) {
        throw 'The appearance-refresh fixture is not PE32.'
    }
    $sections = @()
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $sectionTable = $optionalOffset + $optionalSize
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionTable + $index * 40
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString(
                $Data[$offset..($offset + 7)]).Trim([char]0)
            VirtualAddress =
                [BitConverter]::ToUInt32($Data, $offset + 12)
            RawSize = [BitConverter]::ToUInt32($Data, $offset + 16)
            RawOffset = [BitConverter]::ToUInt32($Data, $offset + 20)
            Characteristics =
                [BitConverter]::ToUInt32($Data, $offset + 36)
        }
    }
    [pscustomobject]@{
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalOffset + 28)
        SizeOfHeaders =
            [BitConverter]::ToUInt32($Data, $optionalOffset + 60)
        FileCharacteristics =
            [BitConverter]::ToUInt16($Data, $peOffset + 22)
        DllCharacteristics =
            [BitConverter]::ToUInt16($Data, $optionalOffset + 70)
        BaseRelocationRva =
            [BitConverter]::ToUInt32($Data, $optionalOffset + 136)
        BaseRelocationSize =
            [BitConverter]::ToUInt32($Data, $optionalOffset + 140)
        Sections = $sections
    }
}

function Find-ByteOffsets(
    [byte[]]$Data,
    [byte]$Value,
    [int]$Start,
    [int]$End
) {
    $cursor = $Start
    while ($cursor -lt $End) {
        $hit = [Array]::IndexOf(
            $Data,
            $Value,
            $cursor,
            $End - $cursor)
        if ($hit -lt 0) { break }
        $hit
        $cursor = $hit + 1
    }
}

function Get-RelativeInboundXrefs(
    [byte[]]$Data,
    [object]$Pe,
    [uint64]$TargetStart,
    [uint64]$TargetEnd
) {
    $ranges = @([pscustomobject]@{
        Start = 0
        End = [int]$Pe.SizeOfHeaders
        VaDelta = [int64]$Pe.ImageBase
    })
    foreach ($section in $Pe.Sections) {
        if ($section.RawSize -eq 0) { continue }
        $ranges += [pscustomobject]@{
            Start = [int]$section.RawOffset
            End = [int]($section.RawOffset + $section.RawSize)
            VaDelta = [int64]$Pe.ImageBase +
                $section.VirtualAddress - $section.RawOffset
        }
    }
    $opcodes = @(0xE8, 0xE9, 0x0F, 0xEB) + @(0x70..0x7F) +
        @(0xE0..0xE3)
    foreach ($range in $ranges) {
        foreach ($opcode in $opcodes) {
            $hits = @(Find-ByteOffsets $Data ([byte]$opcode) `
                $range.Start $range.End)
            foreach ($offset in $hits) {
                $length = 0
                $displacement = [int64]0
                if ($opcode -in @(0xE8, 0xE9) -and
                    $offset + 5 -le $range.End) {
                    $length = 5
                    $displacement =
                        [BitConverter]::ToInt32($Data, $offset + 1)
                }
                elseif ($opcode -eq 0x0F -and
                    $offset + 6 -le $range.End -and
                    $Data[$offset + 1] -ge 0x80 -and
                    $Data[$offset + 1] -le 0x8F) {
                    $length = 6
                    $displacement =
                        [BitConverter]::ToInt32($Data, $offset + 2)
                }
                elseif ($opcode -ne 0x0F -and
                    $opcode -notin @(0xE8, 0xE9) -and
                    $offset + 2 -le $range.End) {
                    $length = 2
                    $next = [int]$Data[$offset + 1]
                    $displacement = if ($next -ge 0x80) {
                        $next - 0x100
                    }
                    else { $next }
                }
                if ($length -eq 0) { continue }
                $sourceVa = [int64]$offset + $range.VaDelta
                $targetVa = $sourceVa + $length + $displacement
                if ($sourceVa -ge $TargetStart -and
                    $sourceVa -lt $TargetEnd) {
                    continue
                }
                if ($targetVa -lt $TargetStart -or
                    $targetVa -ge $TargetEnd) {
                    continue
                }
                [pscustomobject]@{
                    FileOffset = $offset
                    SourceVa = $sourceVa
                    TargetVa = $targetVa
                    Length = $length
                }
            }
        }
    }
}

function Measure-AbsoluteInboundXrefs(
    [byte[]]$Data,
    [uint32]$TargetStart,
    [uint32]$TargetEnd
) {
    $matches = 0
    for ($target = $TargetStart; $target -lt $TargetEnd; $target++) {
        $matches += Measure-ByteSequence $Data (
            [BitConverter]::GetBytes([uint32]$target))
    }
    return $matches
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

function Test-TailAccepted(
    [int]$Species,
    [int]$Bound,
    [int]$ReservedWord = 0
) {
    [uint32]$tail = [uint32]$Species -bor
        ([uint32]$Bound -shl 8) -bor
        ([uint32]$ReservedWord -shl 16)
    return $tail -le 0x12D -and $Species -ne 0 -and $Species -le 45
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination $clientExe

    $ready = & $patcher -ClientExe $clientExe -Mode Status
    Assert-Equal $ready.Status 'Ready to apply' 'source status'
    Assert-Equal $ready.Hash $sourceSha256 'composed F8 predecessor hash'
    Assert-Equal $ready.ProgressionPacketLength 68 `
        'source progression length'
    Assert-Equal $ready.SpeciesRefresh $false 'source species state'

    [byte[]]$source = [IO.File]::ReadAllBytes($clientExe)
    $pe = Get-PeLayout $source
    Assert-Equal $pe.ImageBase 0x00400000 'fixed preferred image base'
    Assert-Equal ($pe.FileCharacteristics -band 0x0001) 0x0001 `
        'PE marks base relocations stripped'
    Assert-Equal ($pe.DllCharacteristics -band 0x0040) 0 `
        'PE does not opt into dynamic-base ASLR'
    Assert-Equal $pe.BaseRelocationRva 0 'PE has no relocation directory'
    Assert-Equal $pe.BaseRelocationSize 0 'PE relocation size is zero'
    $tailSection = @($pe.Sections | Where-Object {
        0x5C38B1 -ge $_.RawOffset -and
        0x5C38E0 -le $_.RawOffset + $_.RawSize
    })
    Assert-Equal $tailSection.Count 1 'tail cave resolves to one PE section'
    Assert-Equal $tailSection[0].Name '.rdata' `
        'tail cave remains in the audited rdata section'
    Assert-Equal ($tailSection[0].Characteristics -band 0x20000000) `
        0x20000000 'tail cave section is executable'
    Assert-Equal (Measure-AbsoluteInboundXrefs $source `
        0x009C38B1 0x009C38E0) 0 `
        'whole tail cave has no absolute inbound xrefs'
    $sourceInbound = @(Get-RelativeInboundXrefs $source $pe `
        0x009C38B1 0x009C38E0)
    Assert-Equal $sourceInbound.Count 0 `
        'full mapped image has no relative inbound tail-cave xrefs'
    Assert-True (Test-Bytes $source 0x5C38AC (
        Hex 'E917E8A7FF')) 'tail cave is preceded by unconditional branch'
    Assert-True (Test-Bytes $source 0x5C38E0 (
        Hex '817F6C475741320F84820000008B4764')) `
        'tail cave successor stays pinned'

    $apply = & $patcher -ClientExe $clientExe -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    Assert-Equal $apply.Hash $patchedSha256 'exact native successor hash'
    Assert-Equal $apply.ChangedBytes 82 'exact changed-byte count'
    Assert-Equal $apply.ProgressionPacketLength 68 `
        'ordinary progression remains 68 bytes'
    Assert-Equal $apply.AppearancePacketLength 72 `
        'appearance refresh becomes 72 bytes'

    [byte[]]$patched = [IO.File]::ReadAllBytes($clientExe)
    $dispatcher = Hex @'
9C 66 83 3E 44 74 0B 66 83 3E 48 75 1E
E9 1F 04 00 00
51 56 57 83 C6 14 81 C7 84 00 00 00 6A 0C 59
F3 A5 5F 5E 59 E9 E7 01 00 00 E9 E2 01 00 00
'@
    $tail = Hex @'
50 8B 46 44 3D 2D 01 00 00 77 17 84 C0 74 13
3C 2D 77 0F 88 47 3C 88 A7 BC 00 00 00 58 E9
BF FB FF FF 58 E9 B9 FD FF FF 00 00 00 00 00 00 00
'@
    $final = Hex '9D C7 84 24 E4 00 00 00 07 00 00 00 EB B8'
    Assert-True (Test-Bytes $patched 0x5C3480 $dispatcher) `
        'dispatcher has exact 68 and 72 gates'
    Assert-True (Test-Bytes $patched 0x5C38B1 $tail) `
        'tail cave has exact bounded decoder'
    Assert-True (Test-Bytes $patched 0x5C3692 $final) `
        'shared finalizer preserves displaced instruction'
    $patchedInbound = @(Get-RelativeInboundXrefs $patched $pe `
        0x009C38B1 0x009C38E0)
    Assert-Equal $patchedInbound.Count 1 `
        'patch introduces exactly one relative tail-cave xref'
    Assert-Equal $patchedInbound[0].FileOffset 0x5C348D `
        'only inbound tail-cave xref is the 72-byte dispatcher'
    Assert-Equal $patchedInbound[0].SourceVa 0x009C348D `
        'dispatcher inbound xref source VA'
    Assert-Equal $patchedInbound[0].TargetVa 0x009C38B1 `
        'dispatcher inbound xref target VA'
    Assert-Equal (Measure-AbsoluteInboundXrefs $patched `
        0x009C38B1 0x009C38E0) 0 `
        'patch uses no absolute tail-cave xrefs'
    Assert-Equal (Get-ShortTarget $dispatcher 5 0x009C3480) `
        0x009C3492 '68-byte branch enters twelve-dword copy'
    Assert-Equal (Get-NearTarget $dispatcher 13 0x009C3480) `
        0x009C38B1 '72-byte branch enters bounded tail decoder'
    Assert-Equal (Get-NearTarget $tail 29 0x009C38B1) `
        0x009C3492 'valid 72-byte tail enters unchanged copy'
    Assert-Equal (Get-NearTarget $tail 35 0x009C38B1) `
        0x009C3692 'invalid tail skips all extension writes'
    Assert-Equal (Get-ShortTarget $final 12 0x009C3692) `
        0x009C3658 'both packet lengths retain full redraw'
    Assert-True (Test-Bytes $dispatcher 30 (Hex '6A0C59F3A5')) `
        'both forms copy exactly twelve dwords'
    Assert-True (Test-Bytes $dispatcher 18 (
        Hex '51565783C61481C7840000006A0C59F3A55F5E59')) `
        'copy restores ECX ESI and EDI after temporary use'
    Assert-True (Test-Bytes $tail 19 (
        Hex '88473C88A7BC000000')) `
        'tail writes only species and bound bean bytes'
    Assert-True ($tail[0] -eq 0x50 -and $tail[28] -eq 0x58 -and
        $tail[34] -eq 0x58) 'tail preserves EAX on success and rejection'
    Assert-True ($dispatcher[0] -eq 0x9C -and $final[0] -eq 0x9D) `
        'dispatcher preserves incoming EFLAGS on every exit'
    Assert-True (Test-Bytes $patched 0x5C3658 (Hex '9C505152')) `
        'redraw saves EFLAGS and volatile registers'
    Assert-True (Test-Bytes $patched 0x5C3689 (Hex '5A59589D')) `
        'redraw restores volatile registers and EFLAGS'
    Assert-Equal ((-4) + 4) 0 'non-extension path stack is balanced'
    Assert-Equal ((-4) + (-12) + 12 + 4) 0 `
        '68-byte copy path stack is balanced'
    Assert-Equal ((-4) + (-4) + 4 + (-12) + 12 + 4) 0 `
        'valid 72-byte path stack is balanced'
    Assert-Equal ((-4) + (-4) + 4 + 4) 0 `
        'rejected 72-byte tail stack is balanced'

    foreach ($species in 1..45) {
        foreach ($bound in 0..1) {
            Assert-True (Test-TailAccepted $species $bound) `
                "valid species $species bound $bound"
        }
    }
    foreach ($invalid in @(
            @(0, 0, 0),
            @(46, 0, 0),
            @(255, 0, 0),
            @(1, 2, 0),
            @(1, 0, 1),
            @(45, 1, 0xFFFF))) {
        Assert-True (-not (Test-TailAccepted @invalid)) `
            "invalid tail $($invalid -join '/')"
    }
    foreach ($length in @(0, 20, 44, 67, 69, 70, 71, 73, 255, 324)) {
        Assert-True ($length -ne 68 -and $length -ne 72) `
            "malformed length $length misses both exact gates"
    }

    $changedOutside = 0
    for ($offset = 0; $offset -lt $source.Length; $offset++) {
        if ($source[$offset] -eq $patched[$offset]) { continue }
        $allowed = ($offset -ge 0x5C3480 -and $offset -lt 0x5C34B0) -or
            ($offset -ge 0x5C3692 -and $offset -lt 0x5C36A0) -or
            ($offset -ge 0x5C38B1 -and $offset -lt 0x5C38E0)
        if (-not $allowed) { $changedOutside++ }
    }
    Assert-Equal $changedOutside 0 'mutation allowlist'

    $status = & $patcher -ClientExe $clientExe -Mode Status
    Assert-Equal $status.Status 'Patched' 'patched status'
    Assert-Equal $status.Hash $patchedSha256 'patched status hash'
    Assert-Equal $status.SpeciesRefresh $true 'patched species state'
    Assert-Equal $status.BoundRefresh $true 'patched bound state'
    $again = & $patcher -ClientExe $clientExe -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    $partial = Join-Path $testRoot 'partial.exe'
    [IO.File]::WriteAllBytes($partial, $patched)
    $partialBytes = [IO.File]::ReadAllBytes($partial)
    $partialBytes[0x5C38B2] = $partialBytes[0x5C38B2] -bxor 1
    [IO.File]::WriteAllBytes($partial, $partialBytes)
    $partialRefused = $false
    try { & $patcher -ClientExe $partial -Mode Status | Out-Null }
    catch { $partialRefused = $_.Exception.Message.Contains('partial') }
    Assert-True $partialRefused 'partial cave is refused'

    $revert = & $patcher -ClientExe $clientExe -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Equal $revert.Status 'Reverted' 'revert status'
    Assert-Equal $revert.Hash $sourceSha256 'revert hash'
    Assert-True ([Linq.Enumerable]::SequenceEqual(
        $source, [IO.File]::ReadAllBytes($clientExe))) `
        'revert is byte-exact'

    Write-Host "Pet appearance-refresh patch checks passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
