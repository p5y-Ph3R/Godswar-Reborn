[CmdletBinding()]
param(
    [string]$ClientPath = 'C:\Godswar Origin',
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$files = @(
    @{
        Path = 'Localization\en_us\Text\DesigName.dat'
        PreserveLocalizedText = $true
        Lines = @(
            "5009`t|cffA335EE[Medusa Executioners]|cFFFFFFFF",
            "5010`t|cffA335EE[Medusa Slayers]|cFFFFFFFF",
            "5011`t|cffA335EE[Medusa Challengers]|cFFFFFFFF",
            "5152`t|cffDC143C[Heir of Perseus]|cFFFFFFFF",
            "5153`t|cffFF8000[Bane of the Three Sisters]|cFFFFFFFF",
            "5154`t|cffFF8000[Gorgon Breaker]|cFFFFFFFF"
        )
    },
    @{
        Path = 'Localization\en_us\Text\DesigInfo.dat'
        ReplaceExisting = $true
        Lines = @(
            "5009`tEnhanced: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +1%. Only the strongest Medusa title bonus applies.",
            "5010`tEnhanced: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +2%. Only the strongest Medusa title bonus applies.",
            "5011`tEnhanced: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +3%. Only the strongest Medusa title bonus applies.",
            "5152`tMythic: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +6%. Only the strongest Medusa title bonus applies.",
            "5153`tMythic: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +5%. Only the strongest Medusa title bonus applies.",
            "5154`tMythic: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +4%. Only the strongest Medusa title bonus applies."
        )
    },
    @{
        Path = 'Localization\zh_cn\Text\DesigName.dat'
        PreserveLocalizedText = $true
        Lines = @(
            "5009`t|cffA335EE[Medusa Executioners]|cFFFFFFFF",
            "5010`t|cffA335EE[Medusa Slayers]|cFFFFFFFF",
            "5011`t|cffA335EE[Medusa Challengers]|cFFFFFFFF",
            "5152`t|cffDC143C[Heir of Perseus]|cFFFFFFFF",
            "5153`t|cffFF8000[Bane of the Three Sisters]|cFFFFFFFF",
            "5154`t|cffFF8000[Gorgon Breaker]|cFFFFFFFF"
        )
    },
    @{
        Path = 'Localization\zh_cn\Text\DesigInfo.dat'
        ReplaceExisting = $true
        Lines = @(
            "5009`tEnhanced: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +1%. Only the strongest Medusa title bonus applies.",
            "5010`tEnhanced: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +2%. Only the strongest Medusa title bonus applies.",
            "5011`tEnhanced: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +3%. Only the strongest Medusa title bonus applies.",
            "5152`tMythic: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +6%. Only the strongest Medusa title bonus applies.",
            "5153`tMythic: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +5%. Only the strongest Medusa title bonus applies.",
            "5154`tMythic: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +4%. Only the strongest Medusa title bonus applies."
        )
    }
)
$encoding = [Text.UnicodeEncoding]::new($false, $true, $true)

foreach ($file in $files) {
    $path = Join-Path $ClientPath $file.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing title localization: $path"
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 20000 -or
        $bytes.Length -lt 2 -or
        $bytes[0] -ne 0xFF -or
        $bytes[1] -ne 0xFE) {
        throw "Title localization has an unsupported size or encoding: $path"
    }

    $content = $encoding.GetString($bytes, 2, $bytes.Length - 2)
    $changed = $false
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($line in $file.Lines) {
        $separator = $line.IndexOf("`t", [StringComparison]::Ordinal)
        if ($separator -le 0) {
            throw "Invalid authored title row: $line"
        }

        $id = $line.Substring(0, $separator)
        $matches = [regex]::Matches(
            $content,
            "(?m)^$id\t[^\r\n]*(?=\r?$)",
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($matches.Count -eq 0) {
            $missing.Add($line)
            continue
        }
        if ($matches.Count -ne 1) {
            throw "Medusa title ID $id is duplicated: $path"
        }

        $current = $matches[0].Value
        if ($current.Equals($line, [StringComparison]::Ordinal)) {
            continue
        }

        if ($file.ContainsKey('PreserveLocalizedText') -and
            $file.PreserveLocalizedText) {
            $color = $line.Substring($separator + 1, 10)
            $localizedText = [regex]::Replace(
                $current.Substring($separator + 1),
                '\|c[0-9A-Fa-f]{8}',
                '')
            if ($localizedText.Length -ge 2 -and
                $localizedText[0] -eq '[' -and
                $localizedText[$localizedText.Length - 1] -eq ']') {
                $localizedText = $localizedText.Substring(
                    1,
                    $localizedText.Length - 2)
            }
            $localizedLine = "$id`t" + $color + '[' +
                $localizedText + ']|cFFFFFFFF'
            if (-not $current.Equals(
                    $localizedLine,
                    [StringComparison]::Ordinal)) {
                $content = $content.Replace($current, $localizedLine)
                $changed = $true
            }
            continue
        }

        if ($file.ContainsKey('ReplaceExisting') -and
            $file.ReplaceExisting) {
            $content = $content.Replace($current, $line)
            $changed = $true
            continue
        }

        $plain = [regex]::Replace($line, '\|c[0-9A-Fa-f]{8}', '')
        if (-not $current.Equals($plain, [StringComparison]::Ordinal)) {
            throw "Medusa title ID $id has contradictory content: $path"
        }
        $content = $content.Replace($current, $line)
        $changed = $true
    }

    if ($missing.Count -gt 0) {
        if (@($missing | Where-Object { $_ -notmatch '^515[2-4]\t' }).Count -gt 0) {
            throw "Stock Medusa title localization is missing: $path"
        }
        $anchorMatches = [regex]::Matches(
            $content,
            '(?m)^5151\t[^\r\n]*(?=\r?$)',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($anchorMatches.Count -ne 1) {
            throw "Medusa title insertion anchor is not exact: $path"
        }
        $anchor = $anchorMatches[0].Value
        $content = $content.Replace(
            $anchor,
            $anchor + "`r`n" + [string]::Join("`r`n", $missing))
        $changed = $true
    }

    if ($changed -and $ValidateOnly) {
        throw "Medusa title localization requires an update: $path"
    }
    if ($changed) {
        [IO.File]::WriteAllText($path, $content, $encoding)
    }

    if ((Get-Item -LiteralPath $path).Length -ge 20000) {
        throw "Patched title localization exceeds 20 KB: $path"
    }
    Write-Output "Medusa titles ready: $path"
}

function Convert-HexBytes {
    param([string]$Hex)

    $compact = $Hex -replace '[^0-9A-Fa-f]', ''
    if (($compact.Length % 2) -ne 0) {
        throw "Invalid hexadecimal byte sequence: $Hex"
    }
    [byte[]]$result = [byte[]]::new($compact.Length / 2)
    for ($index = 0; $index -lt $result.Length; $index++) {
        $result[$index] = [Convert]::ToByte(
            $compact.Substring($index * 2, 2),
            16)
    }
    return $result
}

function Test-BytesAtOffset {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [byte[]]$Expected
    )

    if ($Offset -lt 0 -or $Offset + $Expected.Length -gt $Bytes.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Copy-BytesAtOffset {
    param(
        [byte[]]$Target,
        [int]$Offset,
        [byte[]]$Source
    )

    [Array]::Copy($Source, 0, $Target, $Offset, $Source.Length)
}

function New-RelativeCall {
    param(
        [uint32]$SourceVa,
        [uint32]$TargetVa,
        [ValidateRange(5, 16)]
        [int]$Length = 5
    )

    $relative = [int64]$TargetVa - ([int64]$SourceVa + 5)
    if ($relative -lt [int32]::MinValue -or
        $relative -gt [int32]::MaxValue) {
        throw 'Relative call target exceeds the x86 displacement range.'
    }
    [byte[]]$result = [byte[]]::new($Length)
    $result[0] = 0xE8
    [Array]::Copy(
        [BitConverter]::GetBytes([int32]$relative),
        0,
        $result,
        1,
        4)
    for ($index = 5; $index -lt $Length; $index++) {
        $result[$index] = 0x90
    }
    return $result
}

$originPath = Join-Path $ClientPath 'Origin.exe'
if (-not (Test-Path -LiteralPath $originPath -PathType Leaf)) {
    throw "Missing client executable: $originPath"
}

[byte[]]$origin = [IO.File]::ReadAllBytes($originPath)
$expectedOriginLength = 6676480
$formatterEntryOffset = 0x20921
$formatterEntryVa = [uint32]0x00420921
$formatterBodyOffset = 0x20991
$formatterBodyVa = [uint32]0x00420991
$visibleWidthEntryOffset = 0x21BD1
$visibleWidthEntryVa = [uint32]0x00421BD1
$visibleWidthBodyOffset = 0x21C41
$visibleWidthBodyVa = [uint32]0x00421C41
$selfHoverWidthOffset = 0x21CE1
$selfHoverWidthVa = [uint32]0x00421CE1
$hoverWidthEntryOffset = 0x22111
$hoverWidthEntryVa = [uint32]0x00422111
$hoverWidthBodyOffset = 0x22121
$hoverWidthBodyVa = [uint32]0x00422121
$stockFormatterVa = [uint32]0x00402DF0
$unwrappedFormatVa = [uint32]0x00952204
$formatterHookOffsets = @(0x210C0, 0x21185, 0x211AD)
$directWidthOffsets = @(0xA3E08, 0xA3FB5)
$viaEaxWidthOffsets = @(
    0xA3E94,
    0xA4041,
    0xA4140,
    0xA41C3,
    0xA42D0,
    0xA4353,
    0xA4451,
    0xA44D4,
    0xA45F4,
    0xA467C,
    0xA4780,
    0xA4803,
    0xA4901,
    0xA4984,
    0xA4A90,
    0xA4B13,
    0xA4C11,
    0xA4C94)
$hoverWidthOffsets = @(
    0x6D285,
    0x6D322,
    0x6D3A9,
    0x6D440,
    0x6D4E3,
    0x6D57A,
    0x6D603,
    0x6D698,
    0x6D746,
    0x6D7E2,
    0x6D873,
    0x6D908,
    0x6D993,
    0x6DA2A)
$selfHoverWidthOffsets = @(0x6CD43, 0x6CE26)

[byte[]]$emptyCave = [byte[]]::new(15)
for ($index = 0; $index -lt $emptyCave.Length; $index++) {
    $emptyCave[$index] = 0xCC
}
[byte[]]$formatterEntry = Convert-HexBytes @'
8B 44 24 08 80 38 7C 75 6F E9 62 00 00 00 CC
'@
$formatterJumpFromVa = [uint32]($formatterBodyVa + 8)
$formatterRelative = [int32](
    [int64]$stockFormatterVa - ([int64]$formatterJumpFromVa + 5))
[byte[]]$formatterBody = [byte[]]::new(15)
[byte[]]$formatterBodyPrefix = Convert-HexBytes @'
C7 44 24 04 04 22 95 00 E9
'@
Copy-BytesAtOffset $formatterBody 0 $formatterBodyPrefix
[Array]::Copy(
    [BitConverter]::GetBytes($formatterRelative),
    0,
    $formatterBody,
    $formatterBodyPrefix.Length,
    4)
for ($index = 13; $index -lt $formatterBody.Length; $index++) {
    $formatterBody[$index] = 0xCC
}
[byte[]]$visibleWidthEntry = Convert-HexBytes @'
89 F1 80 BF A1 09 00 00 7C 74 65 EB 66 CC CC
'@
[byte[]]$visibleWidthBody = Convert-HexBytes @'
83 E9 14 C1 E1 03 C3 CC CC CC CC CC CC CC CC
'@
[byte[]]$selfHoverWidth = [byte[]]::new(15)
Copy-BytesAtOffset $selfHoverWidth 0 (Convert-HexBytes '57 89 DF')
Copy-BytesAtOffset $selfHoverWidth 3 (
    New-RelativeCall ([uint32]($selfHoverWidthVa + 3)) $visibleWidthEntryVa)
Copy-BytesAtOffset $selfHoverWidth 8 (
    Convert-HexBytes '5F C3 CC CC CC CC CC')
[byte[]]$hoverWidthEntry = Convert-HexBytes @'
56 57 89 F0 89 FE 8B B8 18 01 00 00 EB 02 CC
'@
[byte[]]$hoverWidthBody = [byte[]]::new(15)
Copy-BytesAtOffset $hoverWidthBody 0 (
    New-RelativeCall $hoverWidthBodyVa $visibleWidthEntryVa)
Copy-BytesAtOffset $hoverWidthBody 5 (
    Convert-HexBytes '89 C8 5F 5E C3 CC CC CC CC CC')
[byte[]]$stockDirectWidth = Convert-HexBytes '8D 0C F5 00 00 00 00'
[byte[]]$stockViaEaxWidth = Convert-HexBytes '8D 04 F5 00 00 00 00'
[byte[]]$stockHoverWidth = Convert-HexBytes '8D 04 FD 00 00 00 00'
[byte[]]$stockViaEaxCopy = Convert-HexBytes '8B C8'
[byte[]]$patchedViaEaxCopy = Convert-HexBytes '90 90'

if ($origin.Length -ne $expectedOriginLength -or
    $origin[0] -ne 0x4D -or $origin[1] -ne 0x5A -or
    -not (Test-BytesAtOffset $origin 0x552204 (
            Convert-HexBytes '25 73 00')) -or
    -not (Test-BytesAtOffset $origin 0x5503F8 (
            Convert-HexBytes '5B 25 73 5D 00'))) {
    throw 'Origin.exe does not match the reviewed title-rendering layout.'
}

$normalSourceState =
    (Test-BytesAtOffset $origin $formatterEntryOffset $emptyCave) -and
    (Test-BytesAtOffset $origin $formatterBodyOffset $emptyCave) -and
    (Test-BytesAtOffset $origin $visibleWidthEntryOffset $emptyCave) -and
    (Test-BytesAtOffset $origin $visibleWidthBodyOffset $emptyCave)
$normalPatchedState =
    (Test-BytesAtOffset $origin $formatterEntryOffset $formatterEntry) -and
    (Test-BytesAtOffset $origin $formatterBodyOffset $formatterBody) -and
    (Test-BytesAtOffset $origin $visibleWidthEntryOffset $visibleWidthEntry) -and
    (Test-BytesAtOffset $origin $visibleWidthBodyOffset $visibleWidthBody)
$hoverSourceState =
    (Test-BytesAtOffset $origin $hoverWidthEntryOffset $emptyCave) -and
    (Test-BytesAtOffset $origin $hoverWidthBodyOffset $emptyCave)
$hoverPatchedState =
    (Test-BytesAtOffset $origin $hoverWidthEntryOffset $hoverWidthEntry) -and
    (Test-BytesAtOffset $origin $hoverWidthBodyOffset $hoverWidthBody)
$selfHoverSourceState =
    (Test-BytesAtOffset $origin $selfHoverWidthOffset $emptyCave)
$selfHoverPatchedState =
    (Test-BytesAtOffset $origin $selfHoverWidthOffset $selfHoverWidth)

foreach ($offset in $formatterHookOffsets) {
    $va = [uint32](0x00400000 + $offset)
    $normalSourceState = $normalSourceState -and (Test-BytesAtOffset `
        $origin $offset (New-RelativeCall $va $stockFormatterVa))
    $normalPatchedState = $normalPatchedState -and (Test-BytesAtOffset `
        $origin $offset (New-RelativeCall $va $formatterEntryVa))
}
foreach ($offset in $directWidthOffsets) {
    $va = [uint32](0x00400000 + $offset)
    $normalSourceState = $normalSourceState -and
        (Test-BytesAtOffset $origin $offset $stockDirectWidth)
    $normalPatchedState = $normalPatchedState -and (Test-BytesAtOffset `
        $origin $offset (New-RelativeCall $va $visibleWidthEntryVa 7))
}
foreach ($offset in $viaEaxWidthOffsets) {
    $va = [uint32](0x00400000 + $offset)
    $normalSourceState = $normalSourceState -and
        (Test-BytesAtOffset $origin $offset $stockViaEaxWidth) -and
        (Test-BytesAtOffset $origin ($offset + 13) $stockViaEaxCopy)
    $normalPatchedState = $normalPatchedState -and
        (Test-BytesAtOffset $origin $offset (
            New-RelativeCall $va $visibleWidthEntryVa 7)) -and
        (Test-BytesAtOffset $origin ($offset + 13) $patchedViaEaxCopy)
}
foreach ($offset in $hoverWidthOffsets) {
    $va = [uint32](0x00400000 + $offset)
    $hoverSourceState = $hoverSourceState -and
        (Test-BytesAtOffset $origin $offset $stockHoverWidth)
    $hoverPatchedState = $hoverPatchedState -and
        (Test-BytesAtOffset $origin $offset (
            New-RelativeCall $va $hoverWidthEntryVa 7))
}
foreach ($offset in $selfHoverWidthOffsets) {
    $va = [uint32](0x00400000 + $offset)
    $selfHoverSourceState = $selfHoverSourceState -and
        (Test-BytesAtOffset $origin $offset $stockDirectWidth)
    $selfHoverPatchedState = $selfHoverPatchedState -and
        (Test-BytesAtOffset $origin $offset (
            New-RelativeCall $va $selfHoverWidthVa 7))
}

$sourceState = $normalSourceState -and $hoverSourceState -and
    $selfHoverSourceState
$normalUpgradeState = $normalPatchedState -and $hoverSourceState -and
    $selfHoverSourceState
$hoverUpgradeState = $normalPatchedState -and $hoverPatchedState -and
    $selfHoverSourceState
$patchedState = $normalPatchedState -and $hoverPatchedState -and
    $selfHoverPatchedState

if (-not $sourceState -and -not $normalUpgradeState -and
    -not $hoverUpgradeState -and -not $patchedState) {
    throw 'Origin.exe has a partial or contradictory title-rendering patch.'
}
if ($ValidateOnly -and -not $patchedState) {
    throw "Medusa title presentation requires an update: $originPath"
}
if (-not $ValidateOnly -and ($sourceState -or $normalUpgradeState -or
        $hoverUpgradeState)) {
    [byte[]]$patchedOrigin = [byte[]]$origin.Clone()
    if ($normalSourceState) {
        Copy-BytesAtOffset $patchedOrigin $formatterEntryOffset $formatterEntry
        Copy-BytesAtOffset $patchedOrigin $formatterBodyOffset $formatterBody
        Copy-BytesAtOffset `
            $patchedOrigin $visibleWidthEntryOffset $visibleWidthEntry
        Copy-BytesAtOffset `
            $patchedOrigin $visibleWidthBodyOffset $visibleWidthBody
        foreach ($offset in $formatterHookOffsets) {
            $va = [uint32](0x00400000 + $offset)
            Copy-BytesAtOffset $patchedOrigin $offset (
                New-RelativeCall $va $formatterEntryVa)
        }
        foreach ($offset in $directWidthOffsets + $viaEaxWidthOffsets) {
            $va = [uint32](0x00400000 + $offset)
            Copy-BytesAtOffset $patchedOrigin $offset (
                New-RelativeCall $va $visibleWidthEntryVa 7)
        }
        foreach ($offset in $viaEaxWidthOffsets) {
            Copy-BytesAtOffset `
                $patchedOrigin ($offset + 13) $patchedViaEaxCopy
        }
    }
    if ($hoverSourceState) {
        Copy-BytesAtOffset `
            $patchedOrigin $hoverWidthEntryOffset $hoverWidthEntry
        Copy-BytesAtOffset `
            $patchedOrigin $hoverWidthBodyOffset $hoverWidthBody
        foreach ($offset in $hoverWidthOffsets) {
            $va = [uint32](0x00400000 + $offset)
            Copy-BytesAtOffset $patchedOrigin $offset (
                New-RelativeCall $va $hoverWidthEntryVa 7)
        }
    }
    Copy-BytesAtOffset $patchedOrigin $selfHoverWidthOffset $selfHoverWidth
    foreach ($offset in $selfHoverWidthOffsets) {
        $va = [uint32](0x00400000 + $offset)
        Copy-BytesAtOffset $patchedOrigin $offset (
            New-RelativeCall $va $selfHoverWidthVa 7)
    }

    $backupDirectory = Join-Path `
        $ClientPath 'backups\medusa-title-presentation'
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backupPath = Join-Path `
        $backupDirectory 'Origin.exe.pre-title-presentation.bak'
    if (-not (Test-Path -LiteralPath $backupPath)) {
        [IO.File]::WriteAllBytes($backupPath, $origin)
    }

    $stagePath = "$originPath.$([guid]::NewGuid().ToString('N')).stage"
    try {
        [IO.File]::WriteAllBytes($stagePath, $patchedOrigin)
        Move-Item -LiteralPath $stagePath -Destination $originPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $stagePath) {
            Remove-Item -LiteralPath $stagePath -Force
        }
    }
}

Write-Output "Medusa title presentation ready: $originPath"
