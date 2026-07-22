param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8Bom = [Text.UTF8Encoding]::new($true)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$expectedForgeRows = @{ en_us = 611; zh_cn = 550 }
$expectedWeaponTargets = @{ en_us = 83; zh_cn = 80 }
$expectedNonWeaponTargets = @{ en_us = 526; zh_cn = 468 }
$expectedBodyTargets = @{ en_us = 68; zh_cn = 60 }
$protectedForgeIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal
)
@('1499', '2190') | ForEach-Object { [void]$protectedForgeIds.Add($_) }
$supportedNonWeaponTypes = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
@(
    'head', 'amulet', 'glove', 'armor', 'cloth', 'cuff',
    'girdle', 'shoes', 'leggins', 'ring', 'shield'
) | ForEach-Object { [void]$supportedNonWeaponTypes.Add($_) }

# The native Q1-Q10 and G1-G12 prefixes are kept exactly. These profiles only
# define the extended part of ordinary equipment score progression.
$ordinaryQualityProfile = @(
    0, 8, 18, 28, 40, 54, 74, 100, 140, 200,
    230, 260, 295, 330, 370, 410, 455, 500, 550, 600
)
$ordinaryGradeProfile = @(
    10, 13, 16, 20, 24, 28, 32, 40, 50, 60, 80, 100,
    116, 133, 151, 170, 190, 211, 233, 256, 280, 305, 332,
    365, 400
)

# A weapon needs five append attributes at Q20/G25 to meet WR10's score 8000.
# This is the same authored profile used by the working Champion weapon.
$weaponBaseFraction = @(
    0, 8, 18, 28, 40, 54, 74, 100, 140, 200,
    260, 340, 440, 560, 700, 860, 1040, 1240, 1460, 1700
)
$weaponAppFraction = @(
    10, 13, 16, 20, 24, 28, 32, 40, 50, 60, 80, 100,
    130, 170, 220, 280, 350, 430, 520, 620, 730, 850, 980,
    1120, 1270
)
$weaponRankThresholds = @(
    40, 100, 180, 240, 300, 460, 600, 1200, 4000, 8000
) + @(1..15 | ForEach-Object { -1 })
$physicalWeaponEffects = @(
    1, 2, 3, 4, 5, 5, 5, 6, 8, 9
) + @(1..15 | ForEach-Object { 5 })
$class2WeaponEffects = @(
    201, 202, 203, 204, 205, 205, 205, 206, 208, 209
) + @(1..15 | ForEach-Object { 205 })
$class3WeaponEffects = @(
    51, 52, 53, 54, 55, 55, 55, 56, 58, 59
) + @(1..15 | ForEach-Object { 55 })
$armorRankThresholds = @(
    330, 475, 750, 950, 1350, 1720, 2225, 3860, 5250,
    8000, 12000, 17000, 22000, 25300, -1
)
$armorRankEffects = @(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 14)

$expectedNonWeaponAnchors = @{
    head = @(200, 100)
    amulet = @(170, 85)
    glove = @(200, 100)
    armor = @(300, 150)
    cloth = @(300, 150)
    cuff = @(170, 85)
    girdle = @(200, 100)
    shoes = @(200, 100)
    leggins = @(170, 85)
    ring = @(170, 85)
    shield = @(50, 25)
}

function Join-Integers([int[]]$Values) {
    return (($Values | ForEach-Object { $_.ToString($invariant) }) -join ',')
}

function Split-Values([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() }
    )
}

function Get-AttributeValue([string]$Element, [string]$Name) {
    $match = [regex]::Match(
        $Element,
        ('(?<=\s){0}="([^"]*)"' -f [regex]::Escape($Name))
    )
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Set-AttributeValue([string]$Element, [string]$Name, [string]$Value) {
    $pattern = '(?<=\s){0}="[^"]*"' -f [regex]::Escape($Name)
    $match = [regex]::Match($Element, $pattern)
    if (-not $match.Success) {
        throw "Required attribute '$Name' is missing."
    }
    $replacement = $Name + '="' + $Value + '"'
    return $Element.Substring(0, $match.Index) + $replacement +
        $Element.Substring($match.Index + $match.Length)
}

function Test-Prefix(
    [string[]]$Actual,
    [int[]]$Expected,
    [int]$Count
) {
    if ($Actual.Count -lt $Count -or $Expected.Count -lt $Count) {
        return $false
    }
    for ($index = 0; $index -lt $Count; $index++) {
        if ($Actual[$index] -cne $Expected[$index].ToString($invariant)) {
            return $false
        }
    }
    return $true
}

function Set-ScaledTail(
    [string]$Value,
    [int[]]$Profile,
    [int]$AnchorIndex,
    [string]$Label
) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -lt $Profile.Count) {
        throw "$Label has only $($parts.Count) entries; expected at least $($Profile.Count)."
    }
    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) {
        $numbers.Add([decimal]::Parse($part, $invariant))
    }
    $anchorValue = $numbers[$AnchorIndex]
    $anchorProfile = [decimal]$Profile[$AnchorIndex]
    if ($anchorValue -le 0 -or $anchorProfile -le 0) {
        throw "$Label has an invalid score anchor."
    }
    $result = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $Profile.Count; $index++) {
        if ($index -le $AnchorIndex) {
            $result.Add($parts[$index])
            continue
        }
        $scaled = $anchorValue * ([decimal]$Profile[$index] / $anchorProfile)
        $rounded = [Math]::Round($scaled, 0, [MidpointRounding]::AwayFromZero)
        $result.Add(([int]$rounded).ToString($invariant))
    }
    return ($result -join ',')
}

function Get-ForgeIds([string]$Text, [string]$Locale) {
    [xml]$document = $Text
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $document.SelectNodes('//*[@ID]')) {
        if (-not $ids.Add($node.GetAttribute('ID'))) {
            throw "Duplicate EquipForge ID $($node.GetAttribute('ID')) for $Locale."
        }
    }
    if ($ids.Count -ne $expectedForgeRows[$Locale]) {
        throw "Expected $($expectedForgeRows[$Locale]) EquipForge IDs for $Locale; found $($ids.Count)."
    }
    if ($ids.Contains('1111')) {
        throw "Non-forgeable GM weapon 1111 unexpectedly appears in $Locale EquipForge."
    }
    foreach ($protectedId in $protectedForgeIds) {
        if (-not $ids.Contains($protectedId)) {
            throw "Protected custom GM equipment $protectedId is missing from $Locale EquipForge."
        }
    }
    return ,$ids
}

function Get-WeaponEffects([string]$ClassId, [string]$ItemId, [string]$Locale) {
    switch ($ClassId) {
        '0' { return Join-Integers $physicalWeaponEffects }
        '1' { return Join-Integers $physicalWeaponEffects }
        '2' { return Join-Integers $class2WeaponEffects }
        '3' { return Join-Integers $class3WeaponEffects }
        default {
            throw "Forgeable ranked weapon $ItemId has unsupported class '$ClassId' for $Locale."
        }
    }
}

function Get-AllowedChangedAttributes(
    [System.Xml.XmlElement]$Node,
    [Collections.Generic.HashSet[string]]$ForgeIds
) {
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $id = $Node.GetAttribute('ID')
    if (-not $ForgeIds.Contains($id) -or $protectedForgeIds.Contains($id)) {
        return ,$allowed
    }
    [void]$allowed.Add('BaseFraction')
    [void]$allowed.Add('AppFraction')
    $type = $Node.GetAttribute('Type')
    if ($type -eq 'weapon') {
        [void]$allowed.Add('ArmEffFraction')
        [void]$allowed.Add('ArmEff')
    }
    elseif ($type -eq 'armor' -or $type -eq 'cloth') {
        [void]$allowed.Add('DefendFraction')
        [void]$allowed.Add('DefendEff')
    }
    return ,$allowed
}

function Assert-OnlyAllowedAttributeChanges(
    [xml]$Before,
    [xml]$After,
    [Collections.Generic.HashSet[string]]$ForgeIds,
    [string]$Locale
) {
    # ItemBaseAttribute legitimately reuses a few non-equipment IDs. Compare
    # nodes in document order instead of assuming every unrelated ID is unique.
    $beforeNodes = @($Before.SelectNodes('//*[@ID]'))
    $afterNodes = @($After.SelectNodes('//*[@ID]'))
    if ($beforeNodes.Count -ne $afterNodes.Count) {
        throw "ItemBaseAttribute node count changed for $Locale."
    }
    for ($index = 0; $index -lt $beforeNodes.Count; $index++) {
        $beforeNode = [System.Xml.XmlElement]$beforeNodes[$index]
        $afterNode = [System.Xml.XmlElement]$afterNodes[$index]
        $id = $beforeNode.GetAttribute('ID')
        if ($id -cne $afterNode.GetAttribute('ID') -or
            $beforeNode.Name -cne $afterNode.Name -or
            $beforeNode.Attributes.Count -ne $afterNode.Attributes.Count) {
            throw "ItemBaseAttribute structure changed for $Locale item $id."
        }
        $allowed = Get-AllowedChangedAttributes $beforeNode $ForgeIds
        foreach ($attribute in $beforeNode.Attributes) {
            $name = $attribute.Name
            if (-not $afterNode.HasAttribute($name)) {
                throw "Attribute $name disappeared from $Locale item $id."
            }
            if (-not $allowed.Contains($name) -and
                $attribute.Value -cne $afterNode.GetAttribute($name)) {
                throw "Unrelated attribute $name changed for $Locale item $id."
            }
        }
        foreach ($attribute in $afterNode.Attributes) {
            if (-not $beforeNode.HasAttribute($attribute.Name)) {
                throw "Attribute $($attribute.Name) was added to $Locale item $id."
            }
        }
    }
}

function Get-MaxItemScore([System.Xml.XmlElement]$Node, [int]$AttributeCount) {
    $base = @(Split-Values $Node.GetAttribute('BaseFraction'))
    $append = @(Split-Values $Node.GetAttribute('AppFraction'))
    if ($base.Count -ne 20 -or $append.Count -ne 25) {
        throw "Item $($Node.GetAttribute('ID')) does not expose exact Q20/G25 score vectors."
    }
    return [int]::Parse($base[19], $invariant) +
        ([int]::Parse($append[24], $invariant) * $AttributeCount)
}

function Assert-DeepRankValidation(
    [xml]$Document,
    [Collections.Generic.HashSet[string]]$ForgeIds,
    [string]$Locale,
    [int]$WeaponTargets,
    [int]$NonWeaponTargets,
    [int]$BodyTargets
) {
    $weaponThresholdText = Join-Integers $weaponRankThresholds
    $armorThresholdText = Join-Integers $armorRankThresholds
    $armorEffectText = Join-Integers $armorRankEffects
    $weaponBaseText = Join-Integers $weaponBaseFraction
    $weaponAppText = Join-Integers $weaponAppFraction
    $seenForgeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenWeaponTargets = 0
    $seenNonWeaponTargets = 0
    $seenBodyTargets = 0

    foreach ($node in $Document.SelectNodes('//*[@ID]')) {
        $id = $node.GetAttribute('ID')
        if (-not $ForgeIds.Contains($id)) { continue }
        if (-not $seenForgeIds.Add($id)) {
            throw "Duplicate forgeable ItemBaseAttribute ID $id for $Locale."
        }
        if ($protectedForgeIds.Contains($id)) { continue }
        $type = $node.GetAttribute('Type')
        if ($type -eq 'weapon') {
            $seenWeaponTargets++
            if ($node.GetAttribute('BaseFraction') -cne $weaponBaseText -or
                $node.GetAttribute('AppFraction') -cne $weaponAppText) {
                throw "Canonical weapon score validation failed for $Locale item $id."
            }
            if ($node.GetAttribute('ArmEffFraction') -cne $weaponThresholdText -or
                $node.GetAttribute('ArmEff') -cne
                    (Get-WeaponEffects $node.GetAttribute('Class') $id $Locale)) {
                throw "Canonical weapon rank validation failed for $Locale item $id."
            }
            if ((Get-MaxItemScore $node 5) -ne 8050 -or
                (Get-MaxItemScore $node 4) -ne 6780) {
                throw "Weapon max-score validation failed for $Locale item $id."
            }
            continue
        }

        $seenNonWeaponTargets++
        if (-not $supportedNonWeaponTypes.Contains($type)) {
            throw "Unsupported forgeable nonweapon type '$type' for $Locale item $id."
        }
        $base = @(Split-Values $node.GetAttribute('BaseFraction'))
        $append = @(Split-Values $node.GetAttribute('AppFraction'))
        if ($base.Count -ne 20 -or $append.Count -ne 25) {
            throw "Nonweapon score vector length validation failed for $Locale item $id."
        }
        $q10 = [int]::Parse($base[9], $invariant)
        $g12 = [int]::Parse($append[11], $invariant)
        if ([int]::Parse($base[19], $invariant) -ne ($q10 * 3) -or
            [int]::Parse($append[24], $invariant) -ne ($g12 * 4)) {
            throw "Scaled rank-score tail validation failed for $Locale item $id."
        }
        if ($type -eq 'armor' -or $type -eq 'cloth') {
            $seenBodyTargets++
            if ($node.GetAttribute('DefendFraction') -cne $armorThresholdText -or
                $node.GetAttribute('DefendEff') -cne $armorEffectText) {
                throw "Canonical armor-rank validation failed for $Locale item $id."
            }
        }
    }

    if ($seenForgeIds.Count -ne $ForgeIds.Count -or
        $seenWeaponTargets -ne $WeaponTargets -or
        $seenNonWeaponTargets -ne $NonWeaponTargets -or
        $seenBodyTargets -ne $BodyTargets) {
        throw "Deep target-count validation failed for $Locale."
    }

    $starterWeaponIds = @('1000', '1400', '1700', '1800')
    foreach ($id in $starterWeaponIds) {
        $node = $Document.SelectSingleNode("//*[@ID='$id']")
        if ($null -eq $node -or (Get-MaxItemScore $node 5) -ne 8050) {
            throw "Class starter weapon $id cannot reach WR10 for $Locale."
        }
    }

    $noShieldLoadout = @(
        '2300', '3100', '2800', '2100', '2600', '3000',
        '2900', '2700', '3200', '3200'
    )
    $armorScore = 0
    foreach ($id in $noShieldLoadout) {
        $node = $Document.SelectSingleNode("//*[@ID='$id']")
        if ($null -eq $node) { throw "Rank reference item $id is missing for $Locale." }
        $armorScore += Get-MaxItemScore $node 5
    }
    $shield = $Document.SelectSingleNode("//*[@ID='2000']")
    if ($null -eq $shield) { throw "Rank reference shield 2000 is missing for $Locale." }
    $warriorArmorScore = $armorScore + (Get-MaxItemScore $shield 5)
    if ($armorScore -ne 25350 -or $warriorArmorScore -ne 26000 -or
        $warriorArmorScore -ge [Int16]::MaxValue) {
        throw "AR14 loadout score validation failed for ${Locale}: $armorScore/$warriorArmorScore."
    }
}

function Patch-ItemBaseText(
    [string]$Text,
    [Collections.Generic.HashSet[string]]$ForgeIds,
    [string]$Locale
) {
    [xml]$before = $Text
    $customWeapon = @($before.SelectNodes("//*[@ID='1499']"))
    $customArmor = @($before.SelectNodes("//*[@ID='2190']"))
    $nonForgeGm = @($before.SelectNodes("//*[@ID='1111']"))
    if ($customWeapon.Count -ne 1 -or $customArmor.Count -ne 1 -or
        $nonForgeGm.Count -ne 1) {
        throw "Expected protected items 1499, 2190, and 1111 exactly once for $Locale."
    }
    $customWeaponBefore = $customWeapon[0].OuterXml
    $customArmorBefore = $customArmor[0].OuterXml
    $nonForgeGmBefore = $nonForgeGm[0].OuterXml
    $state = @{
        ForgeRows = 0
        WeaponTargets = 0
        NonWeaponTargets = 0
        BodyTargets = 0
        ChangedRows = 0
    }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $id = Get-AttributeValue $element 'ID'
        if (-not $ForgeIds.Contains($id)) { return $element }
        $state.ForgeRows++
        if ($protectedForgeIds.Contains($id)) { return $element }

        $type = Get-AttributeValue $element 'Type'
        $base = Get-AttributeValue $element 'BaseFraction'
        $append = Get-AttributeValue $element 'AppFraction'
        if ($null -eq $base -or $null -eq $append) {
            throw "Forgeable $Locale item $id lacks BaseFraction or AppFraction."
        }
        $baseParts = @(Split-Values $base)
        $appendParts = @(Split-Values $append)
        if ($baseParts.Count -ne 20 -or $appendParts.Count -ne 25) {
            throw "Forgeable $Locale item $id must expose exact Q20/G25 score vectors."
        }

        $updated = $element
        if ($type -eq 'weapon') {
            $state.WeaponTargets++
            if (-not (Test-Prefix $baseParts $weaponBaseFraction 10) -or
                -not (Test-Prefix $appendParts $weaponAppFraction 12)) {
                throw "Native weapon score prefix mismatch for $Locale item $id."
            }
            $armThreshold = Get-AttributeValue $element 'ArmEffFraction'
            $armEffect = Get-AttributeValue $element 'ArmEff'
            if ($null -eq $armThreshold -or $null -eq $armEffect) {
                throw "Forgeable weapon $id lacks rank fields for $Locale."
            }
            $expectedEffectText = Get-WeaponEffects (
                Get-AttributeValue $element 'Class'
            ) $id $Locale
            if (-not (Test-Prefix (Split-Values $armThreshold) $weaponRankThresholds 7) -or
                -not (Test-Prefix (Split-Values $armEffect) ([int[]](Split-Values $expectedEffectText)) 7)) {
                throw "Native weapon-rank prefix mismatch for $Locale item $id."
            }
            $updated = Set-AttributeValue $updated 'BaseFraction' (
                Join-Integers $weaponBaseFraction
            )
            $updated = Set-AttributeValue $updated 'AppFraction' (
                Join-Integers $weaponAppFraction
            )
            $updated = Set-AttributeValue $updated 'ArmEffFraction' (
                Join-Integers $weaponRankThresholds
            )
            $updated = Set-AttributeValue $updated 'ArmEff' (
                $expectedEffectText
            )
        }
        else {
            $state.NonWeaponTargets++
            if (-not $supportedNonWeaponTypes.Contains($type)) {
                throw "Unsupported forgeable nonweapon type '$type' for $Locale item $id."
            }
            $anchors = $expectedNonWeaponAnchors[$type]
            if ([int]::Parse($baseParts[9], $invariant) -ne $anchors[0] -or
                [int]::Parse($appendParts[11], $invariant) -ne $anchors[1]) {
                throw "Native score anchor mismatch for $Locale $type item $id."
            }
            $updated = Set-AttributeValue $updated 'BaseFraction' (
                Set-ScaledTail $base $ordinaryQualityProfile 9 "$Locale item $id BaseFraction"
            )
            $updated = Set-AttributeValue $updated 'AppFraction' (
                Set-ScaledTail $append $ordinaryGradeProfile 11 "$Locale item $id AppFraction"
            )
            if ($type -eq 'armor' -or $type -eq 'cloth') {
                $state.BodyTargets++
                $defendThreshold = Get-AttributeValue $element 'DefendFraction'
                $defendEffect = Get-AttributeValue $element 'DefendEff'
                if ($null -eq $defendThreshold -or $null -eq $defendEffect) {
                    throw "Body armor $id lacks rank fields for $Locale."
                }
                if (-not (Test-Prefix (Split-Values $defendThreshold) $armorRankThresholds 9) -or
                    -not (Test-Prefix (Split-Values $defendEffect) $armorRankEffects 9)) {
                    throw "Native armor-rank prefix mismatch for $Locale item $id."
                }
                $updated = Set-AttributeValue $updated 'DefendFraction' (
                    Join-Integers $armorRankThresholds
                )
                $updated = Set-AttributeValue $updated 'DefendEff' (
                    Join-Integers $armorRankEffects
                )
            }
        }
        if ($updated -cne $element) { $state.ChangedRows++ }
        return $updated
    })

    if ($state.ForgeRows -ne $ForgeIds.Count -or
        $state.WeaponTargets -ne $expectedWeaponTargets[$Locale] -or
        $state.NonWeaponTargets -ne $expectedNonWeaponTargets[$Locale] -or
        $state.BodyTargets -ne $expectedBodyTargets[$Locale]) {
        throw "Rank target-count mismatch for ${Locale}: forge=$($state.ForgeRows), weapon=$($state.WeaponTargets), nonweapon=$($state.NonWeaponTargets), body=$($state.BodyTargets)."
    }

    [xml]$after = $patched
    if ($after.SelectSingleNode("//*[@ID='1499']").OuterXml -cne $customWeaponBefore -or
        $after.SelectSingleNode("//*[@ID='2190']").OuterXml -cne $customArmorBefore -or
        $after.SelectSingleNode("//*[@ID='1111']").OuterXml -cne $nonForgeGmBefore) {
        throw "Protected GM equipment changed for $Locale."
    }
    Assert-OnlyAllowedAttributeChanges $before $after $ForgeIds $Locale
    Assert-DeepRankValidation $after $ForgeIds $Locale (
        $state.WeaponTargets
    ) $state.NonWeaponTargets $state.BodyTargets

    return [pscustomobject]@{
        Text = $patched
        ForgeRows = $state.ForgeRows
        WeaponTargets = $state.WeaponTargets
        NonWeaponTargets = $state.NonWeaponTargets
        BodyTargets = $state.BodyTargets
        ChangedRows = $state.ChangedRows
    }
}

function Get-ClientRelativePath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup source is outside the client root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

function Assert-ExactBytes(
    [byte[]]$Bytes,
    [int]$Offset,
    [byte[]]$Expected,
    [string]$Name
) {
    if ($Offset -lt 0 -or $Offset + $Expected.Count -gt $Bytes.Count) {
        throw "Origin.exe prerequisite '$Name' is outside the file."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            throw "Origin.exe prerequisite '$Name' mismatch at 0x$('{0:X}' -f ($Offset + $index))."
        }
    }
}

$resolvedClientRoot = (Resolve-Path -LiteralPath $ClientRoot).Path
$originPath = Join-Path $resolvedClientRoot 'Origin.exe'
if (-not (Test-Path -LiteralPath $originPath -PathType Leaf)) {
    throw "Origin.exe was not found: $originPath"
}
$runningClient = Get-Process -Name 'Origin' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $originPath }
if ($null -ne $runningClient) {
    throw 'Origin.exe is running. Close the client before changing rank data.'
}

# Rank calculations already need the Q20/G25 score gates. This tool never
# writes Origin.exe; it only refuses a client whose prerequisite gates/defaults
# do not match the audited build.
$originBytes = [IO.File]::ReadAllBytes($originPath)
Assert-ExactBytes $originBytes 0xA70AA ([byte[]](0x83,0xF8,0x14)) 'weapon Q20 score gate'
Assert-ExactBytes $originBytes 0xA70B3 ([byte[]](0x83,0xFF,0x19)) 'weapon G25 score gate'
Assert-ExactBytes $originBytes 0xA7505 ([byte[]](0x83,0xF9,0x15)) 'armor Q20 score gate'
Assert-ExactBytes $originBytes 0xA750E ([byte[]](0x83,0xFD,0x1A)) 'armor G25 score gate'
foreach ($offset in @(0x373F5, 0x3740A, 0x3741F, 0x37434)) {
    Assert-ExactBytes $originBytes ($offset - 1) (
        [byte[]](0x6A,0x0D,0x8D,0x44,0x24,0x1C)
    ) ('preserved rank constructor at 0x{0:X}' -f $offset)
}

$paths = @{}
$results = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $base = Join-Path $resolvedClientRoot "Localization\$locale\Settings\Sys"
    $paths[$locale] = @{
        EquipForge = Join-Path $base 'EquipForge.xml'
        ItemBase = Join-Path $base 'ItemBaseAttribute.xml'
    }
    foreach ($path in $paths[$locale].Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required client file was not found: $path"
        }
    }
    $equipText = [IO.File]::ReadAllText(
        $paths[$locale].EquipForge,
        [Text.Encoding]::UTF8
    )
    $forgeIds = Get-ForgeIds $equipText $locale
    $itemText = [IO.File]::ReadAllText(
        $paths[$locale].ItemBase,
        [Text.Encoding]::UTF8
    )
    $results[$locale] = Patch-ItemBaseText $itemText $forgeIds $locale
}

$changedPaths = [Collections.Generic.List[string]]::new()
foreach ($locale in @('en_us', 'zh_cn')) {
    $path = $paths[$locale].ItemBase
    if ([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) -cne
        $results[$locale].Text) {
        $changedPaths.Add($path)
    }
}

$backupPath = $null
if ($changedPaths.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot (
        'client-global-equipment-ranks-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    )
    [IO.Directory]::CreateDirectory($backupPath) | Out-Null
    foreach ($path in $changedPaths) {
        $relative = Get-ClientRelativePath $resolvedClientRoot $path
        $destination = Join-Path $backupPath $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
            Out-Null
        Copy-Item -LiteralPath $path -Destination $destination
    }
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = $paths[$locale].ItemBase
        if ($changedPaths.Contains($path)) {
            [IO.File]::WriteAllText($path, $results[$locale].Text, $utf8Bom)
        }
    }
}

# Re-run the complete guarded transform against what was written. A second
# transform must be byte-for-byte identical.
foreach ($locale in @('en_us', 'zh_cn')) {
    $equipText = [IO.File]::ReadAllText(
        $paths[$locale].EquipForge,
        [Text.Encoding]::UTF8
    )
    $forgeIds = Get-ForgeIds $equipText $locale
    $itemText = [IO.File]::ReadAllText(
        $paths[$locale].ItemBase,
        [Text.Encoding]::UTF8
    )
    $postWrite = Patch-ItemBaseText $itemText $forgeIds $locale
    if ($postWrite.Text -cne $itemText -or $postWrite.ChangedRows -ne 0) {
        throw "ItemBaseAttribute post-write transform is not idempotent for $locale."
    }
}

[pscustomobject]@{
    ChangedFiles = $changedPaths.Count
    BackupPath = $backupPath
    EnForgeRows = $results['en_us'].ForgeRows
    ZhForgeRows = $results['zh_cn'].ForgeRows
    EnWeaponTargets = $results['en_us'].WeaponTargets
    ZhWeaponTargets = $results['zh_cn'].WeaponTargets
    EnNonWeaponTargets = $results['en_us'].NonWeaponTargets
    ZhNonWeaponTargets = $results['zh_cn'].NonWeaponTargets
    EnBodyTargets = $results['en_us'].BodyTargets
    ZhBodyTargets = $results['zh_cn'].BodyTargets
    EnRowsChanged = $results['en_us'].ChangedRows
    ZhRowsChanged = $results['zh_cn'].ChangedRows
    WeaponFiveAttributeMaxScore = 8050
    WeaponFourAttributeMaxScore = 6780
    ArmorNoShieldMaxScore = 25350
    ArmorWithShieldMaxScore = 26000
    MaximumWeaponRank = 10
    MaximumArmorRank = 14
}
