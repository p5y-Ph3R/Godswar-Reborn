Set-StrictMode -Version Latest

function Convert-RebornHexBytes([string]$Hex) {
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

function Test-RebornBytes(
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

function Copy-RebornBytes(
    [byte[]]$Source,
    [byte[]]$Destination,
    [int]$Offset
) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-RebornPeMetadata([byte[]]$Data) {
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

function Resolve-RebornExecutableVa(
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

function Get-RebornNearBranchTarget(
    [byte[]]$Code,
    [int]$InstructionOffset,
    [uint64]$CodeVa
) {
    return [int64]$CodeVa + $InstructionOffset + 5 +
        [BitConverter]::ToInt32($Code, $InstructionOffset + 1)
}

function Get-CharacterStatsBinaryProfile {
    $caveReserveLength = 0x80
    $caveCode = Convert-RebornHexBytes @'
A1 AC 5E 57 01 6A 64 D9 80 8C 02 00 00 DA 0C 24
DB 1C 24 8D 54 24 2C 68 3C 44 95 00 52 FF 15 D0
C3 91 00 83 C4 0C 8B 8E 80 01 00 00 8B 11 8B 92
84 00 00 00 8D 44 24 28 50 FF D2 A1 AC 5E 57 01
6A 64 D9 80 90 02 00 00 DA 0C 24 DB 1C 24 8D 54
24 2C 68 3C 44 95 00 52 FF 15 D0 C3 91 00 83 C4
0C 8B 8E 7C 01 00 00 8B 11 8B 92 84 00 00 00 8D
44 24 28 50 FF D2 E9 39 1C BF FF
'@
    $legacyCave = [byte[]]::new($caveReserveLength)
    Copy-RebornBytes $caveCode $legacyCave 0
    $questLegacy = [byte[]]::new(0x20)
    Copy-RebornBytes (Convert-RebornHexBytes @'
85 F6 74 14 8B 4E 08 85 C9 74 0D 83 7E 0C 00 74
07 8B 01 E9 AD 65 C1 FF C3
'@) $questLegacy 0
    return [pscustomobject]@{
        ExpectedLength = 6676480
        HookOffset = 0x1B5B97
        HookVa = 0x005B5B97
        CaveOffset = 0x5C3F20
        CaveVa = 0x009C3F20
        CaveReserveLength = $caveReserveLength
        EpilogueVa = 0x005B5BD4
        OriginalHook = Convert-RebornHexBytes 'A1 AC 5E 57 01'
        LegacyHook = Convert-RebornHexBytes 'E9 84 E3 40 00'
        EmptyCave = [byte[]]::new($caveReserveLength)
        LegacyCave = $legacyCave
        LegacyCaveCode = $caveCode
        NativePrefix = Convert-RebornHexBytes @'
68 3C 44 95 00 52 FF D7 8B 8E 5C 01 00 00 8B 01
8B 80 84 00 00 00 83 C4 0C 8D 54 24 28 52 FF D0
'@
        NativeSuffix = Convert-RebornHexBytes @'
80 B8 8C 07 00 00 02 75 44 68 08 02 00 00 8D 4C
24 2C 51 6A FF 05 8D 07 00 00 50 6A 00 6A 00 FF
'@
        QuestEmpty = [byte[]]::new(0x20)
        QuestLegacy = $questLegacy
    }
}

function Assert-CharacterStatsBinaryCompatible(
    [byte[]]$Data,
    [object]$Profile
) {
    if ($Data.Length -ne $Profile.ExpectedLength) {
        throw "Unexpected Origin.exe length: $($Data.Length)."
    }
    $pe = Get-RebornPeMetadata $Data
    if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
        $pe.ImageBase -ne 0x00400000 -or
        (Resolve-RebornExecutableVa $pe $Profile.HookOffset 5 '.text') -ne
            $Profile.HookVa -or
        (Resolve-RebornExecutableVa $pe $Profile.CaveOffset (
                $Profile.CaveReserveLength) '.rdata') -ne $Profile.CaveVa) {
        throw 'Origin.exe is not the audited x86 PE32 build.'
    }
    if (-not (Test-RebornBytes $Data (
                $Profile.HookOffset - $Profile.NativePrefix.Length) (
                $Profile.NativePrefix)) -or
        -not (Test-RebornBytes $Data ($Profile.HookOffset + 5) (
                $Profile.NativeSuffix))) {
        throw 'Origin.exe PersonalInfo update boundaries do not match the audited build.'
    }
    if (-not (Test-RebornBytes $Data 0x5C3F00 $Profile.QuestEmpty) -and
        -not (Test-RebornBytes $Data 0x5C3F00 $Profile.QuestLegacy)) {
        throw 'The shared client cave has an unknown QuestView-owner state.'
    }
    $code = $Profile.LegacyCaveCode
    if ($code.Length -ne 123 -or
        (Get-RebornNearBranchTarget $Profile.LegacyHook 0 (
                $Profile.HookVa)) -ne $Profile.CaveVa -or
        (Get-RebornNearBranchTarget $code 118 $Profile.CaveVa) -ne
            $Profile.EpilogueVa) {
        throw 'Legacy character speed-stat trampoline invariants are invalid.'
    }
}

function Get-CharacterStatsBinaryState(
    [byte[]]$Data,
    [object]$Profile
) {
    Assert-CharacterStatsBinaryCompatible $Data $Profile
    if ((Test-RebornBytes $Data $Profile.HookOffset $Profile.OriginalHook) -and
        (Test-RebornBytes $Data $Profile.CaveOffset $Profile.EmptyCave)) {
        return 'Original'
    }
    if ((Test-RebornBytes $Data $Profile.HookOffset $Profile.LegacyHook) -and
        (Test-RebornBytes $Data $Profile.CaveOffset $Profile.LegacyCave)) {
        return 'LegacyPatched'
    }
    throw 'Origin.exe has an unknown or partially applied character-stat state.'
}

function Restore-CharacterStatsOriginalBinary(
    [byte[]]$Data,
    [object]$Profile
) {
    Copy-RebornBytes $Profile.OriginalHook $Data $Profile.HookOffset
    Copy-RebornBytes $Profile.EmptyCave $Data $Profile.CaveOffset
}
