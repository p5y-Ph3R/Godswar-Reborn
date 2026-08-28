[CmdletBinding()]
param(
    [string]$ClientPath = 'C:\Godswar Origin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Convert-HexBytes {
    param([string]$Hex)

    $compact = $Hex -replace '[^0-9A-Fa-f]', ''
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

    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Get-RelativeTarget {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [uint32]$InstructionVa
    )

    Assert-True ($Bytes[$Offset] -eq 0xE8 -or
        $Bytes[$Offset] -eq 0xE9) "relative branch opcode at 0x$('{0:X}' -f $Offset)"
    $relative = [BitConverter]::ToInt32($Bytes, $Offset + 1)
    return [uint32]([int64]$InstructionVa + 5 + $relative)
}

function Add-AllowedRange {
    param(
        [bool[]]$Allowed,
        [int]$Offset,
        [int]$Length
    )

    for ($index = 0; $index -lt $Length; $index++) {
        $Allowed[$Offset + $index] = $true
    }
}

$patcher = Join-Path $PSScriptRoot 'PatchClientMedusaTitles.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = [IO.Path]::GetFullPath((Join-Path $tempRoot (
            'reborn-medusa-title-' + [guid]::NewGuid().ToString('N'))))
if (-not $fixture.StartsWith(
        $tempRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary title fixture escaped the system temporary directory.'
}

$formatterHooks = @(0x210C0, 0x21185, 0x211AD)
$directWidths = @(0xA3E08, 0xA3FB5)
$viaEaxWidths = @(
    0xA3E94, 0xA4041, 0xA4140, 0xA41C3, 0xA42D0, 0xA4353,
    0xA4451, 0xA44D4, 0xA45F4, 0xA467C, 0xA4780, 0xA4803,
    0xA4901, 0xA4984, 0xA4A90, 0xA4B13, 0xA4C11, 0xA4C94)
$hoverWidths = @(
    0x6D285, 0x6D322, 0x6D3A9, 0x6D440, 0x6D4E3, 0x6D57A,
    0x6D603, 0x6D698, 0x6D746, 0x6D7E2, 0x6D873, 0x6D908,
    0x6D993, 0x6DA2A)
$selfHoverWidths = @(0x6CD43, 0x6CE26)
$caves = @(
    @{ Offset = 0x20921; Va = [uint32]0x00420921 },
    @{ Offset = 0x20991; Va = [uint32]0x00420991 },
    @{ Offset = 0x21BD1; Va = [uint32]0x00421BD1 },
    @{ Offset = 0x21C41; Va = [uint32]0x00421C41 },
    @{ Offset = 0x21CE1; Va = [uint32]0x00421CE1 },
    @{ Offset = 0x22111; Va = [uint32]0x00422111 },
    @{ Offset = 0x22121; Va = [uint32]0x00422121 })
$expectedRows = @{
    5009 = @{ Color = 'A335EE'; English = 'Medusa Executioners' }
    5010 = @{ Color = 'A335EE'; English = 'Medusa Slayers' }
    5011 = @{ Color = 'A335EE'; English = 'Medusa Challengers' }
    5152 = @{ Color = 'DC143C'; English = 'Heir of Perseus' }
    5153 = @{ Color = 'FF8000'; English = 'Bane of the Three Sisters' }
    5154 = @{ Color = 'FF8000'; English = 'Gorgon Breaker' }
}
$expectedInfoRows = @{
    en_us = @{
        5009 = 'Enhanced: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +1%. Only the strongest Medusa title bonus applies.'
        5010 = 'Enhanced: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +2%. Only the strongest Medusa title bonus applies.'
        5011 = 'Enhanced: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +3%. Only the strongest Medusa title bonus applies.'
        5152 = 'Mythic: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +6%. Only the strongest Medusa title bonus applies.'
        5153 = 'Mythic: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +5%. Only the strongest Medusa title bonus applies.'
        5154 = 'Mythic: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +4%. Only the strongest Medusa title bonus applies.'
    }
    zh_cn = @{
        5009 = 'Enhanced: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +1%. Only the strongest Medusa title bonus applies.'
        5010 = 'Enhanced: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +2%. Only the strongest Medusa title bonus applies.'
        5011 = 'Enhanced: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +3%. Only the strongest Medusa title bonus applies.'
        5152 = 'Mythic: 3,000 points within 10 minutes. Permanent attributes: Physical/Magical Attack and Defense +6%. Only the strongest Medusa title bonus applies.'
        5153 = 'Mythic: 3,000 points within 15 minutes. Permanent attributes: Physical/Magical Attack and Defense +5%. Only the strongest Medusa title bonus applies.'
        5154 = 'Mythic: 3,000 points within 20 minutes. Permanent attributes: Physical/Magical Attack and Defense +4%. Only the strongest Medusa title bonus applies.'
    }
}
$encoding = [Text.UnicodeEncoding]::new($false, $true, $true)

try {
    [void][IO.Directory]::CreateDirectory($fixture)
    $sourceOrigin = Join-Path $ClientPath 'Origin.exe'
    [byte[]]$candidate = [IO.File]::ReadAllBytes($sourceOrigin)
    $emptyCave = [byte[]](0xCC) * 15
    if (-not (Test-BytesAtOffset $candidate 0x20921 $emptyCave)) {
        $sourceOrigin = Join-Path $ClientPath (
            'backups\medusa-title-presentation\' +
            'Origin.exe.pre-title-presentation.bak')
    }
    Assert-True (Test-Path -LiteralPath $sourceOrigin -PathType Leaf) `
        'a pre-title-presentation Origin.exe is available'
    Copy-Item -LiteralPath $sourceOrigin -Destination (
        Join-Path $fixture 'Origin.exe')

    foreach ($relative in @(
            'Localization\en_us\Text\DesigName.dat',
            'Localization\en_us\Text\DesigInfo.dat',
            'Localization\zh_cn\Text\DesigName.dat',
            'Localization\zh_cn\Text\DesigInfo.dat')) {
        $source = Join-Path $ClientPath $relative
        $destination = Join-Path $fixture $relative
        [void][IO.Directory]::CreateDirectory((Split-Path $destination))
        Copy-Item -LiteralPath $source -Destination $destination
    }

    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $fixture "Localization\$locale\Text\DesigName.dat"
        [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
        $text = $encoding.GetString($bytes, 2, $bytes.Length - 2)
        foreach ($id in $expectedRows.Keys) {
            $text = [regex]::Replace(
                $text,
                "(?m)^($id\t\|c[0-9A-Fa-f]{8})\[([^\r\n]*)\]" +
                    '(\|cFFFFFFFF)(?=\r?$)',
                '$1$2$3')
        }
        [IO.File]::WriteAllText($path, $text, $encoding)

        $infoPath = Join-Path $fixture `
            "Localization\$locale\Text\DesigInfo.dat"
        [byte[]]$infoBytes = [IO.File]::ReadAllBytes($infoPath)
        $infoText = $encoding.GetString(
            $infoBytes,
            2,
            $infoBytes.Length - 2)
        foreach ($id in $expectedInfoRows[$locale].Keys) {
            $infoText = [regex]::Replace(
                $infoText,
                "(?m)^$id\t[^\r\n]*(?=\r?$)",
                "$id`tLegacy Medusa title description.")
        }
        [IO.File]::WriteAllText($infoPath, $infoText, $encoding)
    }

    $originPath = Join-Path $fixture 'Origin.exe'
    [byte[]]$before = [IO.File]::ReadAllBytes($originPath)
    & $patcher -ClientPath $fixture | Out-Null
    [byte[]]$after = [IO.File]::ReadAllBytes($originPath)

    Assert-True ($after.Length -eq $before.Length) 'binary length is stable'
    [bool[]]$allowed = [bool[]]::new($after.Length)
    foreach ($cave in $caves) {
        Add-AllowedRange $allowed $cave.Offset 15
    }
    foreach ($offset in $formatterHooks) {
        Add-AllowedRange $allowed $offset 5
    }
    foreach ($offset in $directWidths + $viaEaxWidths) {
        Add-AllowedRange $allowed $offset 7
    }
    foreach ($offset in $hoverWidths + $selfHoverWidths) {
        Add-AllowedRange $allowed $offset 7
    }
    foreach ($offset in $viaEaxWidths) {
        Add-AllowedRange $allowed ($offset + 13) 2
    }
    for ($index = 0; $index -lt $after.Length; $index++) {
        if (-not $allowed[$index] -and $before[$index] -ne $after[$index]) {
            throw "Unexpected Origin.exe change at 0x$('{0:X}' -f $index)."
        }
    }

    Assert-True ((Get-RelativeTarget $after 0x2092A 0x0042092A) -eq
        0x00420991) 'colored titles select the unwrapped formatter body'
    Assert-True ((Get-RelativeTarget $after 0x20999 0x00420999) -eq
        0x00402DF0) 'formatter helper tail-calls the stock formatter'
    Assert-True (Test-BytesAtOffset $after 0x20991 (
            Convert-HexBytes 'C7 44 24 04 04 22 95 00')) `
        'colored formatter selects the existing percent-s format'
    Assert-True (Test-BytesAtOffset $after 0x21C41 (
            Convert-HexBytes '83 E9 14 C1 E1 03 C3')) `
        'centering removes twenty hidden markup bytes before scaling'
    Assert-True (Test-BytesAtOffset $after 0x22111 (
            Convert-HexBytes '56 57 89 F0 89 FE 8B B8 18 01 00 00 EB 02')) `
        'hover adapter supplies the exact player and raw title length'
    Assert-True ((Get-RelativeTarget $after 0x22121 0x00422121) -eq
        0x00421BD1) 'hover centering reuses the visible-width helper'
    Assert-True (Test-BytesAtOffset $after 0x22126 (
            Convert-HexBytes '89 C8 5F 5E C3')) `
        'hover adapter restores registers and returns the visible width'
    Assert-True (Test-BytesAtOffset $after 0x21CE1 (
            Convert-HexBytes '57 89 DF E8')) `
        'self-hover adapter supplies the local player to visible-width logic'
    Assert-True ((Get-RelativeTarget $after 0x21CE4 0x00421CE4) -eq
        0x00421BD1) 'self-hover centering reuses the visible-width helper'
    Assert-True (Test-BytesAtOffset $after 0x21CE9 (
            Convert-HexBytes '5F C3')) `
        'self-hover adapter restores the nonvolatile player register'

    foreach ($offset in $formatterHooks) {
        Assert-True ((Get-RelativeTarget $after $offset (
                    [uint32](0x00400000 + $offset))) -eq 0x00420921) `
            "formatter hook 0x$('{0:X}' -f $offset)"
    }
    foreach ($offset in $directWidths + $viaEaxWidths) {
        Assert-True ((Get-RelativeTarget $after $offset (
                    [uint32](0x00400000 + $offset))) -eq 0x00421BD1) `
            "width hook 0x$('{0:X}' -f $offset)"
    }
    foreach ($offset in $viaEaxWidths) {
        Assert-True (Test-BytesAtOffset $after ($offset + 13) (
                Convert-HexBytes '90 90')) `
            "stale width copy removed at 0x$('{0:X}' -f ($offset + 13))"
    }
    foreach ($offset in $hoverWidths) {
        Assert-True ((Get-RelativeTarget $after $offset (
                    [uint32](0x00400000 + $offset))) -eq 0x00422111) `
            "hover width hook 0x$('{0:X}' -f $offset)"
    }
    foreach ($offset in $selfHoverWidths) {
        Assert-True ((Get-RelativeTarget $after $offset (
                    [uint32](0x00400000 + $offset))) -eq 0x00421CE1) `
            "self-hover width hook 0x$('{0:X}' -f $offset)"
    }

    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $fixture "Localization\$locale\Text\DesigName.dat"
        [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
        $text = $encoding.GetString($bytes, 2, $bytes.Length - 2)
        foreach ($id in $expectedRows.Keys) {
            $row = [regex]::Match($text, "(?m)^$id\t([^\r\n]+)(?=\r?$)")
            Assert-True $row.Success "title $id exists in $locale"
            $color = $expectedRows[$id].Color
            Assert-True ($row.Groups[1].Value -match
                "^\|cff$color\[[^\[\]]+\]\|cFFFFFFFF$") `
                "title $id colors both brackets in $locale"
            if ($locale -eq 'en_us') {
                Assert-True ($row.Groups[1].Value -eq
                    "|cff$color[$($expectedRows[$id].English)]|cFFFFFFFF") `
                    "English title $id retains its authored name"
            }
        }

        $infoPath = Join-Path $fixture `
            "Localization\$locale\Text\DesigInfo.dat"
        [byte[]]$infoBytes = [IO.File]::ReadAllBytes($infoPath)
        $infoText = $encoding.GetString(
            $infoBytes,
            2,
            $infoBytes.Length - 2)
        foreach ($id in $expectedInfoRows[$locale].Keys) {
            $row = [regex]::Match(
                $infoText,
                "(?m)^$id\t([^\r\n]+)(?=\r?$)")
            Assert-True $row.Success "title $id info exists in $locale"
            Assert-True ($row.Groups[1].Value -eq
                $expectedInfoRows[$locale][$id]) `
                "title $id exposes its permanent attributes in $locale"
        }
    }

    $firstHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $originPath).Hash
    & $patcher -ClientPath $fixture | Out-Null
    & $patcher -ClientPath $fixture -ValidateOnly | Out-Null
    $secondHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $originPath).Hash
    Assert-True ($firstHash -eq $secondHash) 'apply is idempotent'

    [byte[]]$partial = [IO.File]::ReadAllBytes($originPath)
    $partial[0x210C0] = 0x90
    [IO.File]::WriteAllBytes($originPath, $partial)
    $refused = $false
    try {
        & $patcher -ClientPath $fixture -ValidateOnly | Out-Null
    }
    catch {
        $refused = $true
    }
    Assert-True $refused 'partial binary state is rejected'

    Write-Output 'Medusa title color and centering patch checks passed.'
}
finally {
    if (Test-Path -LiteralPath $fixture) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
