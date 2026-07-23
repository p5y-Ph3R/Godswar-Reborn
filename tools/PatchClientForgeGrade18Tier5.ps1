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

$patchHelperRoot = Join-Path $PSScriptRoot 'PatchClientForgeGrade18Tier5'
. (Join-Path $patchHelperRoot 'Common.ps1')
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
