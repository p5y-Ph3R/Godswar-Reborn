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

$patchHelperRoot = Join-Path $PSScriptRoot 'PatchClientGlobalEquipmentRanks'
. (Join-Path $patchHelperRoot 'ForgeValues.ps1')
. (Join-Path $patchHelperRoot 'Validation.ps1')
. (Join-Path $patchHelperRoot 'ItemBase.ps1')
. (Join-Path $patchHelperRoot 'ClientFiles.ps1')

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
