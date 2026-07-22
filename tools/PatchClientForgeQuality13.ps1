param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$BackupRoot = 'C:\Reborn\backups'
)

$ErrorActionPreference = 'Stop'
$utf8Bom = [Text.UTF8Encoding]::new($true)
$invariant = [Globalization.CultureInfo]::InvariantCulture

$numericQualityAttributes = @(
    'Attack', 'AttackRadius', 'AttackSpeed', 'MaxHP', 'MaxMP', 'Defence',
    'MagicAk', 'MagicRec', 'Hit', 'Miss', 'State', 'StateImmunity',
    'AcceptCure', 'Cure', 'PhysicalDamage', 'MagicDamage',
    'MagicDamageAbsorb', 'PhysicalDamageAbsorb', 'Speed', 'FuryAddAk',
    'FuryAddRec', 'InjureImbibe', 'DefendFraction', 'DefendEff',
    'AppFraction'
)
$repeatLastAttributes = @('MainAttribute', 'ArmEffFraction', 'ArmEff')

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

function Extend-NumericValues([string]$Value, [int]$TargetCount = 13) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -ge $TargetCount) { return $Value }
    if ($parts.Count -lt 2) { throw "Cannot extrapolate a numeric quality vector with $($parts.Count) value(s)." }

    $numbers = [Collections.Generic.List[decimal]]::new()
    foreach ($part in $parts) { $numbers.Add([decimal]::Parse($part, $invariant)) }
    $delta = $numbers[$numbers.Count - 1] - $numbers[$numbers.Count - 2]
    while ($numbers.Count -lt $TargetCount) { $numbers.Add($numbers[$numbers.Count - 1] + $delta) }
    return (($numbers | ForEach-Object { Format-Decimal $_ }) -join ',')
}

function Extend-RepeatLast([string]$Value, [int]$TargetCount = 13) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -ge $TargetCount) { return $Value }
    if ($parts.Count -eq 0) { throw 'Cannot extend an empty quality vector.' }
    while ($parts.Count -lt $TargetCount) { $parts += $parts[$parts.Count - 1] }
    return ($parts -join ',')
}

function Set-BaseFractionQ13([string]$Value, [string]$Type) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -ge 13) { return $Value }
    if ($parts.Count -lt 10) { throw "BaseFraction for type '$Type' has only $($parts.Count) values." }

    $tail = switch ($Type.ToLowerInvariant()) {
        'weapon' { @('260', '340', '440'); break }
        { $_ -in @('armor', 'cloth') } { @('345', '390', '443'); break }
        default { @('230', '260', '295') }
    }
    return ((@($parts | Select-Object -First 10) + $tail) -join ',')
}

function Set-Q10ToQ12([string]$Value, [string[]]$Tail) {
    $parts = @(Split-Values $Value)
    if ($parts.Count -lt 9) { throw "Forge vector has only $($parts.Count) values; Q1-Q9 are required." }
    $result = @($parts | Select-Object -First 9) + $Tail
    if ($parts.Count -gt 12) { $result += @($parts | Select-Object -Skip 12) }
    return ($result -join ',')
}

function Get-QualitySilverTail([string]$Bmoney, [string]$ItemId) {
    $parts = @(Split-Values $Bmoney)
    if ($parts.Count -lt 9) { throw "Bmoney for item $ItemId has only $($parts.Count) values." }
    $q9 = [decimal]::Parse($parts[8], $invariant)
    if ($q9 -eq 18) { return @('20', '25', '30') }

    $unit = $q9 / [decimal]18.6
    if ($unit -ne [decimal]::Truncate($unit)) {
        throw "Bmoney Q9=$q9 for item $ItemId does not map to an exact economy unit."
    }
    return @(
        (Format-Decimal ($unit * 20)),
        (Format-Decimal ($unit * 25)),
        (Format-Decimal ($unit * 30))
    )
}

function Patch-EquipForgeText([string]$Text, [string]$Locale) {
    $state = @{ Rows = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $id = Get-AttributeValue $element 'ID'
        $base = Get-AttributeValue $element 'BaseProyAdd'
        $money = Get-AttributeValue $element 'Bmoney'
        if ($null -eq $base -or $null -eq $money) { return $element }

        $state.Rows++
        $updated = Set-AttributeValue $element 'BaseProyAdd' (Set-Q10ToQ12 $base @('-225', '-235', '-245'))
        $updated = Set-AttributeValue $updated 'Bmoney' (Set-Q10ToQ12 $money (Get-QualitySilverTail $money $id))
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    if ($state.Rows -eq 0) { throw "No EquipForge rows were found for $Locale." }

    [xml]$doc = $patched
    $nodes = @($doc.SelectNodes('//*[@ID and @BaseProyAdd and @Bmoney]'))
    if ($nodes.Count -ne $state.Rows) { throw "EquipForge validation count mismatch for $Locale." }
    foreach ($node in $nodes) {
        $base = @(Split-Values $node.BaseProyAdd)
        $money = @(Split-Values $node.Bmoney)
        if ($base.Count -lt 12 -or ($base[9..11] -join ',') -ne '-225,-235,-245') {
            throw "BaseProyAdd Q10-Q12 validation failed for $Locale item $($node.ID)."
        }
        $expectedMoney = @(Get-QualitySilverTail $node.Bmoney $node.ID)
        if ($money.Count -lt 12 -or ($money[9..11] -join ',') -ne ($expectedMoney -join ',')) {
            throw "Bmoney Q10-Q12 validation failed for $Locale item $($node.ID)."
        }
    }
    return [pscustomobject]@{ Text = $patched; Rows = $state.Rows; ChangedRows = $state.Changed }
}

function Patch-BijouForgeText([string]$Text, [string]$Locale) {
    # Origin.exe loads Round with sscanf("%d,%d"): use inclusive endpoints,
    # never an enumerated list whose values after the second are ignored.
    $state = @{ Matched = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="4213"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $materialType = Get-AttributeValue $element 'MaterialType'
        if ($materialType -ne '2') {
            throw "Level 4 Sapphire has unexpected MaterialType '$materialType' for $Locale."
        }

        $state.Matched++
        $updated = Set-AttributeValue $element 'Round' '8,12'
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    if ($state.Matched -ne 1) {
        throw "Expected exactly one Level 4 Sapphire row for $Locale; found $($state.Matched)."
    }

    [xml]$doc = $patched
    $nodes = @($doc.SelectNodes('//*[@ID="4213"]'))
    if ($nodes.Count -ne 1 -or $nodes[0].MaterialType -ne '2' -or $nodes[0].Round -ne '8,12') {
        throw "Level 4 Sapphire endpoint validation failed for $Locale."
    }
    return [pscustomobject]@{ Text = $patched; Rows = $state.Matched; ChangedRows = $state.Changed }
}

function Get-ForgeIds([string]$EquipForgeText) {
    [xml]$doc = $EquipForgeText
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $doc.SelectNodes('//*[@ID]')) {
        if (-not $ids.Add($node.ID)) { throw "Duplicate EquipForge ID $($node.ID)." }
    }
    return $ids
}

function Patch-ItemBaseText([string]$Text, [Collections.Generic.HashSet[string]]$ForgeIds, [string]$Locale) {
    $state = @{ Matched = 0; Changed = 0 }
    $pattern = '<(?<tag>[A-Za-z_][\w]*)\b[^<>]*\bID="\d+"[^<>]*/>'
    $patched = [regex]::Replace($Text, $pattern, {
        param($match)
        $element = $match.Value
        $id = Get-AttributeValue $element 'ID'
        if (-not $ForgeIds.Contains($id)) { return $element }

        $state.Matched++
        $updated = $element
        foreach ($name in $numericQualityAttributes) {
            $value = Get-AttributeValue $updated $name
            if ($null -ne $value) { $updated = Set-AttributeValue $updated $name (Extend-NumericValues $value) }
        }
        foreach ($name in $repeatLastAttributes) {
            $value = Get-AttributeValue $updated $name
            if ($null -ne $value) { $updated = Set-AttributeValue $updated $name (Extend-RepeatLast $value) }
        }
        $type = Get-AttributeValue $updated 'Type'
        $base = Get-AttributeValue $updated 'BaseFraction'
        if ($null -eq $type -or $null -eq $base) { throw "Forgeable item $id lacks Type or BaseFraction." }
        $updated = Set-AttributeValue $updated 'BaseFraction' (Set-BaseFractionQ13 $base $type)
        if ($updated -cne $element) { $state.Changed++ }
        return $updated
    })
    if ($state.Matched -ne $ForgeIds.Count) {
        throw "ItemBaseAttribute $Locale matched $($state.Matched) of $($ForgeIds.Count) forge IDs."
    }

    [xml]$doc = $patched
    $matched = @($doc.SelectNodes('//*[@ID]') | Where-Object { $ForgeIds.Contains($_.ID) })
    if ($matched.Count -ne $ForgeIds.Count) { throw "ItemBaseAttribute validation count mismatch for $Locale." }
    foreach ($node in $matched) {
        foreach ($name in ($numericQualityAttributes + $repeatLastAttributes + @('BaseFraction'))) {
            if ($node.HasAttribute($name) -and @(Split-Values $node.GetAttribute($name)).Count -lt 13) {
                throw "$name remains shorter than Q13 for $Locale item $($node.ID)."
            }
        }
    }
    return [pscustomobject]@{ Text = $patched; Rows = $state.Matched; ChangedRows = $state.Changed }
}

function Assert-BinaryContext([byte[]]$Bytes, [hashtable]$Site) {
    for ($i = 0; $i -lt $Site.Prefix.Length; $i++) {
        if ($Bytes[$Site.Offset - $Site.Prefix.Length + $i] -ne $Site.Prefix[$i]) {
            throw "Origin.exe prefix mismatch at $($Site.Name) (0x$('{0:X}' -f $Site.Offset))."
        }
    }
    for ($i = 0; $i -lt $Site.Suffix.Length; $i++) {
        if ($Bytes[$Site.Offset + 1 + $i] -ne $Site.Suffix[$i]) {
            throw "Origin.exe suffix mismatch at $($Site.Name) (0x$('{0:X}' -f $Site.Offset))."
        }
    }
    $allowed = @($Site.Original, $Site.Desired)
    if ($Site.ContainsKey('Compatible')) { $allowed += @($Site.Compatible) }
    if ($Bytes[$Site.Offset] -notin $allowed) {
        throw "Origin.exe byte mismatch at $($Site.Name): got 0x$('{0:X2}' -f $Bytes[$Site.Offset])."
    }
}

$equipPaths = @{}
$bijouPaths = @{}
$itemPaths = @{}
$equipResults = @{}
$bijouResults = @{}
$itemResults = @{}
foreach ($locale in @('en_us', 'zh_cn')) {
    $equipPaths[$locale] = Join-Path $ClientRoot "Localization\$locale\Settings\Sys\EquipForge.xml"
    $bijouPaths[$locale] = Join-Path $ClientRoot "Localization\$locale\Settings\Sys\BijouForge.xml"
    $itemPaths[$locale] = Join-Path $ClientRoot "Localization\$locale\Settings\Sys\ItemBaseAttribute.xml"
    foreach ($path in @($equipPaths[$locale], $bijouPaths[$locale], $itemPaths[$locale])) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required client file not found: $path" }
    }
    $equipText = [IO.File]::ReadAllText($equipPaths[$locale], [Text.Encoding]::UTF8)
    $equipResults[$locale] = Patch-EquipForgeText $equipText $locale
    $bijouText = [IO.File]::ReadAllText($bijouPaths[$locale], [Text.Encoding]::UTF8)
    $bijouResults[$locale] = Patch-BijouForgeText $bijouText $locale
    $itemText = [IO.File]::ReadAllText($itemPaths[$locale], [Text.Encoding]::UTF8)
    $itemResults[$locale] = Patch-ItemBaseText $itemText (Get-ForgeIds $equipResults[$locale].Text) $locale
}

$exePath = Join-Path $ClientRoot 'Origin.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw "Origin.exe not found: $exePath" }
$exeBytes = [IO.File]::ReadAllBytes($exePath)
$sites = @(
    @{ Name='sapphire_preflight_current_max'; Offset=0x23A18; Prefix=[byte[]](0x80,0x7F,0x48); Original=0x09; Desired=0x0C; Suffix=[byte[]](0x7E) },
    # The shared result/candidate paths must also admit a Q13 item when Ruby
    # or Emerald forging is selected. Sapphire-specific sites below remain
    # capped at current Q12, so this does not permit a Q14 upgrade.
    @{ Name='result_current_quality_max';     Offset=0x2459C; Prefix=[byte[]](0x80,0xF9);      Original=0x0A; Compatible=0x0C; Desired=0x0D; Suffix=[byte[]](0x0F,0x8F) },
    @{ Name='generic_result_quality_max';    Offset=0x24776; Prefix=[byte[]](0x80,0xF9);      Original=0x0A; Desired=0x0D; Suffix=[byte[]](0x7F) },
    @{ Name='quality_increment_ceiling';     Offset=0x24981; Prefix=[byte[]](0x80,0x78,0x48); Original=0x0A; Desired=0x0D; Suffix=[byte[]](0x7D) },
    @{ Name='forge_ui_main_exclusive_max';   Offset=0x15DEC4;Prefix=[byte[]](0x3C);           Original=0x0B; Compatible=0x0D; Desired=0x0E; Suffix=[byte[]](0x0F,0x8D) },
    @{ Name='forge_ui_alt_exclusive_max';    Offset=0x15E818;Prefix=[byte[]](0x3C);           Original=0x0B; Compatible=0x0D; Desired=0x0E; Suffix=[byte[]](0x0F,0x8D) },
    @{ Name='forge_ui_sapphire_current_max'; Offset=0x160CA2;Prefix=[byte[]](0x80,0x7B,0x48); Original=0x09; Desired=0x0C; Suffix=[byte[]](0x7E) }
)

# The item-base constructor pre-fills absent quality attributes with native
# zero/default vectors. Q13 indexes element 12, so every default-fill count
# must grow from 12 to 13 as well as every XML-authored vector. AttackSpeed is
# intentionally filled in two chunks and therefore has two sites.
$defaultQualityVectorSites = @(
    @{ Name='item_default_max_hp';             Offset=0x37202; VectorOffset=0x0EC; Float=$false },
    @{ Name='item_default_max_mp';             Offset=0x37217; VectorOffset=0x0FC; Float=$false },
    @{ Name='item_default_attack';             Offset=0x3722C; VectorOffset=0x08C; Float=$false },
    @{ Name='item_default_defence';            Offset=0x37241; VectorOffset=0x0BC; Float=$false },
    @{ Name='item_default_magic_attack';       Offset=0x37256; VectorOffset=0x25C; Float=$false },
    @{ Name='item_default_magic_recovery';     Offset=0x3726F; VectorOffset=0x26C; Float=$false },
    @{ Name='item_default_hit';                Offset=0x37280; VectorOffset=0x0CC; Float=$false },
    @{ Name='item_default_miss';               Offset=0x37295; VectorOffset=0x0DC; Float=$false },
    @{ Name='item_default_fury_attack';        Offset=0x372AA; VectorOffset=0x2AC; Float=$false },
    @{ Name='item_default_fury_recovery';      Offset=0x372BF; VectorOffset=0x2BC; Float=$false },
    @{ Name='item_default_speed';              Offset=0x372D6; VectorOffset=0x2CC; Float=$true  },
    @{ Name='item_default_physical_damage';    Offset=0x372ED; VectorOffset=0x130; Float=$true  },
    @{ Name='item_default_magic_damage';       Offset=0x37304; VectorOffset=0x140; Float=$true  },
    @{ Name='item_default_injure_imbibe';      Offset=0x37319; VectorOffset=0x27C; Float=$false },
    @{ Name='item_default_accept_cure';        Offset=0x37330; VectorOffset=0x110; Float=$true  },
    @{ Name='item_default_cure';               Offset=0x37347; VectorOffset=0x120; Float=$true  },
    @{ Name='item_default_state';              Offset=0x3735C; VectorOffset=0x28C; Float=$false },
    @{ Name='item_default_state_immunity';     Offset=0x37371; VectorOffset=0x29C; Float=$false },
    @{ Name='item_default_attack_radius';      Offset=0x37388; VectorOffset=0x09C; Float=$true  },
    @{ Name='item_default_attack_speed_1';     Offset=0x3739F; VectorOffset=0x0AC; Float=$true  },
    @{ Name='item_default_attack_speed_2';     Offset=0x373BA; VectorOffset=0x0AC; Float=$false },
    @{ Name='item_default_base_fraction';      Offset=0x373CB; VectorOffset=0x30C; Float=$false },
    @{ Name='item_default_append_fraction';    Offset=0x373E0; VectorOffset=0x31C; Float=$false },
    @{ Name='item_default_armor_effect';       Offset=0x373F5; VectorOffset=0x32C; Float=$false },
    @{ Name='item_default_armor_effect_ratio'; Offset=0x3740A; VectorOffset=0x33C; Float=$false },
    @{ Name='item_default_defend_effect';      Offset=0x3741F; VectorOffset=0x34C; Float=$false },
    @{ Name='item_default_defend_ratio';       Offset=0x37434; VectorOffset=0x35C; Float=$false }
)
foreach ($defaultSite in $defaultQualityVectorSites) {
    $suffix = [byte[]](0x8D,0x44,0x24,0x1C)
    if ($defaultSite.Float) { $suffix = [byte[]](0xD9,0x5C,0x24,0x1C) }
    $sites += @{
        Name = $defaultSite.Name
        Offset = $defaultSite.Offset
        Prefix = [byte[]](0x6A)
        Original = 0x0C
        Desired = 0x0D
        Suffix = $suffix
    }
}
$binaryChanges = 0
foreach ($site in $sites) {
    Assert-BinaryContext $exeBytes $site
    if ($exeBytes[$site.Offset] -ne $site.Desired) { $exeBytes[$site.Offset] = $site.Desired; $binaryChanges++ }
}

$textChanges = 0
foreach ($locale in @('en_us', 'zh_cn')) {
    if ([IO.File]::ReadAllText($equipPaths[$locale], [Text.Encoding]::UTF8) -cne $equipResults[$locale].Text) { $textChanges++ }
    if ([IO.File]::ReadAllText($bijouPaths[$locale], [Text.Encoding]::UTF8) -cne $bijouResults[$locale].Text) { $textChanges++ }
    if ([IO.File]::ReadAllText($itemPaths[$locale], [Text.Encoding]::UTF8) -cne $itemResults[$locale].Text) { $textChanges++ }
}

$backupPath = $null
if (($binaryChanges + $textChanges) -gt 0) {
    $backupPath = Join-Path $BackupRoot ("client-forge-q13-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $backupPath | Out-Null
    Copy-Item -LiteralPath $exePath -Destination (Join-Path $backupPath 'Origin.exe')
    foreach ($locale in @('en_us', 'zh_cn')) {
        Copy-Item -LiteralPath $equipPaths[$locale] -Destination (Join-Path $backupPath "$locale.EquipForge.xml")
        Copy-Item -LiteralPath $bijouPaths[$locale] -Destination (Join-Path $backupPath "$locale.BijouForge.xml")
        Copy-Item -LiteralPath $itemPaths[$locale] -Destination (Join-Path $backupPath "$locale.ItemBaseAttribute.xml")
    }

    foreach ($locale in @('en_us', 'zh_cn')) {
        [IO.File]::WriteAllText($equipPaths[$locale], $equipResults[$locale].Text, $utf8Bom)
        [IO.File]::WriteAllText($bijouPaths[$locale], $bijouResults[$locale].Text, $utf8Bom)
        [IO.File]::WriteAllText($itemPaths[$locale], $itemResults[$locale].Text, $utf8Bom)
    }
    [IO.File]::WriteAllBytes($exePath, $exeBytes)
}

$writtenBytes = [IO.File]::ReadAllBytes($exePath)
foreach ($site in $sites) {
    Assert-BinaryContext $writtenBytes $site
    if ($writtenBytes[$site.Offset] -ne $site.Desired) { throw "Post-write verification failed at $($site.Name)." }
}
foreach ($locale in @('en_us', 'zh_cn')) {
    [void](Patch-EquipForgeText ([IO.File]::ReadAllText($equipPaths[$locale], [Text.Encoding]::UTF8)) $locale)
    [void](Patch-BijouForgeText ([IO.File]::ReadAllText($bijouPaths[$locale], [Text.Encoding]::UTF8)) $locale)
    [void](Patch-ItemBaseText ([IO.File]::ReadAllText($itemPaths[$locale], [Text.Encoding]::UTF8)) (Get-ForgeIds ([IO.File]::ReadAllText($equipPaths[$locale], [Text.Encoding]::UTF8))) $locale)
}

[pscustomobject]@{
    BinaryBytesChanged = $binaryChanges
    TextFilesChanged = $textChanges
    BackupPath = $backupPath
    OriginSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash
    EnForgeRows = $equipResults['en_us'].Rows
    ZhForgeRows = $equipResults['zh_cn'].Rows
    EnBijouRows = $bijouResults['en_us'].Rows
    ZhBijouRows = $bijouResults['zh_cn'].Rows
    EnItemRows = $itemResults['en_us'].Rows
    ZhItemRows = $itemResults['zh_cn'].Rows
}
