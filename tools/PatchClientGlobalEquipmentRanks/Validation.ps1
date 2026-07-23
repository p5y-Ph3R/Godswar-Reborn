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
