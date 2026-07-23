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
    if ($Data.Length -lt 0x100 -or $Data[0] -ne 0x4D -or $Data[1] -ne 0x5A) {
        throw 'Origin.exe does not have a valid DOS header.'
    }

    $peOffset = [BitConverter]::ToInt32($Data, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 24 -gt $Data.Length -or
        [BitConverter]::ToUInt32($Data, $peOffset) -ne 0x00004550) {
        throw 'Origin.exe does not have a valid PE header.'
    }

    $machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $optionalHeaderSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $optionalHeaderOffset = $peOffset + 24
    if ($sectionCount -le 0 -or $optionalHeaderSize -lt 0x60 -or
        $optionalHeaderOffset + $optionalHeaderSize -gt $Data.Length) {
        throw 'Origin.exe PE table is truncated or invalid.'
    }

    $optionalMagic = [BitConverter]::ToUInt16($Data, $optionalHeaderOffset)
    $imageBase = [BitConverter]::ToUInt32($Data, $optionalHeaderOffset + 28)
    $sectionTableOffset = $optionalHeaderOffset + $optionalHeaderSize
    if ($sectionTableOffset + ($sectionCount * 40) -gt $Data.Length) {
        throw 'Origin.exe section table is truncated.'
    }

    $sections = @()
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount; $sectionIndex++) {
        $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
        $nameBytes = $Data[$sectionOffset..($sectionOffset + 7)]
        $zeroIndex = [Array]::IndexOf([byte[]]$nameBytes, [byte]0)
        if ($zeroIndex -ge 0) {
            $nameBytes = $nameBytes[0..([Math]::Max(0, $zeroIndex - 1))]
        }
        $rawSize = [BitConverter]::ToUInt32($Data, $sectionOffset + 16)
        $rawOffset = [BitConverter]::ToUInt32($Data, $sectionOffset + 20)
        if ([uint64]$rawOffset + $rawSize -gt [uint64]$Data.Length) {
            throw 'Origin.exe contains a section outside the file.'
        }
        $sections += [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString($nameBytes)
            VirtualAddress = [BitConverter]::ToUInt32($Data, $sectionOffset + 12)
            RawSize = $rawSize
            RawOffset = $rawOffset
            Characteristics = [BitConverter]::ToUInt32($Data, $sectionOffset + 36)
        }
    }

    return [pscustomobject]@{
        Machine = $machine
        OptionalMagic = $optionalMagic
        ImageBase = $imageBase
        Sections = $sections
    }
}

function Resolve-ExecutableFileRange(
    [object]$PeMetadata,
    [int]$FileOffset,
    [int]$Length
) {
    if ($FileOffset -lt 0 -or $Length -le 0) {
        throw 'A requested PE file range is invalid.'
    }

    foreach ($section in $PeMetadata.Sections) {
        if ([uint64]$FileOffset -lt [uint64]$section.RawOffset -or
            [uint64]$FileOffset + $Length -gt
                [uint64]$section.RawOffset + $section.RawSize) {
            continue
        }
        if (($section.Characteristics -band 0x20000000) -eq 0) {
            throw "Origin.exe range at 0x$('{0:X}' -f $FileOffset) is not executable."
        }

        return [pscustomobject]@{
            Section = $section.Name
            Va = [uint64]$PeMetadata.ImageBase + $section.VirtualAddress +
                ([uint64]$FileOffset - $section.RawOffset)
        }
    }

    throw "Origin.exe range at 0x$('{0:X}' -f $FileOffset) is not in a PE section."
}

function Test-AllowedDifference(
    [int]$Offset,
    [object[]]$AllowedRanges
) {
    foreach ($range in $AllowedRanges) {
        if ($Offset -ge $range.Offset -and
            $Offset -lt $range.Offset + $range.Length) {
            return $true
        }
    }
    return $false
}

function Measure-ByteDifference(
    [byte[]]$Left,
    [byte[]]$Right
) {
    if ($Left.Length -ne $Right.Length) {
        throw 'Cannot compare byte sequences of different lengths.'
    }
    $count = 0
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { $count++ }
    }
    return $count
}
