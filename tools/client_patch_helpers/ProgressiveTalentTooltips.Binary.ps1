Set-StrictMode -Version Latest

function Convert-ProgressiveTalentHexBytes([string]$Hex) {
    $normalized = $Hex -replace '\s', ''
    if (($normalized.Length -band 1) -ne 0) {
        throw 'Progressive-talent hex text must contain an even number of digits.'
    }
    [byte[]]$result = for ($index = 0; $index -lt $normalized.Length;
        $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $result
}

function Test-ProgressiveTalentBytes(
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

function Copy-ProgressiveTalentBytes(
    [byte[]]$Source,
    [byte[]]$Destination,
    [int]$Offset
) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Get-ProgressiveTalentPeMetadata([byte[]]$Data) {
    if ($Data.Length -lt 0x100 -or $Data[0] -ne 0x4D -or
        $Data[1] -ne 0x5A) {
        throw 'Origin.exe does not have a valid DOS header.'
    }
    $peOffset = [BitConverter]::ToInt32($Data, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 24 -gt $Data.Length -or
        [BitConverter]::ToUInt32($Data, $peOffset) -ne 0x00004550) {
        throw 'Origin.exe does not have a valid PE header.'
    }
    $optionalOffset = $peOffset + 24
    $optionalSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $tableOffset = $optionalOffset + $optionalSize
    if ($optionalSize -lt 0xE0 -or $sectionCount -le 0 -or
        $tableOffset + ($sectionCount * 40) -gt $Data.Length) {
        throw 'Origin.exe has an invalid PE section table.'
    }
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $tableOffset + ($index * 40)
        $sections += [pscustomobject]@{
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

function Resolve-ProgressiveTalentExecutableVa(
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
            throw ('Origin.exe offset 0x{0:X} is not executable.' -f
                $FileOffset)
        }
        return [uint64]$Pe.ImageBase + $section.VirtualAddress +
            ([uint64]$FileOffset - $section.RawOffset)
    }
    throw ('Origin.exe offset 0x{0:X} is outside a PE section.' -f
        $FileOffset)
}

function Get-ProgressiveTalentRelativeTarget(
    [byte[]]$Code,
    [int]$Offset,
    [uint64]$InstructionVa
) {
    if ($Offset -lt 0 -or $Offset + 5 -gt $Code.Length -or
        $Code[$Offset] -ne 0xE8) {
        throw 'Progressive-talent hook is not a near CALL.'
    }
    return [int64]$InstructionVa + 5 +
        [BitConverter]::ToInt32($Code, $Offset + 1)
}

function Get-ProgressiveTalentRelativeCaveXrefs(
    [byte[]]$Data,
    [object]$Pe,
    [uint64]$StartVa,
    [uint64]$EndVa
) {
    $result = @()
    foreach ($section in $Pe.Sections) {
        if (($section.Characteristics -band 0x20000000) -eq 0) { continue }
        $first = [int]$section.RawOffset
        $last = [int]($section.RawOffset + $section.RawSize - 5)
        for ($offset = $first; $offset -le $last; $offset++) {
            if ($Data[$offset] -ne 0xE8 -and $Data[$offset] -ne 0xE9) {
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

function Get-ProgressiveTalentBinaryProfile {
    $helper = Convert-ProgressiveTalentHexBytes @'
0F B6 48 25 83 F9 64 83 D1 00 EB 04
0F B6 48 25 83 F9 64 76 05 B9 64 00 00 00
83 F9 28 76 32 83 F9 3C 76 28 83 F9 50 76 1B
83 F9 5A 76 0B 6B C9 07 81 E9 B8 01 00 00 EB 18
6B C9 05 81 E9 04 01 00 00 EB 0D
6B C9 03 83 E9 64 EB 05 01 C9 83 E9 28
89 4C 24 18 C3
'@
    [byte[]]$patchedCave = [byte[]]::new(96)
    Copy-ProgressiveTalentBytes $helper $patchedCave 0
    return [pscustomobject]@{
        Length = 6676480
        SourceSha256 =
            'FB634307517770ED8C677503C7D6F9E0E51A5995AFAF1A9D19631F1EFE1B6683'
        PatchedSha256 =
            '8FC6FB26B36227836B9C468083C07B331640DB216578387C6F27670F96F5DEDF'
        CaveOffset = 0x53E380
        CaveVa = [uint64]0x0093E380
        CaveLength = 96
        HelperLength = $helper.Length
        EmptyCave = [byte[]]::new(96)
        PatchedCave = $patchedCave
        Hooks = @(
            [pscustomobject]@{
                Label = 'next flat'; Offset = 0x208E29; Va = 0x00608E29
                TargetVa = 0x0093E380
                Original = Convert-ProgressiveTalentHexBytes (
                    '0F B6 48 25 83 C1 01 89 4C 24 14')
                Patched = Convert-ProgressiveTalentHexBytes (
                    'E8 52 55 33 00 90 90 90 90 90 90')
                Continuation = Convert-ProgressiveTalentHexBytes (
                    'DB 44 24 14 D8 4C 24 1C')
            },
            [pscustomobject]@{
                Label = 'next percent'; Offset = 0x208F02; Va = 0x00608F02
                TargetVa = 0x0093E380
                Original = Convert-ProgressiveTalentHexBytes (
                    '0F B6 48 25 83 C1 01 89 4C 24 14')
                Patched = Convert-ProgressiveTalentHexBytes (
                    'E8 79 54 33 00 90 90 90 90 90 90')
                Continuation = Convert-ProgressiveTalentHexBytes (
                    '83 EC 08 DB 44 24 1C')
            },
            [pscustomobject]@{
                Label = 'current flat'; Offset = 0x2090F4; Va = 0x006090F4
                TargetVa = 0x0093E38C
                Original = Convert-ProgressiveTalentHexBytes (
                    '0F B6 48 25 89 4C 24 14')
                Patched = Convert-ProgressiveTalentHexBytes (
                    'E8 93 52 33 00 90 90 90')
                Continuation = Convert-ProgressiveTalentHexBytes (
                    'DB 44 24 14 D8 4C 24 1C')
            },
            [pscustomobject]@{
                Label = 'current percent'; Offset = 0x2091C8; Va = 0x006091C8
                TargetVa = 0x0093E38C
                Original = Convert-ProgressiveTalentHexBytes (
                    '0F B6 48 25 89 4C 24 14')
                Patched = Convert-ProgressiveTalentHexBytes (
                    'E8 BF 51 33 00 90 90 90')
                Continuation = Convert-ProgressiveTalentHexBytes (
                    '83 EC 08 DB 44 24 1C')
            }
        )
    }
}

function Assert-ProgressiveTalentBinaryCompatible(
    [byte[]]$Data,
    [object]$Profile
) {
    if ($Data.Length -ne $Profile.Length) {
        throw "Unexpected Origin.exe length: $($Data.Length)."
    }
    $pe = Get-ProgressiveTalentPeMetadata $Data
    if ($pe.Machine -ne 0x014C -or $pe.OptionalMagic -ne 0x010B -or
        $pe.ImageBase -ne 0x00400000) {
        throw 'Origin.exe is not the audited x86 PE32 build.'
    }
    $caveVa = Resolve-ProgressiveTalentExecutableVa $pe (
        $Profile.CaveOffset) $Profile.CaveLength
    if ($caveVa -ne $Profile.CaveVa) {
        throw 'Origin.exe tooltip helper cave mapping is not exact.'
    }
    foreach ($hook in $Profile.Hooks) {
        $hookVa = Resolve-ProgressiveTalentExecutableVa $pe (
            $hook.Offset) $hook.Patched.Length
        $continuationMatches = Test-ProgressiveTalentBytes $Data (
            $hook.Offset + $hook.Original.Length) $hook.Continuation
        $targetVa = Get-ProgressiveTalentRelativeTarget (
            $hook.Patched) 0 $hook.Va
        if ($hookVa -ne $hook.Va -or -not $continuationMatches -or
            $targetVa -ne $hook.TargetVa) {
            throw "Origin.exe $($hook.Label) tooltip boundary is not exact."
        }
    }
    if ($Profile.HelperLength -ne 86) {
        throw 'Progressive-talent helper length is not the audited 86 bytes.'
    }
    return $pe
}

function Get-ProgressiveTalentBinaryState(
    [byte[]]$Data,
    [object]$Profile,
    [switch]$AuditXrefs
) {
    $pe = Assert-ProgressiveTalentBinaryCompatible $Data $Profile
    $hash = Get-ProgressiveTalentBytesSha256 $Data
    $source = $hash -eq $Profile.SourceSha256 -and
        (Test-ProgressiveTalentBytes $Data $Profile.CaveOffset (
            $Profile.EmptyCave))
    $patched = $hash -eq $Profile.PatchedSha256 -and
        (Test-ProgressiveTalentBytes $Data $Profile.CaveOffset (
            $Profile.PatchedCave))
    foreach ($hook in $Profile.Hooks) {
        $source = $source -and
            (Test-ProgressiveTalentBytes $Data $hook.Offset $hook.Original)
        $patched = $patched -and
            (Test-ProgressiveTalentBytes $Data $hook.Offset $hook.Patched)
    }
    if (-not $source -and -not $patched) {
        throw "Unsupported or partial tooltip binary state (SHA-256 $hash)."
    }
    $xrefs = @()
    if ($AuditXrefs) {
        $xrefs = @(Get-ProgressiveTalentRelativeCaveXrefs $Data $pe (
                $Profile.CaveVa) ($Profile.CaveVa + $Profile.CaveLength))
        $expected = if ($patched) { $Profile.Hooks.Count } else { 0 }
        if ($xrefs.Count -ne $expected) {
            throw "Tooltip cave xref audit expected $expected, found $($xrefs.Count)."
        }
        if ($patched) {
            $actual = @($xrefs | Sort-Object Offset)
            $hooks = @($Profile.Hooks | Sort-Object Offset)
            for ($index = 0; $index -lt $hooks.Count; $index++) {
                if ($actual[$index].Offset -ne $hooks[$index].Offset -or
                    $actual[$index].Target -ne $hooks[$index].TargetVa) {
                    throw 'Tooltip cave has an unexpected inbound xref.'
                }
            }
        }
    }
    return [pscustomobject]@{
        State = if ($patched) { 'Patched' } else { 'Original' }
        Sha256 = $hash
        CaveInboundRelativeXrefs = if ($AuditXrefs) { $xrefs.Count } else { $null }
    }
}

function Convert-ProgressiveTalentBinary(
    [byte[]]$Data,
    [object]$Profile,
    [ValidateSet('Original', 'Patched')]
    [string]$TargetState
) {
    Get-ProgressiveTalentBinaryState $Data $Profile | Out-Null
    [byte[]]$output = $Data.Clone()
    foreach ($hook in $Profile.Hooks) {
        $value = if ($TargetState -eq 'Patched') {
            $hook.Patched
        } else { $hook.Original }
        Copy-ProgressiveTalentBytes $value $output $hook.Offset
    }
    $cave = if ($TargetState -eq 'Patched') {
        $Profile.PatchedCave
    } else { $Profile.EmptyCave }
    Copy-ProgressiveTalentBytes $cave $output $Profile.CaveOffset
    $state = Get-ProgressiveTalentBinaryState $output $Profile -AuditXrefs
    if ($state.State -ne $TargetState) {
        throw 'Generated tooltip binary did not reach the requested state.'
    }
    return $output
}
