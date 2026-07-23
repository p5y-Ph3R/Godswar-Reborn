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

    $sectionCount = [BitConverter]::ToUInt16($Data, $peOffset + 6)
    $optionalHeaderSize = [BitConverter]::ToUInt16($Data, $peOffset + 20)
    $optionalHeaderOffset = $peOffset + 24
    $sectionTableOffset = $optionalHeaderOffset + $optionalHeaderSize
    if ($sectionCount -le 0 -or $optionalHeaderSize -lt 0x60 -or
        $sectionTableOffset + ($sectionCount * 40) -gt $Data.Length) {
        throw 'Origin.exe PE headers are truncated or invalid.'
    }

    $sections = @()
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount; $sectionIndex++) {
        $sectionOffset = $sectionTableOffset + ($sectionIndex * 40)
        $nameBytes = [byte[]]$Data[$sectionOffset..($sectionOffset + 7)]
        $zeroIndex = [Array]::IndexOf($nameBytes, [byte]0)
        if ($zeroIndex -eq 0) {
            $nameBytes = @()
        }
        elseif ($zeroIndex -gt 0) {
            $nameBytes = $nameBytes[0..($zeroIndex - 1)]
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
        Machine = [BitConverter]::ToUInt16($Data, $peOffset + 4)
        OptionalMagic = [BitConverter]::ToUInt16($Data, $optionalHeaderOffset)
        ImageBase = [BitConverter]::ToUInt32($Data, $optionalHeaderOffset + 28)
        Sections = $sections
    }
}

function Resolve-ExecutableFileRange(
    [object]$PeMetadata,
    [int]$FileOffset,
    [int]$Length
) {
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

function Get-RelativeTarget(
    [byte[]]$Code,
    [int]$DisplacementOffset,
    [uint64]$InstructionNextVa
) {
    return [int64]$InstructionNextVa + [BitConverter]::ToInt32(
        $Code,
        $DisplacementOffset)
}

function Read-Utf8File([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    return [pscustomobject]@{ Text = $text; HasBom = $hasBom }
}

function Convert-ToUtf8Bytes([string]$Text, [bool]$HasBom) {
    [byte[]]$body = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    if (-not $HasBom) {
        return $body
    }

    [byte[]]$result = [byte[]]::new($body.Length + 3)
    $result[0] = 0xEF
    $result[1] = 0xBB
    $result[2] = 0xBF
    [Array]::Copy($body, 0, $result, 3, $body.Length)
    return $result
}

function Write-AtomicBytes([string]$Path, [byte[]]$Bytes) {
    $directory = Split-Path -Parent $Path
    $temporaryPath = Join-Path $directory ('.gwge1-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporaryPath, $Bytes)
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Replace-ExactOnce(
    [string]$Text,
    [string]$OldValue,
    [string]$NewValue,
    [string]$Label
) {
    $first = $Text.IndexOf($OldValue, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "$Label does not contain its expected source text."
    }
    if ($Text.IndexOf($OldValue, $first + $OldValue.Length,
            [StringComparison]::Ordinal) -ge 0) {
        throw "$Label contains its expected source text more than once."
    }
    return $Text.Substring(0, $first) + $NewValue +
        $Text.Substring($first + $OldValue.Length)
}
