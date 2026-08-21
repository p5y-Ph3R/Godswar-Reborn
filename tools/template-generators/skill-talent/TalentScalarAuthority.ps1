$talentTooltipScale = [decimal]2.6

# This table fences every class. Values are the reviewed stock per-rank
# server scalars.
$talentAuthorityDefinitions = @{
    0 = @{ ClassId = 0; Value = [decimal]60 }
    1 = @{ ClassId = 0; Value = [decimal]8 }
    2 = @{ ClassId = 0; Value = [decimal]6 }
    3 = @{ ClassId = 0; Value = [decimal]4 }
    4 = @{ ClassId = 0; Value = [decimal]8 }
    5 = @{ ClassId = 0; Value = [decimal]0.004 }
    6 = @{ ClassId = 0; Value = [decimal]12 }
    7 = @{ ClassId = 0; Value = [decimal]4 }
    8 = @{ ClassId = 0; Value = [decimal]4 }
    9 = @{ ClassId = 0; Value = [decimal]20 }
    10 = @{ ClassId = 0; Value = [decimal]1 }
    11 = @{ ClassId = 0; Value = [decimal]12 }
    12 = @{ ClassId = 0; Value = [decimal]14 }
    13 = @{ ClassId = 0; Value = [decimal]100 }
    14 = @{ ClassId = 0; Value = [decimal]1.6 }
    15 = @{ ClassId = 0; Value = [decimal]7 }
    16 = @{ ClassId = 0; Value = [decimal]10 }
    17 = @{ ClassId = 0; Value = [decimal]1.2 }
    50 = @{ ClassId = 1; Value = [decimal]3 }
    51 = @{ ClassId = 1; Value = [decimal]10 }
    52 = @{ ClassId = 1; Value = [decimal]9 }
    53 = @{ ClassId = 1; Value = [decimal]50 }
    54 = @{ ClassId = 1; Value = [decimal]2 }
    55 = @{ ClassId = 1; Value = [decimal]0.005 }
    56 = @{ ClassId = 1; Value = [decimal]5 }
    57 = @{ ClassId = 1; Value = [decimal]16 }
    58 = @{ ClassId = 1; Value = [decimal]4 }
    59 = @{ ClassId = 1; Value = [decimal]7 }
    60 = @{ ClassId = 1; Value = [decimal]3 }
    61 = @{ ClassId = 1; Value = [decimal]0.01 }
    62 = @{ ClassId = 1; Value = [decimal]20 }
    63 = @{ ClassId = 1; Value = [decimal]1.6 }
    64 = @{ ClassId = 1; Value = [decimal]4 }
    65 = @{ ClassId = 1; Value = [decimal]1.2 }
    66 = @{ ClassId = 1; Value = [decimal]7 }
    67 = @{ ClassId = 1; Value = [decimal]90 }
    68 = @{ ClassId = 1; Value = [decimal]90 }
    100 = @{ ClassId = 3; Value = [decimal]12 }
    101 = @{ ClassId = 3; Value = [decimal]8 }
    102 = @{ ClassId = 3; Value = [decimal]3 }
    103 = @{ ClassId = 3; Value = [decimal]4 }
    104 = @{ ClassId = 3; Value = [decimal]4 }
    105 = @{ ClassId = 3; Value = [decimal]14 }
    106 = @{ ClassId = 3; Value = [decimal]4 }
    107 = @{ ClassId = 3; Value = [decimal]8 }
    108 = @{ ClassId = 3; Value = [decimal]4 }
    109 = @{ ClassId = 3; Value = [decimal]10 }
    110 = @{ ClassId = 3; Value = [decimal]20 }
    111 = @{ ClassId = 3; Value = [decimal]8 }
    112 = @{ ClassId = 3; Value = [decimal]0.006 }
    113 = @{ ClassId = 3; Value = [decimal]1.2 }
    114 = @{ ClassId = 3; Value = [decimal]6 }
    115 = @{ ClassId = 3; Value = [decimal]1 }
    116 = @{ ClassId = 3; Value = [decimal]80 }
    117 = @{ ClassId = 3; Value = [decimal]4 }
    150 = @{ ClassId = 2; Value = [decimal]2 }
    151 = @{ ClassId = 2; Value = [decimal]5 }
    152 = @{ ClassId = 2; Value = [decimal]10 }
    153 = @{ ClassId = 2; Value = [decimal]7 }
    154 = @{ ClassId = 2; Value = [decimal]10 }
    155 = @{ ClassId = 2; Value = [decimal]9 }
    156 = @{ ClassId = 2; Value = [decimal]1 }
    157 = @{ ClassId = 2; Value = [decimal]5 }
    158 = @{ ClassId = 2; Value = [decimal]14 }
    159 = @{ ClassId = 2; Value = [decimal]1.4 }
    160 = @{ ClassId = 2; Value = [decimal]10 }
    161 = @{ ClassId = 2; Value = [decimal]0.02 }
    162 = @{ ClassId = 2; Value = [decimal]8 }
    163 = @{ ClassId = 2; Value = [decimal]5 }
    164 = @{ ClassId = 2; Value = [decimal]12 }
    165 = @{ ClassId = 2; Value = [decimal]3 }
    166 = @{ ClassId = 2; Value = [decimal]1.2 }
    167 = @{ ClassId = 2; Value = [decimal]90 }
}

function Assert-TalentAuthorityCoverage($talentClassOrder) {
    $actualIds = @($talentClassOrder.Keys | ForEach-Object { [int]$_ })
    $expectedIds = @($talentAuthorityDefinitions.Keys | ForEach-Object {
        [int]$_
    })
    if ($actualIds.Count -ne $expectedIds.Count -or
        (Compare-Object $actualIds $expectedIds).Count -ne 0) {
        throw (
            "Talent authority must cover all 73 reviewed IDs exactly; " +
            "actual IDs: $(@($actualIds | Sort-Object) -join ',').")
    }

    foreach ($talentId in $expectedIds) {
        $actualClassId = [int]$talentClassOrder[$talentId].ClassId
        $expectedClassId =
            [int]$talentAuthorityDefinitions[$talentId].ClassId
        if ($actualClassId -ne $expectedClassId) {
            throw (
                "Talent $talentId moved from reviewed class " +
                "$expectedClassId to class $actualClassId.")
        }
    }
}

function Resolve-AuthoritativeTalentEffectValue(
    [int]$talentId,
    [int]$classId,
    [decimal]$sourceValue,
    [hashtable]$observedModes
) {
    if (-not $talentAuthorityDefinitions.ContainsKey($talentId)) {
        throw "Talent $talentId has no reviewed server scalar."
    }

    $definition = $talentAuthorityDefinitions[$talentId]
    if ([int]$definition.ClassId -ne $classId) {
        throw "Talent $talentId has unexpected class $classId."
    }

    $authoritative = [decimal]$definition.Value
    $tooltip = $authoritative * $talentTooltipScale
    $mode = if ($sourceValue -eq $authoritative) {
        "Stock"
    } elseif ($sourceValue -eq $tooltip) {
        "Tooltip"
    } else {
        throw (
            "Talent $talentId has unexpected scalar $sourceValue; expected " +
            "server $authoritative or tooltip $tooltip.")
    }

    if ($observedModes.ContainsKey($classId) -and
        $observedModes[$classId] -ne $mode) {
        throw (
            "Talent class $classId mixes stock and tooltip scalars " +
            "($($observedModes[$classId]) then $mode at talent $talentId).")
    }
    $observedModes[$classId] = $mode
    return ConvertTo-TalentCanonicalDecimal $authoritative
}

function Assert-TalentAuthorityModes([hashtable]$observedModes) {
    $missing = @(0..3 | Where-Object { -not $observedModes.ContainsKey($_) })
    if ($missing.Count -ne 0 -or $observedModes.Count -ne 4) {
        throw (
            "Talent authority did not classify every class; missing: " +
            "$($missing -join ',').")
    }
}

function Resolve-LegacyTalentEffectValue([int]$talentId, [int]$classId) {
    $definition = $talentAuthorityDefinitions[$talentId]
    if ($null -eq $definition -or [int]$definition.ClassId -ne $classId) {
        throw "Talent $talentId cannot be emitted into legacy SQL."
    }

    $value = [decimal]$definition.Value
    if ($classId -eq 1) {
        $value *= $talentTooltipScale
    }
    return ConvertTo-TalentCanonicalDecimal $value
}

function Format-TalentScalar([decimal]$value) {
    return $value.ToString(
        "G29",
        [System.Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-TalentCanonicalDecimal([decimal]$value) {
    return [decimal]::Parse(
        (Format-TalentScalar $value),
        [System.Globalization.CultureInfo]::InvariantCulture)
}
