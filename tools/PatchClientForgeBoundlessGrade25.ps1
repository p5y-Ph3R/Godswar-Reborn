param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8Bom = [Text.UTF8Encoding]::new($true)
$utf16LeBom = [Text.UnicodeEncoding]::new($false, $true)
$gb2312 = [Text.Encoding]::GetEncoding(936)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$expectedForgeRows = @{ en_us = 611; zh_cn = 550 }
$maximumQuality = 20
$maximumGrade = 25
$tier5PrimaryBonus = 32
$tier5CrystalBonus = 25

$qualityProbability = @(
    50, 30, -5, -15, -45, -75, -105, -165, -215, -225,
    -235, -245, -255, -265, -275, -285, -295, -305, -315, 0
)
$gradeProbability = @(
    60, 35, 5, -10, -25, -50, -65, -85, -115, -175, -220,
    -245, -270, -295, -320, -345, -370, -395, -420, -445,
    -470, -495, -520, -545, 0
)
$qualityCostMultipliers = @(20, 25, 30, 35, 40, 45, 50, 55, 60, 65)
$gradeCostMultipliers = @(25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85)
$numericQualityAttributes = @(
    'Attack', 'AttackRadius', 'AttackSpeed', 'MaxHP', 'MaxMP', 'Defence',
    'MagicAk', 'MagicRec', 'Hit', 'Miss', 'State', 'StateImmunity',
    'AcceptCure', 'Cure', 'PhysicalDamage', 'MagicDamage',
    'PhysicalDamageAbsorb', 'MagicDamageAbsorb',
    'Speed', 'FuryAddAk', 'FuryAddRec', 'InjureImbibe'
)

$patchHelperRoot = Join-Path $PSScriptRoot 'PatchClientForgeBoundlessGrade25'
. (Join-Path $patchHelperRoot 'ForgeXml.ps1')
. (Join-Path $patchHelperRoot 'ItemLocalization.ps1')
. (Join-Path $patchHelperRoot 'Validation.ps1')

$paths = @{}
$results = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $base = Join-Path $ClientRoot "Localization\$locale"
    $paths[$locale] = @{
        Equip = Join-Path $base 'Settings\Sys\EquipForge.xml'
        Bijou = Join-Path $base 'Settings\Sys\BijouForge.xml'
        Item = Join-Path $base 'Settings\Sys\ItemBaseAttribute.xml'
        ItemAppend = Join-Path $base 'Settings\Sys\ItemAppendAttribute.xml'
        Names = Join-Path $base 'Text\EquipName.dat'
        Descriptions = Join-Path $base 'Text\EquipDescription.dat'
    }
    foreach ($path in $paths[$locale].Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required client file was not found: $path"
        }
    }
    Assert-ItemAppendAttributePrerequisite $paths[$locale].ItemAppend $locale
    $equipResult = Patch-EquipForgeText (
        [IO.File]::ReadAllText($paths[$locale].Equip, [Text.Encoding]::UTF8)
    ) $locale
    $bijouResult = Patch-BijouForgeText (
        [IO.File]::ReadAllText($paths[$locale].Bijou, [Text.Encoding]::UTF8)
    ) $locale
    $itemResult = Patch-ItemBaseText (
        [IO.File]::ReadAllText($paths[$locale].Item, [Text.Encoding]::UTF8)
    ) (Get-ForgeIds $equipResult.Text) $locale
    $descriptionEncoding = if ($locale -eq 'en_us') {
        $utf16LeBom
    }
    else {
        $gb2312
    }
    Assert-LocalizationKeys (
        [IO.File]::ReadAllText($paths[$locale].Names, $descriptionEncoding)
    ) $locale 'EquipName'
    $descriptionResult = Patch-DescriptionText (
        [IO.File]::ReadAllText(
            $paths[$locale].Descriptions,
            $descriptionEncoding
        )
    ) $locale
    $results[$locale] = @{
        Equip = $equipResult
        Bijou = $bijouResult
        Item = $itemResult
        Descriptions = $descriptionResult
        DescriptionEncoding = $descriptionEncoding
    }
}

$exePath = Join-Path $ClientRoot 'Origin.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Origin.exe was not found: $exePath"
}
$exeBytes = [IO.File]::ReadAllBytes($exePath)

# These existing Q20/G25 score and L25 append-attribute patches are required,
# but are outside this tool's write scope.
Assert-ExactBytes $exeBytes 0xA70AA (
    [byte[]](0x83,0xF8,0x14)
) 'single-item Q20 score cap'
Assert-ExactBytes $exeBytes 0xA70B3 (
    [byte[]](0x83,0xFF,0x19)
) 'single-item G25 score cap'
Assert-ExactBytes $exeBytes 0xA7505 (
    [byte[]](0x83,0xF9,0x15)
) 'aggregate Q20 score cap'
Assert-ExactBytes $exeBytes 0xA750E (
    [byte[]](0x83,0xFD,0x1A)
) 'aggregate G25 score cap'
Assert-ExactBytes $exeBytes 0x3F275 (
    [byte[]](0x80,0x38,0x4C,0x0F,0x85)
) 'L25 XML loader hook'
Assert-ExactBytes $exeBytes 0x3F2CA (
    [byte[]](0x83,0xF9,0x19)
) 'L25 XML loader ceiling'
Assert-ExactBytes $exeBytes 0x180370 (
    [byte[]](0x74,0x5A)
) 'L25 append vector clamp branch'
Assert-ExactBytes $exeBytes 0x180381 (
    [byte[]](0x8D,0x58,0xFF,0x90,0x90)
) 'L25 append vector clamp body'

$binarySites = @(
    @{ Name='sapphire_preflight_current_q19'; Offset=0x23A18; Prefix=[byte[]](0x80,0x7F,0x48); Allowed=[byte[]](0x09,0x0C,0x13); Desired=[byte]0x13; Suffix=[byte[]](0x7E) },
    @{ Name='shared_success_quality_q20'; Offset=0x2459C; Prefix=[byte[]](0x80,0xF9); Allowed=[byte[]](0x0A,0x0C,0x0D,0x14); Desired=[byte]0x14; Suffix=[byte[]](0x0F,0x8F) },
    @{ Name='generic_result_quality_q20'; Offset=0x24776; Prefix=[byte[]](0x80,0xF9); Allowed=[byte[]](0x0A,0x0D,0x14); Desired=[byte]0x14; Suffix=[byte[]](0x7F) },
    @{ Name='quality_increment_ceiling_q20'; Offset=0x24981; Prefix=[byte[]](0x80,0x78,0x48); Allowed=[byte[]](0x0A,0x0D,0x14); Desired=[byte]0x14; Suffix=[byte[]](0x7D) },
    @{ Name='forge_ui_main_exclusive_q21'; Offset=0x15DEC4; Prefix=[byte[]](0x3C); Allowed=[byte[]](0x0B,0x0D,0x0E,0x15); Desired=[byte]0x15; Suffix=[byte[]](0x0F,0x8D) },
    @{ Name='forge_ui_alt_exclusive_q21'; Offset=0x15E818; Prefix=[byte[]](0x3C); Allowed=[byte[]](0x0B,0x0D,0x0E,0x15); Desired=[byte]0x15; Suffix=[byte[]](0x0F,0x8D) },
    @{ Name='forge_ui_sapphire_current_q19'; Offset=0x160CA2; Prefix=[byte[]](0x80,0x7B,0x48); Allowed=[byte[]](0x09,0x0C,0x13); Desired=[byte]0x13; Suffix=[byte[]](0x7E) },
    @{ Name='emerald_preflight_current_g24'; Offset=0x23A24; Prefix=[byte[]](0x80,0x7F,0x49); Allowed=[byte[]](0x0B,0x11,0x18); Desired=[byte]0x18; Suffix=[byte[]](0xBD) },
    @{ Name='shared_success_grade_g25'; Offset=0x245B0; Prefix=[byte[]](0x80,0xF9); Allowed=[byte[]](0x0C,0x12,0x19); Desired=[byte]0x19; Suffix=[byte[]](0x0F,0x8F) },
    @{ Name='generic_result_grade_g25'; Offset=0x24781; Prefix=[byte[]](0x3C); Allowed=[byte[]](0x0C,0x12,0x19); Desired=[byte]0x19; Suffix=[byte[]](0x7F,0x19) },
    @{ Name='forge_ui_emerald_current_g24'; Offset=0x160CAF; Prefix=[byte[]](0x80,0x7B,0x49); Allowed=[byte[]](0x0B,0x11,0x18); Desired=[byte]0x18; Suffix=[byte[]](0x7F,0x04) }
)

# Only quality-indexed/default base vectors and AppFraction are resized.
# ArmEff*/Defend* are rank tables, and MainAttribute is an allowed-ID list.
$constructorSites = @(
    @{ Name='item_default_max_hp'; Offset=0x37202; Desired=0x14; Float=$false },
    @{ Name='item_default_max_mp'; Offset=0x37217; Desired=0x14; Float=$false },
    @{ Name='item_default_attack'; Offset=0x3722C; Desired=0x14; Float=$false },
    @{ Name='item_default_defence'; Offset=0x37241; Desired=0x14; Float=$false },
    @{ Name='item_default_magic_attack'; Offset=0x37256; Desired=0x14; Float=$false },
    @{ Name='item_default_magic_recovery'; Offset=0x3726F; Desired=0x14; Float=$false },
    @{ Name='item_default_hit'; Offset=0x37280; Desired=0x14; Float=$false },
    @{ Name='item_default_miss'; Offset=0x37295; Desired=0x14; Float=$false },
    @{ Name='item_default_fury_attack'; Offset=0x372AA; Desired=0x14; Float=$false },
    @{ Name='item_default_fury_recovery'; Offset=0x372BF; Desired=0x14; Float=$false },
    @{ Name='item_default_speed'; Offset=0x372D6; Desired=0x14; Float=$true },
    @{ Name='item_default_physical_damage'; Offset=0x372ED; Desired=0x14; Float=$true },
    @{ Name='item_default_magic_damage'; Offset=0x37304; Desired=0x14; Float=$true },
    @{ Name='item_default_injure_imbibe'; Offset=0x37319; Desired=0x14; Float=$false },
    @{ Name='item_default_accept_cure'; Offset=0x37330; Desired=0x14; Float=$true },
    @{ Name='item_default_cure'; Offset=0x37347; Desired=0x14; Float=$true },
    @{ Name='item_default_state'; Offset=0x3735C; Desired=0x14; Float=$false },
    @{ Name='item_default_state_immunity'; Offset=0x37371; Desired=0x14; Float=$false },
    @{ Name='item_default_attack_radius'; Offset=0x37388; Desired=0x14; Float=$true },
    @{ Name='item_default_attack_speed_1'; Offset=0x3739F; Desired=0x14; Float=$true },
    @{ Name='item_default_attack_speed_2'; Offset=0x373BA; Desired=0x14; Float=$false },
    @{ Name='item_default_base_fraction'; Offset=0x373CB; Desired=0x14; Float=$false },
    @{ Name='item_default_append_fraction'; Offset=0x373E0; Desired=0x19; Float=$false }
)
foreach ($site in $constructorSites) {
    $suffix = if ($site.Float) {
        [byte[]](0xD9,0x5C,0x24,0x1C)
    }
    else {
        [byte[]](0x8D,0x44,0x24,0x1C)
    }
    $allowedCounts = if ($site.Desired -eq 0x14) {
        [byte[]](0x0C,0x0D,0x14)
    }
    else {
        [byte[]](0x0C,0x0D,0x19)
    }
    $binarySites += @{
        Name = $site.Name
        Offset = $site.Offset
        Prefix = [byte[]](0x6A)
        Allowed = $allowedCounts
        Desired = [byte]$site.Desired
        Suffix = $suffix
    }
}

# These four constructor counts belong to independent rank tables. Require the
# current Q13-era count and validate it again after writing, but never resize it.
$binarySites += @(
    @{ Name='preserve_item_default_armor_effect'; Offset=0x373F5; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) },
    @{ Name='preserve_item_default_armor_effect_ratio'; Offset=0x3740A; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) },
    @{ Name='preserve_item_default_defend_effect'; Offset=0x3741F; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) },
    @{ Name='preserve_item_default_defend_ratio'; Offset=0x37434; Prefix=[byte[]](0x6A); Allowed=[byte[]](0x0D); Desired=[byte]0x0D; Suffix=[byte[]](0x8D,0x44,0x24,0x1C) }
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
    if ([IO.File]::ReadAllText(
            $paths[$locale].Equip,
            [Text.Encoding]::UTF8
        ) -cne $result.Equip.Text) {
        $changedPaths.Add($paths[$locale].Equip)
    }
    if ([IO.File]::ReadAllText(
            $paths[$locale].Bijou,
            [Text.Encoding]::UTF8
        ) -cne $result.Bijou) {
        $changedPaths.Add($paths[$locale].Bijou)
    }
    if ([IO.File]::ReadAllText(
            $paths[$locale].Item,
            [Text.Encoding]::UTF8
        ) -cne $result.Item.Text) {
        $changedPaths.Add($paths[$locale].Item)
    }
    if ([IO.File]::ReadAllText(
            $paths[$locale].Descriptions,
            $result.DescriptionEncoding
        ) -cne $result.Descriptions) {
        $changedPaths.Add($paths[$locale].Descriptions)
    }
}
if ($binaryChanges -gt 0) { $changedPaths.Add($exePath) }

$changedPathSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($path in $changedPaths) {
    [void]$changedPathSet.Add([IO.Path]::GetFullPath($path))
}

$backupPath = $null
if ($changedPaths.Count -gt 0) {
    $backupPath = Join-Path $BackupRoot (
        'client-forge-boundless-g25-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    )
    [IO.Directory]::CreateDirectory($backupPath) | Out-Null
    foreach ($path in $changedPaths) {
        $relative = Get-ClientRelativePath $ClientRoot $path
        $destination = Join-Path $backupPath $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
            Out-Null
        Copy-Item -LiteralPath $path -Destination $destination
    }
    foreach ($locale in @('en_us', 'zh_cn')) {
        $result = $results[$locale]
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Equip)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Equip,
                $result.Equip.Text,
                $utf8Bom
            )
        }
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Bijou)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Bijou,
                $result.Bijou,
                $utf8Bom
            )
        }
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Item)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Item,
                $result.Item.Text,
                $utf8Bom
            )
        }
        if ($changedPathSet.Contains(
                [IO.Path]::GetFullPath($paths[$locale].Descriptions)
            )) {
            [IO.File]::WriteAllText(
                $paths[$locale].Descriptions,
                $result.Descriptions,
                $result.DescriptionEncoding
            )
        }
    }
    if ($binaryChanges -gt 0) {
        [IO.File]::WriteAllBytes($exePath, $exeBytes)
    }
}

# Re-run every transform against written data. Any difference is an
# idempotence or post-write validation failure.
foreach ($locale in @('en_us', 'zh_cn')) {
    $equipText = [IO.File]::ReadAllText(
        $paths[$locale].Equip,
        [Text.Encoding]::UTF8
    )
    if ((Patch-EquipForgeText $equipText $locale).Text -cne $equipText) {
        throw "EquipForge post-write validation was not idempotent for $locale."
    }
    $bijouText = [IO.File]::ReadAllText(
        $paths[$locale].Bijou,
        [Text.Encoding]::UTF8
    )
    if ((Patch-BijouForgeText $bijouText $locale) -cne $bijouText) {
        throw "BijouForge post-write validation was not idempotent for $locale."
    }
    $itemText = [IO.File]::ReadAllText(
        $paths[$locale].Item,
        [Text.Encoding]::UTF8
    )
    if ((Patch-ItemBaseText (
                $itemText
            ) (Get-ForgeIds $equipText) $locale).Text -cne $itemText) {
        throw "ItemBaseAttribute post-write validation was not idempotent for $locale."
    }
    $encoding = $results[$locale].DescriptionEncoding
    $descriptionText = [IO.File]::ReadAllText(
        $paths[$locale].Descriptions,
        $encoding
    )
    if ((Patch-DescriptionText $descriptionText $locale) -cne
        $descriptionText) {
        throw "Description post-write validation was not idempotent for $locale."
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
    EnForgeRowsChanged = $results['en_us'].Equip.ChangedRows
    ZhForgeRowsChanged = $results['zh_cn'].Equip.ChangedRows
    EnItemRows = $results['en_us'].Item.Rows
    ZhItemRows = $results['zh_cn'].Item.Rows
    EnItemRowsChanged = $results['en_us'].Item.ChangedRows
    ZhItemRowsChanged = $results['zh_cn'].Item.ChangedRows
    Tier5Ids = '4215,4225,4234'
    MaximumQuality = $maximumQuality
    MaximumGrade = $maximumGrade
    Tier5PrimaryBonus = $tier5PrimaryBonus
    Tier5CrystalBonus = $tier5CrystalBonus
    BinaryBytesChanged = $binaryChanges
    OriginSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash
}
