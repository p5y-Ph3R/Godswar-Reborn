param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups'
)

# Prerequisite: run PatchClientForgeQuality13.ps1 against the same client.
# G18 shares item/result paths with Q13, while the Sapphire-only ceilings and
# native default-vector initializers remain owned by that earlier patch.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8Bom = [Text.UTF8Encoding]::new($true)
$utf16LeBom = [Text.UnicodeEncoding]::new($false, $true)
$gb2312 = [Text.Encoding]::GetEncoding(936)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$expectedForgeRows = @{ en_us = 611; zh_cn = 550 }

# Maximum progression inputs use base chances Q12=-245 and G17=-370.
# Keeping the Level-5 primary bonus at +32 means +18 is the smallest
# per-Crystal bonus that lets the native maximum of 25 reach 100% at G17:
# -370 + 32 + (25 * 18) = 112, which the forge calculator clamps to 100.
$tier5CrystalProbabilityBonus = 18

function Get-AttributeValue([string]$Element, [string]$Name) {
    $match = [regex]::Match($Element, "(?<=\s)$([regex]::Escape($Name))=`"([^`"]*)`"")
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Set-AttributeValue([string]$Element, [string]$Name, [string]$Value) {
    $pattern = "(?<=\s)$([regex]::Escape($Name))=`"[^`"]*`""
    $match = [regex]::Match($Element, $pattern)
    if (-not $match.Success) { throw "Required attribute '$Name' is missing." }
    return $Element.Substring(0, $match.Index) + "$Name=`"$Value`"" + $Element.Substring($match.Index + $match.Length)
}

function Split-Values([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @($Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
}

function Format-Decimal([decimal]$Value) {
    return $Value.ToString('0.############', $invariant)
}

function Decode-Utf8([string]$Base64) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

function Extend-NumericValues([string]$Value, [int]$TargetCount) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -ge $TargetCount) { return $Value }
    if ($parts.Count -lt 2) { throw "Cannot extrapolate a vector with $($parts.Count) value(s)." }

    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) { $numbers.Add([decimal]::Parse($part, $invariant)) }
    $delta = $numbers[$numbers.Count - 1] - $numbers[$numbers.Count - 2]
    while ($numbers.Count -lt $TargetCount) { $numbers.Add($numbers[$numbers.Count - 1] + $delta) }
    return (($numbers | ForEach-Object { Format-Decimal $_ }) -join ',')
}

function Set-GradeProbabilityVector([string]$Value) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -lt 11) { throw "AppendProyAdd has only $($parts.Count) values; G1-G11 are required." }
    $result = @($parts | Select-Object -First 11) + @('-245', '-270', '-295', '-320', '-345', '-370', '0')
    if ($parts.Count -gt 18) { $result += @($parts | Select-Object -Skip 18) }
    return ($result -join ',')
}

function Set-GradeMoneyVector([string]$Cmoney, [string]$Bmoney, [string]$ItemId) {
    $current = @(Split-Values $Cmoney)
    $qualityMoney = @(Split-Values $Bmoney)
    if ($current.Count -lt 11) { throw "Cmoney for item $ItemId has only $($current.Count) values; G1-G11 are required." }
    if ($qualityMoney.Count -lt 2) { throw "Bmoney for item $ItemId has no economy unit." }

    $unit = [decimal]::Parse($qualityMoney[1], $invariant)
    $tail = @(25, 30, 35, 40, 45, 50) | ForEach-Object { Format-Decimal ($unit * $_) }
    $result = @($current | Select-Object -First 11) + $tail + @('0')
    if ($current.Count -gt 18) { $result += @($current | Select-Object -Skip 18) }
    return ($result -join ',')
}

function Patch-EquipForgeText([string]$Text, [string]$Locale) {
    $state = @{ Rows = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $append = Get-AttributeValue $element 'AppendProyAdd'
        $cmoney = Get-AttributeValue $element 'Cmoney'
        $bmoney = Get-AttributeValue $element 'Bmoney'
        if ($null -eq $append -or $null -eq $cmoney -or $null -eq $bmoney) { return $element }

        $state.Rows++
        $id = Get-AttributeValue $element 'ID'
        $updated = Set-AttributeValue $element 'AppendProyAdd' (Set-GradeProbabilityVector $append)
        $updated = Set-AttributeValue $updated 'Cmoney' (Set-GradeMoneyVector $cmoney $bmoney $id)
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    $expectedRows = $expectedForgeRows[$Locale]
    if ($null -eq $expectedRows -or $state.Rows -ne $expectedRows) {
        throw "Expected $expectedRows EquipForge rows for $Locale; found $($state.Rows)."
    }

    [xml]$document = $patched
    foreach ($node in $document.SelectNodes('//*[@ID and @AppendProyAdd and @Cmoney and @Bmoney]')) {
        $append = @(Split-Values $node.AppendProyAdd)
        $money = @(Split-Values $node.Cmoney)
        if ($append.Count -lt 18 -or ($append[11..17] -join ',') -ne '-245,-270,-295,-320,-345,-370,0') {
            throw "AppendProyAdd G12-G18 validation failed for $Locale item $($node.ID)."
        }
        if ($money.Count -lt 18 -or $money[17] -ne '0') {
            throw "Cmoney G18 validation failed for $Locale item $($node.ID)."
        }
    }

    return [pscustomobject]@{ Text = $patched; Rows = $state.Rows; ChangedRows = $state.Changed }
}

function Upsert-XmlElementAfterId(
    [string]$Text,
    [string]$Id,
    [string]$AnchorId,
    [string]$Element,
    [string]$Label
) {
    $existingPattern = "<[^<>]*\bID=`"$([regex]::Escape($Id))`"[^<>]*/>"
    $existing = [regex]::Matches($Text, $existingPattern)
    if ($existing.Count -gt 1) { throw "Found duplicate $Label rows for ID $Id." }
    if ($existing.Count -eq 1) {
        $match = $existing[0]
        return $Text.Substring(0, $match.Index) + $Element + $Text.Substring($match.Index + $match.Length)
    }

    $anchorPattern = "<[^<>]*\bID=`"$([regex]::Escape($AnchorId))`"[^<>]*/>"
    $anchors = [regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) { throw "Expected one $Label anchor ID $AnchorId; found $($anchors.Count)." }
    $anchor = $anchors[0]
    $afterAnchor = $anchor.Index + $anchor.Length
    $tail = $Text.Substring($afterAnchor)
    $separator = ''
    if ($tail.StartsWith("`r`n") -or $tail.StartsWith("`n")) {
        $newline = if ($tail.StartsWith("`r`n")) { "`r`n" } else { "`n" }
        $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $anchor.Index - 1)) + 1
        $indent = $Text.Substring($lineStart, $anchor.Index - $lineStart)
        if ($indent -notmatch '^[ \t]*$') { $indent = '' }
        $separator = $newline + $indent
    }
    return $Text.Substring(0, $afterAnchor) + $separator + $Element + $tail
}

function Patch-BijouForgeText([string]$Text, [string]$Locale) {
    $emeraldPattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="4223"[^<>]*/>'
    $matches = [regex]::Matches($Text, $emeraldPattern)
    if ($matches.Count -ne 1) { throw "Expected one Emerald 4 rule for $Locale; found $($matches.Count)." }
    $emerald = $matches[0].Value
    if ((Get-AttributeValue $emerald 'MaterialType') -ne '3') { throw "Emerald 4 MaterialType mismatch for $Locale." }
    $patched = $Text.Substring(0, $matches[0].Index) +
        (Set-AttributeValue $emerald 'Round' '10,17') +
        $Text.Substring($matches[0].Index + $matches[0].Length)

    $patched = Upsert-XmlElementAfterId $patched '4215' '4213' '<MaterialBase6 ID="4215" MaterialType="2" MaterialProyAdd="32" Round="8,12"/>' 'Sapphire 5'
    $patched = Upsert-XmlElementAfterId $patched '4225' '4223' '<MaterialAppend6 ID="4225" MaterialType="3" MaterialProyAdd="32" Round="10,17"/>' 'Emerald 5'
    $patched = Upsert-XmlElementAfterId $patched '4234' '4233' "<MaterialOdds5 ID=`"4234`" MaterialType=`"4`" MaterialProyAdd=`"$tier5CrystalProbabilityBonus`"/>" 'Crystal 5'

    [xml]$document = $patched
    $expected = @(
        @{ Id = '4215'; Type = '2'; Bonus = '32'; Round = '8,12' },
        @{ Id = '4223'; Type = '3'; Bonus = '24'; Round = '10,17' },
        @{ Id = '4225'; Type = '3'; Bonus = '32'; Round = '10,17' },
        @{ Id = '4234'; Type = '4'; Bonus = "$tier5CrystalProbabilityBonus"; Round = '' }
    )
    foreach ($entry in $expected) {
        $nodes = @($document.SelectNodes("//*[@ID='$($entry.Id)']"))
        if ($nodes.Count -ne 1 -or $nodes[0].MaterialType -ne $entry.Type -or $nodes[0].MaterialProyAdd -ne $entry.Bonus) {
            throw "Tier-5 Bijou validation failed for $Locale ID $($entry.Id)."
        }
        if ($entry.Round -and $nodes[0].Round -ne $entry.Round) {
            throw "Tier-5 Round validation failed for $Locale ID $($entry.Id)."
        }
    }
    return $patched
}

function Get-ForgeIds([string]$EquipForgeText) {
    [xml]$document = $EquipForgeText
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $document.SelectNodes('//*[@ID]')) {
        if (-not $ids.Add($node.ID)) { throw "Duplicate EquipForge ID $($node.ID)." }
    }
    return ,$ids
}

function Patch-ItemBaseText(
    [string]$Text,
    [Collections.Generic.HashSet[string]]$ForgeIds,
    [string]$Locale
) {
    $state = @{ Rows = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $id = Get-AttributeValue $element 'ID'
        if (-not $ForgeIds.Contains($id)) { return $element }

        $state.Rows++
        $appFraction = Get-AttributeValue $element 'AppFraction'
        if ($null -eq $appFraction) { throw "Forgeable item $id lacks AppFraction for $Locale." }
        $updated = Set-AttributeValue $element 'AppFraction' (Extend-NumericValues $appFraction 18)
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    if ($state.Rows -ne $ForgeIds.Count) {
        throw "ItemBaseAttribute $Locale matched $($state.Rows) of $($ForgeIds.Count) forge IDs."
    }

    $patched = Upsert-XmlElementAfterId $patched '4215' '4214' '<MaterialBase6 ID="4215" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="36,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />' 'Sapphire 5 item'
    $patched = Upsert-XmlElementAfterId $patched '4225' '4224' '<MaterialAppend6 ID="4225" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="72,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />' 'Emerald 5 item'
    $patched = Upsert-XmlElementAfterId $patched '4234' '4233' '<MaterialOdds5 ID="4234" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon4.gwo" Icon="0,0" Random="0" Distribution="0,0" Money="0" Overlap="99" BindType="1" />' 'Crystal 5 item'

    [xml]$document = $patched
    $matched = @($document.SelectNodes('//*[@ID]') | Where-Object { $ForgeIds.Contains($_.ID) })
    if ($matched.Count -ne $ForgeIds.Count) { throw "ItemBaseAttribute validation count mismatch for $Locale." }
    foreach ($node in $matched) {
        if (@(Split-Values $node.AppFraction).Count -lt 18) {
            throw "AppFraction remains shorter than G18 for $Locale item $($node.ID)."
        }
    }
    foreach ($id in @('4215', '4225', '4234')) {
        if (@($document.SelectNodes("//*[@ID='$id']")).Count -ne 1) { throw "Item ID $id validation failed for $Locale." }
    }

    return [pscustomobject]@{ Text = $patched; Rows = $state.Rows; ChangedRows = $state.Changed }
}

function Set-LocalizedLine(
    [string]$Text,
    [string]$Key,
    [string]$Value,
    [string]$AnchorKey
) {
    $line = "$Key`t$Value"
    $existingPattern = "(?m)^$([regex]::Escape($Key))\t[^\r\n]*(?=\r?$)"
    $existing = [regex]::Matches($Text, $existingPattern)
    if ($existing.Count -gt 1) { throw "Duplicate localization key '$Key'." }
    if ($existing.Count -eq 1) {
        $match = $existing[0]
        return $Text.Substring(0, $match.Index) + $line + $Text.Substring($match.Index + $match.Length)
    }

    $anchorPattern = "(?m)^$([regex]::Escape($AnchorKey))\t[^\r\n]*(?=\r?$)"
    $anchors = [regex]::Matches($Text, $anchorPattern)
    if ($anchors.Count -ne 1) { throw "Expected one localization anchor '$AnchorKey' for '$Key'; found $($anchors.Count)." }
    $anchor = $anchors[0]
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $insert = $anchor.Value + $newline + $line
    return $Text.Substring(0, $anchor.Index) + $insert + $Text.Substring($anchor.Index + $anchor.Length)
}

function Patch-LocalizationText([string]$Names, [string]$Descriptions, [string]$Locale) {
    if ($Locale -eq 'en_us') {
        $names = Set-LocalizedLine $Names 'MaterialBase6' 'Level 5 Sapphire' 'MaterialBase5'
        $names = Set-LocalizedLine $names 'MaterialAppend6' 'Level 5 Emerald' 'MaterialAppend5'
        $names = Set-LocalizedLine $names 'MaterialOdds5' 'Level 5 Crystal' 'MaterialOdds4'

        $descriptions = Set-LocalizedLine $Descriptions 'MaterialAppend4' 'Polished emerald with much energy. It can increase equipment''s star level.|cFF39d8b8Can only be used to improve 11-18 star equipment.|cffffffff' 'MaterialAppend4'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialBase6' 'Radiant sapphire with concentrated energy. It raises equipment quality with a greater success bonus.|cFF39d8b8Can only be used on equipment at current quality 8-12.|cffffffff' 'MaterialBase5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialAppend6' 'Radiant emerald with concentrated energy. It raises equipment star level with a greater success bonus.|cFF39d8b8Can only be used on equipment at current grade 10-17.|cffffffff' 'MaterialAppend5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialOdds5' "Brilliant crystal with concentrated energy. Each crystal adds $tier5CrystalProbabilityBonus percentage points to the authoritative forge chance." 'MaterialOdds4'
    }
    else {
        $names = Set-LocalizedLine $Names 'MaterialBase6' (Decode-Utf8 '5LqU57qn6JOd5a6d55+z') 'MaterialBase5'
        $names = Set-LocalizedLine $names 'MaterialAppend6' (Decode-Utf8 '5LqU57qn57u/5a6d55+z') 'MaterialAppend5'
        $names = Set-LocalizedLine $names 'MaterialOdds5' (Decode-Utf8 '5LqU57qn5rC05pm2') 'MaterialOdds4'

        $descriptions = Set-LocalizedLine $Descriptions 'MaterialAppend4' (Decode-Utf8 '57uP6L+H57K+5b+D5omT56Oo55qE5Zub57qn57u/5a6d55+z77yM6IO95aSf5o+Q5Y2H6KOF5aSH55qE5pif57qn44CCfGNGRjM5ZDhiOOWPquiDveaJk+mAoOWNgeS4gOaYn+iHs+WNgeWFq+aYn+eahOijheWkh+OAgnxjZmZmZmZmZmY=') 'MaterialAppend4'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialBase6' (Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn6JOd5a6d55+z77yM6IO95aSf5Lul5pu06auY5oiQ5Yqf546H5o+Q5Y2H6KOF5aSH5ZOB6LSo44CCfGNGRjM5ZDhiOOWPr+eUqOS6juW9k+WJjeWTgei0qOWFq+iHs+WNgeS6jOe6p+eahOijheWkh+OAgnxjZmZmZmZmZmY=') 'MaterialBase5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialAppend6' (Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn57u/5a6d55+z77yM6IO95aSf5Lul5pu06auY5oiQ5Yqf546H5o+Q5Y2H6KOF5aSH5pif57qn44CCfGNGRjM5ZDhiOOWPr+eUqOS6juW9k+WJjeWNgeaYn+iHs+WNgeS4g+aYn+eahOijheWkh+OAgnxjZmZmZmZmZmY=') 'MaterialAppend5'
        $descriptions = Set-LocalizedLine $descriptions 'MaterialOdds5' (Decode-Utf8 '6IO96YeP6auY5bqm5Yed6IGa55qE5LqU57qn5rC05pm277yM5q+P6aKX5Y+v5L2/5pyN5Yqh5Zmo5Yik5a6a55qE5omT6YCg5oiQ5Yqf546H5o+Q6auYMTjkuKrnmb7liIbngrnjgII=') 'MaterialOdds4'
    }

    return [pscustomobject]@{ Names = $names; Descriptions = $descriptions }
}

function Assert-BinaryContext([byte[]]$Bytes, [hashtable]$Site) {
    if ($Site.Offset -lt $Site.Prefix.Count -or
        $Site.Offset + $Site.Suffix.Count -ge $Bytes.Count) {
        throw "Origin.exe site '$($Site.Name)' is outside the file."
    }

    for ($index = 0; $index -lt $Site.Prefix.Count; $index++) {
        if ($Bytes[$Site.Offset - $Site.Prefix.Count + $index] -ne $Site.Prefix[$index]) {
            throw "Origin.exe prefix mismatch at $($Site.Name) (0x$('{0:X}' -f $Site.Offset))."
        }
    }
    for ($index = 0; $index -lt $Site.Suffix.Count; $index++) {
        if ($Bytes[$Site.Offset + 1 + $index] -ne $Site.Suffix[$index]) {
            throw "Origin.exe suffix mismatch at $($Site.Name) (0x$('{0:X}' -f $Site.Offset))."
        }
    }
    if ($Site.Allowed -notcontains $Bytes[$Site.Offset]) {
        throw "Origin.exe byte mismatch at $($Site.Name): got 0x$('{0:X2}' -f $Bytes[$Site.Offset])."
    }
}

$paths = @{}
$results = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $base = Join-Path $ClientRoot "Localization\$locale"
    $paths[$locale] = @{
        Equip = Join-Path $base 'Settings\Sys\EquipForge.xml'
        Bijou = Join-Path $base 'Settings\Sys\BijouForge.xml'
        Item = Join-Path $base 'Settings\Sys\ItemBaseAttribute.xml'
        Names = Join-Path $base 'Text\EquipName.dat'
        Descriptions = Join-Path $base 'Text\EquipDescription.dat'
    }
    foreach ($path in $paths[$locale].Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required client file was not found: $path" }
    }

    $equip = [IO.File]::ReadAllText($paths[$locale].Equip, [Text.Encoding]::UTF8)
    $equipResult = Patch-EquipForgeText $equip $locale
    $bijou = Patch-BijouForgeText ([IO.File]::ReadAllText($paths[$locale].Bijou, [Text.Encoding]::UTF8)) $locale
    $itemResult = Patch-ItemBaseText ([IO.File]::ReadAllText($paths[$locale].Item, [Text.Encoding]::UTF8)) (Get-ForgeIds $equipResult.Text) $locale
    $textEncoding = if ($locale -eq 'en_us') { $utf16LeBom } else { $gb2312 }
    $localized = Patch-LocalizationText `
        ([IO.File]::ReadAllText($paths[$locale].Names, $textEncoding)) `
        ([IO.File]::ReadAllText($paths[$locale].Descriptions, $textEncoding)) `
        $locale
    $results[$locale] = @{
        Equip = $equipResult
        Bijou = $bijou
        Item = $itemResult
        Names = $localized.Names
        Descriptions = $localized.Descriptions
        TextEncoding = $textEncoding
    }
}

$exePath = Join-Path $ClientRoot 'Origin.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "Origin.exe was not found: $exePath" }
$exeBytes = [IO.File]::ReadAllBytes($exePath)
$q13Prerequisites = @{
    0x23A18 = [byte]0x0C
    0x24776 = [byte]0x0D
    0x24981 = [byte]0x0D
    0x160CA2 = [byte]0x0C
}
$q13DefaultVectorOffsets = @(
    0x37202, 0x37217, 0x3722C, 0x37241, 0x37256, 0x3726F, 0x37280,
    0x37295, 0x372AA, 0x372BF, 0x372D6, 0x372ED, 0x37304, 0x37319,
    0x37330, 0x37347, 0x3735C, 0x37371, 0x37388, 0x3739F, 0x373BA,
    0x373CB, 0x373E0, 0x373F5, 0x3740A, 0x3741F, 0x37434
)
foreach ($entry in $q13Prerequisites.GetEnumerator()) {
    if ($entry.Key -ge $exeBytes.Count -or $exeBytes[$entry.Key] -ne $entry.Value) {
        throw "Q13 client prerequisite is missing at Origin.exe offset 0x$('{0:X}' -f $entry.Key); run PatchClientForgeQuality13.ps1 first."
    }
}
foreach ($offset in $q13DefaultVectorOffsets) {
    if ($offset -ge $exeBytes.Count -or $exeBytes[$offset] -ne 0x0D) {
        throw "Q13 default-vector prerequisite is missing at Origin.exe offset 0x$('{0:X}' -f $offset); run PatchClientForgeQuality13.ps1 first."
    }
}
$binarySites = @(
    # Cross-axis Q13 acceptance. Sapphire-specific Q13 ceilings remain unchanged.
    @{ Name = 'shared_success_quality_q13'; Offset = 0x2459C; Prefix = [byte[]](0x80, 0xF9); Allowed = [byte[]](0x0A, 0x0C, 0x0D); Desired = [byte]0x0D; Suffix = [byte[]](0x0F, 0x8F) },
    @{ Name = 'forge_ui_main_quality_q13'; Offset = 0x15DEC4; Prefix = [byte[]](0x3C); Allowed = [byte[]](0x0B, 0x0D, 0x0E); Desired = [byte]0x0E; Suffix = [byte[]](0x0F, 0x8D) },
    @{ Name = 'forge_ui_alt_quality_q13'; Offset = 0x15E818; Prefix = [byte[]](0x3C); Allowed = [byte[]](0x0B, 0x0D, 0x0E); Desired = [byte]0x0E; Suffix = [byte[]](0x0F, 0x8D) },

    # Emerald current-grade gates use G17; shared result gates accept the G18 result.
    @{ Name = 'emerald_preflight_current_g17'; Offset = 0x23A24; Prefix = [byte[]](0x80, 0x7F, 0x49); Allowed = [byte[]](0x0B, 0x11); Desired = [byte]0x11; Suffix = [byte[]](0xBD) },
    @{ Name = 'shared_success_grade_g18'; Offset = 0x245B0; Prefix = [byte[]](0x80, 0xF9); Allowed = [byte[]](0x0C, 0x12); Desired = [byte]0x12; Suffix = [byte[]](0x0F, 0x8F) },
    @{ Name = 'generic_result_grade_g18'; Offset = 0x24781; Prefix = [byte[]](0x3C); Allowed = [byte[]](0x0C, 0x12); Desired = [byte]0x12; Suffix = [byte[]](0x7F, 0x19) },
    @{ Name = 'forge_ui_emerald_current_g17'; Offset = 0x160CAF; Prefix = [byte[]](0x80, 0x7B, 0x49); Allowed = [byte[]](0x0B, 0x11); Desired = [byte]0x11; Suffix = [byte[]](0x7F, 0x04) }
)
$binaryChanges = 0
foreach ($site in $binarySites) {
    Assert-BinaryContext $exeBytes $site
    if ($exeBytes[$site.Offset] -ne $site.Desired) {
        $exeBytes[$site.Offset] = $site.Desired
        $binaryChanges++
    }
}

$changedPaths = [Collections.Generic.List[string]]::new()
foreach ($locale in @('en_us', 'zh_cn')) {
    $result = $results[$locale]
    if ([IO.File]::ReadAllText($paths[$locale].Equip, [Text.Encoding]::UTF8) -cne $result.Equip.Text) { $changedPaths.Add($paths[$locale].Equip) }
    if ([IO.File]::ReadAllText($paths[$locale].Bijou, [Text.Encoding]::UTF8) -cne $result.Bijou) { $changedPaths.Add($paths[$locale].Bijou) }
    if ([IO.File]::ReadAllText($paths[$locale].Item, [Text.Encoding]::UTF8) -cne $result.Item.Text) { $changedPaths.Add($paths[$locale].Item) }
    if ([IO.File]::ReadAllText($paths[$locale].Names, $result.TextEncoding) -cne $result.Names) { $changedPaths.Add($paths[$locale].Names) }
    if ([IO.File]::ReadAllText($paths[$locale].Descriptions, $result.TextEncoding) -cne $result.Descriptions) { $changedPaths.Add($paths[$locale].Descriptions) }
}
if ($binaryChanges -gt 0) { $changedPaths.Add($exePath) }
$changedPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in $changedPaths) { [void]$changedPathSet.Add([IO.Path]::GetFullPath($path)) }

$backupPath = $null
if ($changedPaths.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot ("client-forge-g18-tier5-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $backupPath | Out-Null
    foreach ($path in $changedPaths) {
        $relative = Get-ClientRelativePath $ClientRoot $path
        $destination = Join-Path $backupPath $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $path -Destination $destination
    }

    foreach ($locale in @('en_us', 'zh_cn')) {
        $result = $results[$locale]
        if ($changedPathSet.Contains([IO.Path]::GetFullPath($paths[$locale].Equip))) {
            [IO.File]::WriteAllText($paths[$locale].Equip, $result.Equip.Text, $utf8Bom)
        }
        if ($changedPathSet.Contains([IO.Path]::GetFullPath($paths[$locale].Bijou))) {
            [IO.File]::WriteAllText($paths[$locale].Bijou, $result.Bijou, $utf8Bom)
        }
        if ($changedPathSet.Contains([IO.Path]::GetFullPath($paths[$locale].Item))) {
            [IO.File]::WriteAllText($paths[$locale].Item, $result.Item.Text, $utf8Bom)
        }
        if ($changedPathSet.Contains([IO.Path]::GetFullPath($paths[$locale].Names))) {
            [IO.File]::WriteAllText($paths[$locale].Names, $result.Names, $result.TextEncoding)
        }
        if ($changedPathSet.Contains([IO.Path]::GetFullPath($paths[$locale].Descriptions))) {
            [IO.File]::WriteAllText($paths[$locale].Descriptions, $result.Descriptions, $result.TextEncoding)
        }
    }
    if ($binaryChanges -gt 0) { [IO.File]::WriteAllBytes($exePath, $exeBytes) }
}

foreach ($locale in @('en_us', 'zh_cn')) {
    $equipText = [IO.File]::ReadAllText($paths[$locale].Equip, [Text.Encoding]::UTF8)
    $equip = Patch-EquipForgeText $equipText $locale
    if ($equip.Text -cne $equipText) { throw "EquipForge post-write validation was not idempotent for $locale." }
    $bijouText = [IO.File]::ReadAllText($paths[$locale].Bijou, [Text.Encoding]::UTF8)
    if ((Patch-BijouForgeText $bijouText $locale) -cne $bijouText) {
        throw "BijouForge post-write validation was not idempotent for $locale."
    }
    $itemText = [IO.File]::ReadAllText($paths[$locale].Item, [Text.Encoding]::UTF8)
    $item = Patch-ItemBaseText $itemText (Get-ForgeIds $equip.Text) $locale
    if ($item.Text -cne $itemText) { throw "ItemBaseAttribute post-write validation was not idempotent for $locale." }
    $encoding = $results[$locale].TextEncoding
    $nameText = [IO.File]::ReadAllText($paths[$locale].Names, $encoding)
    $descriptionText = [IO.File]::ReadAllText($paths[$locale].Descriptions, $encoding)
    $localized = Patch-LocalizationText $nameText $descriptionText $locale
    if ($localized.Names -cne $nameText -or $localized.Descriptions -cne $descriptionText) {
        throw "Localization post-write validation was not idempotent for $locale."
    }
    foreach ($key in @('MaterialBase6', 'MaterialAppend6', 'MaterialOdds5')) {
        if ([regex]::Matches($nameText, "(?m)^$key\t[^\r\n]*(?=\r?$)").Count -ne 1 -or
            [regex]::Matches($descriptionText, "(?m)^$key\t[^\r\n]*(?=\r?$)").Count -ne 1) {
            throw "Localization key '$key' validation failed for $locale."
        }
    }
}
$writtenBytes = [IO.File]::ReadAllBytes($exePath)
foreach ($site in $binarySites) {
    Assert-BinaryContext $writtenBytes $site
    if ($writtenBytes[$site.Offset] -ne $site.Desired) {
        throw "Origin.exe post-write validation failed at $($site.Name)."
    }
}

[pscustomobject]@{
    ChangedFiles = $changedPaths.Count
    BackupPath = $backupPath
    EnForgeRows = $results['en_us'].Equip.Rows
    ZhForgeRows = $results['zh_cn'].Equip.Rows
    EnItemRows = $results['en_us'].Item.Rows
    ZhItemRows = $results['zh_cn'].Item.Rows
    Tier5Ids = '4215,4225,4234'
    MaximumGrade = 18
    BinaryBytesChanged = $binaryChanges
    OriginSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash
}
