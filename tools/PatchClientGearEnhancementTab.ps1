param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [ValidateSet('Verify', 'Apply', 'Revert')]
    [string]$Mode = 'Verify',
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

function Get-XmlPatchState([string]$Text, [string]$Label) {
    $marker = '<!-- Gear Enhancement forge tab (GWGE1) -->'
    $hasMarker = $Text.IndexOf($marker, [StringComparison]::Ordinal) -ge 0
    $hasOriginalRoot = $Text.IndexOf(
        '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="300,188,650,770" BtnRect="283,13,321,50"',
        [StringComparison]::Ordinal) -ge 0
    $hasWideRoot = $Text.IndexOf(
        '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="222,188,650,770" BtnRect="361,13,399,50"',
        [StringComparison]::Ordinal) -ge 0
    $hasOriginalLayout = $Text.IndexOf(
        '<Bag0 Type="Tab" Rectangle="11,-28,71,-5"',
        [StringComparison]::Ordinal) -ge 0
    $hasNarrowLayout = $Text.IndexOf(
        '<Bag0 Type="Tab" Rectangle="11,-28,53,-5"',
        [StringComparison]::Ordinal) -ge 0
    $hasNarrowBag4 = $Text.IndexOf(
        '<Bag4 Type="Tab" Rectangle="187,-28,295,-5"',
        [StringComparison]::Ordinal) -ge 0
    $hasWideBag4 = $Text.IndexOf(
        '<Bag4 Type="Tab" Rectangle="262,-28,370,-5"',
        [StringComparison]::Ordinal) -ge 0

    if (-not $hasMarker -and $hasOriginalRoot -and -not $hasWideRoot -and
        $hasOriginalLayout -and -not $hasNarrowLayout -and
        -not $hasNarrowBag4 -and -not $hasWideBag4) {
        return 'Original'
    }
    if ($hasMarker -and $hasOriginalRoot -and -not $hasWideRoot -and
        $hasNarrowLayout -and -not $hasOriginalLayout -and
        $hasNarrowBag4 -and -not $hasWideBag4) {
        return 'PatchedNarrow'
    }
    if ($hasMarker -and $hasWideRoot -and -not $hasOriginalRoot -and
        $hasOriginalLayout -and -not $hasNarrowLayout -and
        $hasWideBag4 -and -not $hasNarrowBag4) {
        return 'Patched'
    }
    throw "$Label is neither an exact original, legacy narrow, nor wide GWGE1 XML state."
}

function Set-XmlPatch([string]$Text, [bool]$Apply, [string]$Label) {
    $newLine = "`r`n"
    $state = Get-XmlPatchState $Text $Label
    $originalRoot = '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="300,188,650,770" BtnRect="283,13,321,50"'
    $wideRoot = '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="222,188,650,770" BtnRect="361,13,399,50"'
    $originalTabs = @(
        '<Bag0 Type="Tab" Rectangle="11,-28,71,-5"',
        '<Bag1 Type="Tab" Rectangle="74,-28,134,-5"',
        '<Bag2 Type="Tab" Rectangle="136,-28,197,-5"',
        '<Bag3 Type="Tab" Rectangle="198,-28,259,-5"'
    )
    $narrowTabs = @(
        '<Bag0 Type="Tab" Rectangle="11,-28,53,-5"',
        '<Bag1 Type="Tab" Rectangle="55,-28,97,-5"',
        '<Bag2 Type="Tab" Rectangle="99,-28,141,-5"',
        '<Bag3 Type="Tab" Rectangle="143,-28,185,-5"'
    )
    $originalPoints = @'
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="106,67,122,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="169,67,185,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="232,67,248,73" Text=""/>
'@ -replace "`n", $newLine
    $narrowPoints = @'
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="88,67,104,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="132,67,148,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="176,67,192,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="220,67,236,73" Text=""/>
'@ -replace "`n", $newLine
    $widePoints = @'
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="106,67,122,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="169,67,185,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="232,67,248,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="295,67,311,73" Text=""/>
'@ -replace "`n", $newLine
    $narrowBag4 = @'
     <!-- Gear Enhancement forge tab (GWGE1) -->
     <Bag4 Type="Tab" Rectangle="187,-28,295,-5" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="244,450" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_60">
       <EnhanceTitle Type="Text" Rectangle="18,42,244,82" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_61"/>
       <EnhanceInfo Type="Text" Rectangle="18,96,244,154" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_62"/>
     </Bag4>
     <!-- /Gear Enhancement forge tab (GWGE1) -->
'@ -replace "`n", $newLine
    $wideBag4 = @'
     <!-- Gear Enhancement forge tab (GWGE1) -->
     <Bag4 Type="Tab" Rectangle="262,-28,370,-5" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="244,450" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_60">
       <EnhanceTitle Type="Text" Rectangle="18,42,322,82" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_61"/>
       <EnhanceInfo Type="Text" Rectangle="18,96,322,154" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_62"/>
     </Bag4>
     <!-- /Gear Enhancement forge tab (GWGE1) -->
'@ -replace "`n", $newLine

    if ($Apply) {
        if ($state -eq 'Patched') {
            return $Text
        }

        $Text = Replace-ExactOnce $Text $originalRoot $wideRoot $Label
        if ($state -eq 'PatchedNarrow') {
            for ($index = 0; $index -lt $originalTabs.Count; $index++) {
                $Text = Replace-ExactOnce $Text $narrowTabs[$index] $originalTabs[$index] $Label
            }
            $Text = Replace-ExactOnce $Text $narrowPoints $widePoints $Label
            $Text = Replace-ExactOnce $Text $narrowBag4 $wideBag4 $Label
            return $Text
        }

        $Text = Replace-ExactOnce $Text $originalPoints $widePoints $Label
        $Text = Replace-ExactOnce $Text ("     </Bag3>${newLine}   </Bags>") (
            "     </Bag3>${newLine}${wideBag4}   </Bags>") $Label
        return $Text
    }

    if ($state -eq 'Original') {
        return $Text
    }
    if ($state -eq 'PatchedNarrow') {
        for ($index = 0; $index -lt $originalTabs.Count; $index++) {
            $Text = Replace-ExactOnce $Text $narrowTabs[$index] $originalTabs[$index] $Label
        }
        $Text = Replace-ExactOnce $Text $narrowPoints $originalPoints $Label
        $Text = Replace-ExactOnce $Text $narrowBag4 '' $Label
        return $Text
    }

    $Text = Replace-ExactOnce $Text $wideRoot $originalRoot $Label
    $Text = Replace-ExactOnce $Text $widePoints $originalPoints $Label
    $Text = Replace-ExactOnce $Text $wideBag4 '' $Label
    return $Text
}

function Get-TextPatchBlock([string]$Locale, [bool]$LegacyLabel) {
    $newLine = "`r`n"
    $values = if ($Locale -eq 'zh_cn') {
        $titleBase64 = if ($LegacyLabel) {
            '6KOF5aSH5bGe5oCn5by65YyW'
        }
        else {
            '6KOF5aSH'
        }
        @(
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
                $titleBase64)),
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
                '5by65YyW44CB5re75Yqg5oiW56e76Zmk6KOF5aSH6ZmE5Yqg5bGe5oCn44CC')),
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
                '5q2j5Zyo6L+e5o6l6KOF5aSH5bGe5oCn5by65YyW55WM6Z2i4oCm4oCm'))
        )
    }
    else {
        $title = if ($LegacyLabel) { 'Gear Enhancement' } else { 'Gear' }
        @($title,
            'Enhance, add, or remove gear attributes.',
            'Connecting to Gear Enhancement...')
    }
    return @(
        '-- Gear Enhancement forge tab (GWGE1)',
        ('EF_X0_60 = "{0}"' -f $values[0]),
        ('EF_X0_61 = "{0}"' -f $values[1]),
        ('EF_X0_62 = "{0}"' -f $values[2]),
        '-- /Gear Enhancement forge tab (GWGE1)',
        ''
    ) -join $newLine
}

function Test-OccursExactlyOnce([string]$Text, [string]$Value) {
    $first = $Text.IndexOf($Value, [StringComparison]::Ordinal)
    return $first -ge 0 -and $Text.IndexOf(
        $Value,
        $first + $Value.Length,
        [StringComparison]::Ordinal) -lt 0
}

function Get-TextPatchState(
    [string]$Text,
    [string]$Locale,
    [string]$Label
) {
    $tokens = @(
        '-- Gear Enhancement forge tab (GWGE1)',
        'EF_X0_60',
        'EF_X0_61',
        'EF_X0_62',
        '-- /Gear Enhancement forge tab (GWGE1)'
    )
    $hasAnyToken = @($tokens | Where-Object {
        $Text.IndexOf($_, [StringComparison]::Ordinal) -ge 0
    }).Count -gt 0
    if (-not $hasAnyToken) { return 'Original' }

    $hasExactEnvelope = @($tokens | Where-Object {
        -not (Test-OccursExactlyOnce $Text $_)
    }).Count -eq 0
    if ($hasExactEnvelope -and (Test-OccursExactlyOnce $Text (
            Get-TextPatchBlock $Locale $false))) {
        return 'Patched'
    }
    if ($hasExactEnvelope -and (Test-OccursExactlyOnce $Text (
            Get-TextPatchBlock $Locale $true))) {
        return 'PatchedLegacyLabel'
    }
    throw "$Label has a partial or conflicting GWGE1 text patch."
}

function Set-TextPatch(
    [string]$Text,
    [bool]$Apply,
    [string]$Locale,
    [string]$Label
) {
    $state = Get-TextPatchState $Text $Locale $Label
    $desiredBlock = Get-TextPatchBlock $Locale $false
    $legacyBlock = Get-TextPatchBlock $Locale $true

    if ($Apply) {
        if ($state -eq 'Patched') { return $Text }
        if ($state -eq 'PatchedLegacyLabel') {
            return Replace-ExactOnce $Text $legacyBlock $desiredBlock $Label
        }
        return Replace-ExactOnce $Text '--Event.xml' (
            $desiredBlock + '--Event.xml') $Label
    }
    if ($state -eq 'Original') { return $Text }
    $block = if ($state -eq 'Patched') { $desiredBlock } else { $legacyBlock }
    return Replace-ExactOnce $Text $block '' $Label
}

$originPath = Join-Path $ClientRoot 'Origin.exe'
$locales = @('en_us', 'zh_cn')
$xmlPaths = @{}
$textPaths = @{}
foreach ($locale in $locales) {
    $xmlPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\XML\EquipForgeExUI.xml")
    $textPaths[$locale] = Join-Path $ClientRoot (
        "Localization\$locale\UI\Base\text.lua")
}

$allPaths = @($originPath) + @($xmlPaths.Values) + @($textPaths.Values)
foreach ($path in $allPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client file is missing: $path"
    }
}

[byte[]]$origin = [IO.File]::ReadAllBytes($originPath)
$pe = Get-PeMetadata $origin
if ($origin.Length -ne 6676480 -or $pe.Machine -ne 0x014C -or
    $pe.OptionalMagic -ne 0x010B -or $pe.ImageBase -ne 0x00400000) {
    throw 'Origin.exe is not the supported 32-bit 6,676,480-byte client build.'
}

# VA 0x0055F3CD is the selected-forge-tab dispatch JNE. The replacement
# always routes through an isolated cave. Index 3 returns to the untouched
# replacement handler, indices 0-2 return to ordinary forging, and index 4
# closes the forge with its established cross-modal reset/hide sequence before
# sending the native fixed-size 48-byte NpcDialogOpen request. The request is
# 30 00 53 27 FF FF FF FF followed by 40 zero bytes. The server
# resolves NPC ID -1 to the character's faction-correct dialog-118 endpoint.
$hookOffset = 0x15F3CD
$hookVa = 0x0055F3CD
$caveOffset = 0x5C3380
$caveVa = 0x009C3380
[byte[]]$originalHook = Convert-HexBytes '0F 85 43 01 00 00'
[byte[]]$patchedHook = Convert-HexBytes 'E9 AE 3F 46 00 90'
[byte[]]$legacyCaveCode = Convert-HexBytes @'
83 F8 03 0F 84 4A C0 B9 FF 83 F8 04 0F 85 84 C1 B9 FF
9C 60 8B C6 E8 55 D7 B9 FF E8 C0 9F B9 FF 31 DB E8 C9
A7 B9 FF 83 EC 08 C7 04 24 08 00 53 27 C7 44 24 04 FF
FF FF FF 8B 0D 50 61 57 01 8B 11 8B 52 1C 6A 08 8D 44
24 04 50 FF D2 83 C4 08 61 9D E9 CF C3 B9 FF
'@
[byte[]]$sendFirstCaveCode = Convert-HexBytes @'
83 F8 03 0F 84 4A C0 B9 FF 83 F8 04 0F 85 84 C1 B9 FF
9C 60 83 EC 30 31 C0 89 E7 B9 0C 00 00 00 FC F3 AB C7
04 24 30 00 53 27 C7 44 24 04 FF FF FF FF 8B 0D 50 61
57 01 8B 11 8B 52 1C 6A 30 8D 44 24 04 50 FF D2 83 C4
30 8B C6 E8 20 D7 B9 FF E8 8B 9F B9 FF 31 DB E8 94 A7
B9 FF 61 9D E9 C3 C3 B9 FF
'@
[byte[]]$caveCode = Convert-HexBytes @'
83 F8 03 0F 84 4A C0 B9 FF 83 F8 04 0F 85 84 C1 B9 FF
9C 60 8B C6 E8 55 D7 B9 FF E8 C0 9F B9 FF 31 DB E8 C9
A7 B9 FF 83 EC 30 31 C0 89 E7 B9 0C 00 00 00 FC F3 AB
C7 04 24 30 00 53 27 C7 44 24 04 FF FF FF FF 8B 0D 50
61 57 01 8B 11 8B 52 1C 6A 30 8D 44 24 04 50 FF D2 83
C4 30 61 9D E9 C3 C3 B9 FF
'@
[byte[]]$emptyCave = [byte[]]::new($caveCode.Length)
[byte[]]$legacyPaddedCave = [byte[]]::new($caveCode.Length)
Copy-Bytes $legacyCaveCode $legacyPaddedCave 0

$hookMapping = Resolve-ExecutableFileRange $pe $hookOffset $patchedHook.Length
$caveMapping = Resolve-ExecutableFileRange $pe $caveOffset $caveCode.Length
if ($hookMapping.Va -ne $hookVa -or $caveMapping.Va -ne $caveVa -or
    $hookMapping.Section -ne '.text' -or $caveMapping.Section -ne '.rdata') {
    throw 'Origin.exe hook/cave PE mappings do not match the supported build.'
}
if ($legacyCaveCode.Length -ne 87 -or
    $sendFirstCaveCode.Length -ne 99 -or $caveCode.Length -ne 99 -or
    (Get-RelativeTarget $patchedHook 1 ($hookVa + 5)) -ne $caveVa -or
    (Get-RelativeTarget $caveCode 5 ($caveVa + 9)) -ne 0x0055F3D3 -or
    (Get-RelativeTarget $caveCode 14 ($caveVa + 18)) -ne 0x0055F516 -or
    (Get-RelativeTarget $caveCode 23 ($caveVa + 27)) -ne 0x00560AF0 -or
    (Get-RelativeTarget $caveCode 28 ($caveVa + 32)) -ne 0x0055D360 -or
    (Get-RelativeTarget $caveCode 35 ($caveVa + 39)) -ne 0x0055DB70 -or
    -not (Test-Bytes $caveCode 39 (Convert-HexBytes (
        '83 EC 30 31 C0 89 E7 B9 0C 00 00 00 FC F3 AB'))) -or
    -not (Test-Bytes $caveCode 54 (Convert-HexBytes 'C7 04 24 30 00 53 27')) -or
    -not (Test-Bytes $caveCode 61 (Convert-HexBytes 'C7 44 24 04 FF FF FF FF')) -or
    -not (Test-Bytes $caveCode 80 (Convert-HexBytes '6A 30 8D 44 24 04 50 FF D2')) -or
    (Get-RelativeTarget $caveCode 95 ($caveVa + 99)) -ne 0x0055F7A6) {
    throw 'Internal GWGE1 branch encoding verification failed.'
}

$hasOriginalHook = Test-Bytes $origin $hookOffset $originalHook
$hasPatchedHook = Test-Bytes $origin $hookOffset $patchedHook
$hasEmptyCave = Test-Bytes $origin $caveOffset $emptyCave
$hasPatchedCave = Test-Bytes $origin $caveOffset $caveCode
$hasSendFirstCave = Test-Bytes $origin $caveOffset $sendFirstCaveCode
$hasLegacyCave = Test-Bytes $origin $caveOffset $legacyPaddedCave
$nativeState = if ($hasOriginalHook -and $hasEmptyCave) {
    'Original'
}
elseif ($hasPatchedHook -and $hasPatchedCave) {
    'Patched'
}
elseif ($hasPatchedHook -and $hasSendFirstCave) {
    'PatchedSendFirst'
}
elseif ($hasPatchedHook -and $hasLegacyCave) {
    'PatchedLegacy'
}
else {
    throw 'Origin.exe has a partial/conflicting forge-tab hook or occupied GWGE1 cave.'
}

$xmlDocuments = @{}
$xmlStates = @{}
$textDocuments = @{}
$textStates = @{}
foreach ($locale in $locales) {
    $xmlDocuments[$locale] = Read-Utf8File $xmlPaths[$locale]
    $xmlStates[$locale] = Get-XmlPatchState $xmlDocuments[$locale].Text (
        "$locale EquipForgeExUI.xml")
    $textDocuments[$locale] = Read-Utf8File $textPaths[$locale]
    $textStates[$locale] = Get-TextPatchState $textDocuments[$locale].Text $locale (
        "$locale text.lua")
}

if ($Mode -eq 'Verify') {
    [pscustomobject]@{
        Mode = $Mode
        NativeState = $nativeState
        EnUsXmlState = $xmlStates['en_us']
        ZhCnXmlState = $xmlStates['zh_cn']
        EnUsTextState = $textStates['en_us']
        ZhCnTextState = $textStates['zh_cn']
        HookVa = ('0x{0:X8}' -f $hookVa)
        CaveVa = ('0x{0:X8}' -f $caveVa)
        CaveBytes = $caveCode.Length
        LauncherPacket = '30005327FFFFFFFF + 40 zero bytes'
        ServerDialog = 118
    }
    return
}

$desiredPatched = $Mode -eq 'Apply'
$alreadyDesired = if ($desiredPatched) {
    $nativeState -eq 'Patched' -and
        @($locales | Where-Object {
            $xmlStates[$_] -ne 'Patched' -or $textStates[$_] -ne 'Patched'
        }).Count -eq 0
}
else {
    $nativeState -eq 'Original' -and
        @($locales | Where-Object {
            $xmlStates[$_] -ne 'Original' -or $textStates[$_] -ne 'Original'
        }).Count -eq 0
}
if ($alreadyDesired) {
    [pscustomobject]@{ Mode = $Mode; State = 'AlreadyDesired'; ClientRoot = $ClientRoot }
    return
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path -Parent $ClientRoot) 'backups'
}
$clientRootFull = [IO.Path]::GetFullPath($ClientRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($clientRootFull -eq [IO.Path]::GetPathRoot($clientRootFull)) {
    throw 'ClientRoot cannot be a filesystem root.'
}
$clientRootPrefix = $clientRootFull + [IO.Path]::DirectorySeparatorChar
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$backupDirectory = Join-Path $BackupRoot ("client-gear-enhancement-tab-$Mode-$timestamp")
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
foreach ($path in $allPaths) {
    $pathFull = [IO.Path]::GetFullPath($path)
    if (-not $pathFull.StartsWith($clientRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to back up a path outside ClientRoot: $pathFull"
    }
    $relativePath = $pathFull.Substring($clientRootPrefix.Length)
    $backupPath = Join-Path $backupDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force |
        Out-Null
    Copy-Item -LiteralPath $path -Destination $backupPath
}

if ($desiredPatched -and $nativeState -ne 'Patched') {
    Copy-Bytes $patchedHook $origin $hookOffset
    Copy-Bytes $caveCode $origin $caveOffset
}
elseif (-not $desiredPatched -and $nativeState -ne 'Original') {
    Copy-Bytes $originalHook $origin $hookOffset
    Copy-Bytes $emptyCave $origin $caveOffset
}

$updatedXml = @{}
$updatedText = @{}
foreach ($locale in $locales) {
    $updatedXml[$locale] = if (($desiredPatched -and $xmlStates[$locale] -ne 'Patched') -or
        (-not $desiredPatched -and $xmlStates[$locale] -ne 'Original')) {
        Set-XmlPatch $xmlDocuments[$locale].Text $desiredPatched (
            "$locale EquipForgeExUI.xml")
    }
    else { $xmlDocuments[$locale].Text }

    $updatedText[$locale] = if (($desiredPatched -and $textStates[$locale] -ne 'Patched') -or
        (-not $desiredPatched -and $textStates[$locale] -ne 'Original')) {
        Set-TextPatch $textDocuments[$locale].Text $desiredPatched $locale (
            "$locale text.lua")
    }
    else { $textDocuments[$locale].Text }

    $xmlCheck = [Xml.XmlDocument]::new()
    $xmlCheck.PreserveWhitespace = $true
    $xmlCheck.LoadXml($updatedXml[$locale])
}

Write-AtomicBytes $originPath $origin
foreach ($locale in $locales) {
    Write-AtomicBytes $xmlPaths[$locale] (Convert-ToUtf8Bytes $updatedXml[$locale] (
        $xmlDocuments[$locale].HasBom))
    Write-AtomicBytes $textPaths[$locale] (Convert-ToUtf8Bytes $updatedText[$locale] (
        $textDocuments[$locale].HasBom))
}

[byte[]]$writtenOrigin = [IO.File]::ReadAllBytes($originPath)
$expectedHook = if ($desiredPatched) { $patchedHook } else { $originalHook }
$expectedCave = if ($desiredPatched) { $caveCode } else { $emptyCave }
if (-not (Test-Bytes $writtenOrigin $hookOffset $expectedHook) -or
    -not (Test-Bytes $writtenOrigin $caveOffset $expectedCave)) {
    throw 'Origin.exe post-write verification failed.'
}
foreach ($locale in $locales) {
    $xmlReadback = Read-Utf8File $xmlPaths[$locale]
    $textReadback = Read-Utf8File $textPaths[$locale]
    $expectedState = if ($desiredPatched) { 'Patched' } else { 'Original' }
    if ((Get-XmlPatchState $xmlReadback.Text "$locale XML readback") -ne $expectedState -or
        (Get-TextPatchState $textReadback.Text $locale "$locale text readback") -ne
            $expectedState) {
        throw "$locale localization post-write verification failed."
    }
}

[pscustomobject]@{
    Mode = $Mode
    State = if ($desiredPatched) { 'Patched' } else { 'Original' }
    ClientRoot = $ClientRoot
    BackupDirectory = $backupDirectory
    OriginSha256 = (Get-FileHash -LiteralPath $originPath -Algorithm SHA256).Hash
    HookVa = ('0x{0:X8}' -f $hookVa)
    CaveVa = ('0x{0:X8}' -f $caveVa)
    LauncherPacket = '30005327FFFFFFFF + 40 zero bytes'
    ServerDialog = 118
}
