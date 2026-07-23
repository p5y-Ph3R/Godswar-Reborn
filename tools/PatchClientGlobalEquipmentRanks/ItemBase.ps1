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
