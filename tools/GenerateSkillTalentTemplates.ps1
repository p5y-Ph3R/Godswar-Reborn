param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\SkillTalentSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\006_skills_and_talents.sql"
)

$ErrorActionPreference = "Stop"

$skillIniPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Skill.ini"
$itemBasePath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\ItemBaseAttribute.xml"
$equipNamePath = Join-Path $ClientRoot "Localization\en_us\Text\EquipName.dat"
$skillInfoPath = Join-Path $ClientRoot "Localization\en_us\Text\SkillInfo.dat"

foreach ($path in @($skillIniPath, $itemBasePath, $equipNamePath, $skillInfoPath)) {
    if (-not (Test-Path $path)) {
        throw "Required client file not found: $path"
    }
}

$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

function ConvertTo-CSharpString([string]$value) {
    if ($null -eq $value) {
        return 'string.Empty'
    }

    return '"' + $value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function ConvertTo-SqlString([string]$value) {
    if ($null -eq $value) {
        return 'NULL'
    }

    return "'" + $value.Replace("'", "''") + "'"
}

function ConvertTo-CSharpShortArray([Int16[]]$values) {
    if ($values.Count -eq 0) {
        return "[]"
    }

    return "new short[] { " + (($values | ForEach-Object { $_.ToString($invariantCulture) }) -join ", ") + " }"
}

function ConvertTo-SqlSmallintArray([Int16[]]$values) {
    if ($values.Count -eq 0) {
        return "ARRAY[]::smallint[]"
    }

    return "ARRAY[" + (($values | ForEach-Object { $_.ToString($invariantCulture) }) -join ",") + "]::smallint[]"
}

function ConvertTo-CSharpNullableShort($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "null"
    }

    return "(short)$(([Int16]$value).ToString($invariantCulture))"
}

function ConvertTo-CSharpNullableInt($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "null"
    }

    return ([Int32]$value).ToString($invariantCulture)
}

function ConvertTo-SqlNullableSmallint($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "NULL"
    }

    return ([Int16]$value).ToString($invariantCulture)
}

function ConvertTo-SqlNullableInt($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "NULL"
    }

    return ([Int32]$value).ToString($invariantCulture)
}

function ConvertTo-CSharpDecimal([decimal]$value) {
    return $value.ToString($invariantCulture) + "m"
}

function ConvertTo-SqlNumeric([decimal]$value) {
    return $value.ToString($invariantCulture)
}

function Get-AttributeValue($attributes, [string]$name) {
    if ($attributes.Contains($name)) {
        return $attributes[$name]
    }

    return $null
}

function ConvertTo-IntegerPair([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @(0, 0)
    }

    $parts = $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) {
        return @([int]$parts[0], 0)
    }

    return @([int]$parts[0].Trim(), [int]$parts[1].Trim())
}

function ConvertTo-EffectPair([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @{
            Id = 0
            Value = [decimal]0
        }
    }

    $parts = $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    $effectValue = if ($parts.Count -gt 1) {
        [decimal]::Parse($parts[1].Trim(), $invariantCulture)
    } else {
        [decimal]0
    }

    return @{
        Id = [int]$parts[0].Trim()
        Value = $effectValue
    }
}

function ConvertTo-ClassIds([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return [Int16[]]@()
    }

    return [Int16[]]@(
        $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { [Int16]$_.Trim() }
    )
}

function ConvertTo-LevelRange([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @{
            Min = $null
            Max = $null
        }
    }

    $parts = $value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    return @{
        Min = if ($parts.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($parts[0])) { [Nullable[Int32]][int]$parts[0].Trim() } else { $null }
        Max = if ($parts.Count -gt 1 -and -not [string]::IsNullOrWhiteSpace($parts[1])) { [Nullable[Int32]][int]$parts[1].Trim() } else { $null }
    }
}

function ConvertTo-SkillNames([string]$displayName) {
    if ([string]::IsNullOrWhiteSpace($displayName)) {
        return @{
            Display = ""
            Base = ""
            Level = $null
        }
    }

    $clean = $displayName.Trim()
    $clean = $clean -replace '^Book:\s*', ''
    $clean = $clean -replace '^Read it to learn\s+', ''

    $level = $null
    $base = $clean
    if ($clean -match '^(.+?)\s+(\d+)$') {
        $base = $Matches[1].Trim()
        $level = [Nullable[Int16]][Int16]$Matches[2]
    }

    return @{
        Display = $clean
        Base = $base
        Level = $level
    }
}

$classes = @(
    [pscustomobject]@{ Id = [Int16]0; Name = "warrior"; DisplayName = "Warrior"; Source = "Skill.ini class0 / Message.dat Warriorbuild" },
    [pscustomobject]@{ Id = [Int16]1; Name = "champion"; DisplayName = "Champion"; Source = "Skill.ini class1 / Message.dat Spearmanbuild" },
    [pscustomobject]@{ Id = [Int16]2; Name = "priest"; DisplayName = "Priest"; Source = "Skill.ini class2 / Message.dat Flamenbuild" },
    [pscustomobject]@{ Id = [Int16]3; Name = "mage"; DisplayName = "Mage"; Source = "Skill.ini class3 / Message.dat Magebuild" }
)

$sections = [ordered]@{}
$currentSection = $null
foreach ($line in Get-Content $skillIniPath) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith(";") -or $trimmed.StartsWith("#")) {
        continue
    }

    if ($trimmed -match '^\[(.+)\]$') {
        $currentSection = $Matches[1]
        $sections[$currentSection] = [ordered]@{}
        continue
    }

    if ($currentSection -and $trimmed -match '^([^=]+)=(.*)$') {
        $sections[$currentSection][$Matches[1].Trim()] = $Matches[2].Trim()
    }
}

$percentEffectIds = [System.Collections.Generic.HashSet[int]]::new()
$percentText = Get-AttributeValue $sections["parameter"] "Percent"
if (-not [string]::IsNullOrWhiteSpace($percentText)) {
    $percentText.TrimEnd(";").Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { [void]$percentEffectIds.Add([int]$_.Trim()) }
}

$effectNameById = @{}
$effectIdByName = @{}
foreach ($key in $sections["Effect"].Keys) {
    if ($key -match '^Effect(\d+)$') {
        $effectId = [int]$Matches[1]
        $effectName = $sections["Effect"][$key]
        $effectNameById[$effectId] = $effectName
        $effectIdByName[$effectName] = $effectId
    }
}

$talentEffects = @(
    foreach ($effectId in ($effectNameById.Keys | Sort-Object)) {
        [pscustomobject]@{
            Id = [Int16]$effectId
            Key = $effectNameById[$effectId]
            DisplayName = if ($sections["NODE"].Contains([string]$effectId)) { $sections["NODE"][[string]$effectId] } else { $effectNameById[$effectId] }
            Percent = $percentEffectIds.Contains($effectId)
        }
    }
)

$talentClassOrder = @{}
foreach ($classId in 0..3) {
    $list = @(($sections["class$classId"]["Skill"]).TrimEnd(";").Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [int]$_.Trim() })
    for ($index = 0; $index -lt $list.Count; $index++) {
        $talentClassOrder[$list[$index]] = @{
            ClassId = [Int16]$classId
            TreeOrder = [Int16]$index
        }
    }
}

$talents = @(
    foreach ($sectionName in ($sections.Keys | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ } | Sort-Object)) {
        $section = $sections[[string]$sectionName]
        if (-not $talentClassOrder.ContainsKey($sectionName)) {
            continue
        }

        $effectKey = @($section.Keys | Where-Object { $effectIdByName.ContainsKey($_) })[0]
        $effectPair = ConvertTo-EffectPair $section[$effectKey]
        $iconPos = ConvertTo-IntegerPair $section["IconPos"]
        $iconSize = ConvertTo-IntegerPair $section["IconSize"]
        $classOrder = $talentClassOrder[$sectionName]

        [pscustomobject]@{
            Id = [int]$sectionName
            ClassId = [Int16]$classOrder.ClassId
            TreeOrder = [Int16]$classOrder.TreeOrder
            Name = $section["Name"]
            PrefixId = [int]$section["PrefixID"]
            RequiredPrefixRank = [int]$section["RrefixRank"]
            RequiredTotalRank = [int]$section["TotalRank"]
            EquipRequest = [int]$section["EquipRequest"]
            EffectType = $effectKey
            EffectId = [Int16]$effectPair.Id
            EffectValue = $effectPair.Value
            IsPercent = $percentEffectIds.Contains($effectPair.Id)
            IconX = [int]$iconPos[0]
            IconY = [int]$iconPos[1]
            IconWidth = [int]$iconSize[0]
            IconHeight = [int]$iconSize[1]
            StatsJson = ($section | ConvertTo-Json -Compress)
        }
    }
) | Sort-Object ClassId, TreeOrder

$displayNames = @{}
foreach ($line in Get-Content $equipNamePath) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("//")) {
        continue
    }

    $parts = $line -split "`t", 2
    if ($parts.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($parts[0])) {
        $displayNames[$parts[0]] = $parts[1].Trim()
    }
}

$skillDescriptions = @{}
foreach ($line in Get-Content $skillInfoPath) {
    if ($line -match '^(\d+)\t(.*)$') {
        $skillDescriptions[[int]$Matches[1]] = $Matches[2]
    }
}

[xml]$itemBase = Get-Content $itemBasePath -Raw
$skillBooks = @(
    foreach ($node in $itemBase.SelectNodes('/ItemBaseAttribute//*[@SkillID]')) {
        $attributes = [ordered]@{}
        foreach ($attribute in $node.Attributes) {
            $attributes[$attribute.Name] = $attribute.Value
        }

        $displayName = if ($displayNames.ContainsKey($node.Name)) { $displayNames[$node.Name] } else { $node.Name }
        $names = ConvertTo-SkillNames $displayName
        $range = ConvertTo-LevelRange (Get-AttributeValue $attributes "PlayLv")
        $previousSkillId = [int](Get-AttributeValue $attributes "PrevSkillID")

        [pscustomobject]@{
            ItemId = [int](Get-AttributeValue $attributes "ID")
            NameKey = $node.Name
            DisplayName = $displayName
            SkillId = [int](Get-AttributeValue $attributes "SkillID")
            BaseName = $names.Base
            SkillLevel = $names.Level
            ClassIds = ConvertTo-ClassIds (Get-AttributeValue $attributes "Class")
            MinLevel = $range.Min
            MaxLevel = $range.Max
            PreviousSkillId = if ($previousSkillId -lt 0) { $null } else { [Nullable[Int32]]$previousSkillId }
            StatsJson = ($attributes | ConvertTo-Json -Compress)
        }
    }
) | Sort-Object SkillId, ItemId

$skills = @(
    foreach ($group in ($skillBooks | Group-Object SkillId | Sort-Object { [int]$_.Name })) {
        $books = @($group.Group | Sort-Object ItemId)
        $first = $books[0]
        $classIds = [Int16[]]@($books | ForEach-Object { $_.ClassIds } | Sort-Object -Unique)
        $bookItemIds = [int[]]@($books | ForEach-Object { $_.ItemId } | Sort-Object -Unique)
        $nameKeys = [string[]]@($books | ForEach-Object { $_.NameKey } | Sort-Object -Unique)
        $skillId = [int]$group.Name
        $stats = [ordered]@{
            Source = "ItemBaseAttribute.SkillBook+SkillInfo.dat"
            BookItemIds = $bookItemIds
            NameKeys = $nameKeys
        }

        [pscustomobject]@{
            SkillId = $skillId
            DisplayName = $first.DisplayName
            BaseName = $first.BaseName
            SkillLevel = $first.SkillLevel
            ClassIds = $classIds
            PreviousSkillId = $first.PreviousSkillId
            MinLevel = ($books | Where-Object { $null -ne $_.MinLevel } | Measure-Object -Property MinLevel -Minimum).Minimum
            MaxLevel = ($books | Where-Object { $null -ne $_.MaxLevel } | Measure-Object -Property MaxLevel -Maximum).Maximum
            Description = if ($skillDescriptions.ContainsKey($skillId)) { $skillDescriptions[$skillId] } else { "" }
            StatsJson = ($stats | ConvertTo-Json -Compress)
        }
    }
)

$csharp = [System.Text.StringBuilder]::new()
[void]$csharp.AppendLine("// <auto-generated />")
[void]$csharp.AppendLine("// Generated from Godswar Origin Skill.ini, ItemBaseAttribute.xml, EquipName.dat, and SkillInfo.dat.")
[void]$csharp.AppendLine("namespace Godswar.Server.State;")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct ClassTemplateSeed(short Id, string Name, string DisplayName, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct TalentEffectTemplateSeed(short Id, string Key, string DisplayName, bool Percent);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct TalentTemplateSeed(")
[void]$csharp.AppendLine("    int Id,")
[void]$csharp.AppendLine("    short ClassId,")
[void]$csharp.AppendLine("    short TreeOrder,")
[void]$csharp.AppendLine("    string Name,")
[void]$csharp.AppendLine("    int PrefixId,")
[void]$csharp.AppendLine("    int RequiredPrefixRank,")
[void]$csharp.AppendLine("    int RequiredTotalRank,")
[void]$csharp.AppendLine("    int EquipRequest,")
[void]$csharp.AppendLine("    string EffectType,")
[void]$csharp.AppendLine("    short EffectId,")
[void]$csharp.AppendLine("    decimal EffectValue,")
[void]$csharp.AppendLine("    bool IsPercent,")
[void]$csharp.AppendLine("    int IconX,")
[void]$csharp.AppendLine("    int IconY,")
[void]$csharp.AppendLine("    int IconWidth,")
[void]$csharp.AppendLine("    int IconHeight,")
[void]$csharp.AppendLine("    string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct SkillTemplateSeed(")
[void]$csharp.AppendLine("    int SkillId,")
[void]$csharp.AppendLine("    string DisplayName,")
[void]$csharp.AppendLine("    string BaseName,")
[void]$csharp.AppendLine("    short? SkillLevel,")
[void]$csharp.AppendLine("    short[] ClassIds,")
[void]$csharp.AppendLine("    int? PreviousSkillId,")
[void]$csharp.AppendLine("    int? MinLevel,")
[void]$csharp.AppendLine("    int? MaxLevel,")
[void]$csharp.AppendLine("    string Description,")
[void]$csharp.AppendLine("    string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct SkillBookTemplateSeed(")
[void]$csharp.AppendLine("    int ItemId,")
[void]$csharp.AppendLine("    string NameKey,")
[void]$csharp.AppendLine("    string DisplayName,")
[void]$csharp.AppendLine("    int SkillId,")
[void]$csharp.AppendLine("    string BaseName,")
[void]$csharp.AppendLine("    short? SkillLevel,")
[void]$csharp.AppendLine("    short[] ClassIds,")
[void]$csharp.AppendLine("    int? MinLevel,")
[void]$csharp.AppendLine("    int? MaxLevel,")
[void]$csharp.AppendLine("    int? PreviousSkillId,")
[void]$csharp.AppendLine("    string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal static class SkillTalentSeeds")
[void]$csharp.AppendLine("{")
[void]$csharp.AppendLine("    public static IReadOnlyList<ClassTemplateSeed> Classes { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($class in $classes) {
    [void]$csharp.AppendLine("        new($($class.Id), $(ConvertTo-CSharpString $class.Name), $(ConvertTo-CSharpString $class.DisplayName), $(ConvertTo-CSharpString $class.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<TalentEffectTemplateSeed> TalentEffects { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($effect in $talentEffects) {
    [void]$csharp.AppendLine("        new($($effect.Id), $(ConvertTo-CSharpString $effect.Key), $(ConvertTo-CSharpString $effect.DisplayName), $($effect.Percent.ToString().ToLowerInvariant())),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<TalentTemplateSeed> Talents { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($talent in $talents) {
    [void]$csharp.AppendLine(
        "        new(" +
        "$($talent.Id), $($talent.ClassId), $($talent.TreeOrder), " +
        "$(ConvertTo-CSharpString $talent.Name), $($talent.PrefixId), $($talent.RequiredPrefixRank), $($talent.RequiredTotalRank), $($talent.EquipRequest), " +
        "$(ConvertTo-CSharpString $talent.EffectType), $($talent.EffectId), $(ConvertTo-CSharpDecimal $talent.EffectValue), $($talent.IsPercent.ToString().ToLowerInvariant()), " +
        "$($talent.IconX), $($talent.IconY), $($talent.IconWidth), $($talent.IconHeight), $(ConvertTo-CSharpString $talent.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<SkillTemplateSeed> Skills { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($skill in $skills) {
    [void]$csharp.AppendLine(
        "        new(" +
        "$($skill.SkillId), $(ConvertTo-CSharpString $skill.DisplayName), $(ConvertTo-CSharpString $skill.BaseName), $(ConvertTo-CSharpNullableShort $skill.SkillLevel), " +
        "$(ConvertTo-CSharpShortArray $skill.ClassIds), $(ConvertTo-CSharpNullableInt $skill.PreviousSkillId), $(ConvertTo-CSharpNullableInt $skill.MinLevel), $(ConvertTo-CSharpNullableInt $skill.MaxLevel), " +
        "$(ConvertTo-CSharpString $skill.Description), $(ConvertTo-CSharpString $skill.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<SkillBookTemplateSeed> SkillBooks { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($book in $skillBooks) {
    [void]$csharp.AppendLine(
        "        new(" +
        "$($book.ItemId), $(ConvertTo-CSharpString $book.NameKey), $(ConvertTo-CSharpString $book.DisplayName), $($book.SkillId), " +
        "$(ConvertTo-CSharpString $book.BaseName), $(ConvertTo-CSharpNullableShort $book.SkillLevel), $(ConvertTo-CSharpShortArray $book.ClassIds), " +
        "$(ConvertTo-CSharpNullableInt $book.MinLevel), $(ConvertTo-CSharpNullableInt $book.MaxLevel), $(ConvertTo-CSharpNullableInt $book.PreviousSkillId), $(ConvertTo-CSharpString $book.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine("}")

$sql = [System.Text.StringBuilder]::new()
[void]$sql.AppendLine("-- <auto-generated />")
[void]$sql.AppendLine("-- Generated from Godswar Origin Skill.ini, ItemBaseAttribute.xml, EquipName.dat, and SkillInfo.dat.")
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS class_templates (")
[void]$sql.AppendLine("    id smallint PRIMARY KEY,")
[void]$sql.AppendLine("    name varchar(32) NOT NULL UNIQUE,")
[void]$sql.AppendLine("    display_name varchar(64) NOT NULL,")
[void]$sql.AppendLine("    source varchar(128) NOT NULL DEFAULT ''")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO class_templates (id, name, display_name, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $classes.Count; $i++) {
    $class = $classes[$i]
    $suffix = if ($i -eq $classes.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($class.Id), $(ConvertTo-SqlString $class.Name), $(ConvertTo-SqlString $class.DisplayName), $(ConvertTo-SqlString $class.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (id) DO UPDATE")
[void]$sql.AppendLine("SET name = EXCLUDED.name,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS talent_effect_templates (")
[void]$sql.AppendLine("    id smallint PRIMARY KEY,")
[void]$sql.AppendLine("    key varchar(32) NOT NULL UNIQUE,")
[void]$sql.AppendLine("    display_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    percent boolean NOT NULL DEFAULT false")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO talent_effect_templates (id, key, display_name, percent)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $talentEffects.Count; $i++) {
    $effect = $talentEffects[$i]
    $suffix = if ($i -eq $talentEffects.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($effect.Id), $(ConvertTo-SqlString $effect.Key), $(ConvertTo-SqlString $effect.DisplayName), $($effect.Percent.ToString().ToLowerInvariant()))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (id) DO UPDATE")
[void]$sql.AppendLine("SET key = EXCLUDED.key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    percent = EXCLUDED.percent;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS talent_templates (")
[void]$sql.AppendLine("    id integer PRIMARY KEY,")
[void]$sql.AppendLine("    class_id smallint NOT NULL REFERENCES class_templates(id),")
[void]$sql.AppendLine("    tree_order smallint NOT NULL,")
[void]$sql.AppendLine("    name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    prefix_id integer NOT NULL,")
[void]$sql.AppendLine("    required_prefix_rank integer NOT NULL,")
[void]$sql.AppendLine("    required_total_rank integer NOT NULL,")
[void]$sql.AppendLine("    equip_request integer NOT NULL,")
[void]$sql.AppendLine("    effect_type varchar(32) NOT NULL,")
[void]$sql.AppendLine("    effect_id smallint NOT NULL REFERENCES talent_effect_templates(id),")
[void]$sql.AppendLine("    effect_value numeric NOT NULL,")
[void]$sql.AppendLine("    is_percent boolean NOT NULL DEFAULT false,")
[void]$sql.AppendLine("    icon_x integer NOT NULL,")
[void]$sql.AppendLine("    icon_y integer NOT NULL,")
[void]$sql.AppendLine("    icon_width integer NOT NULL,")
[void]$sql.AppendLine("    icon_height integer NOT NULL,")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_talent_templates_class ON talent_templates (class_id, tree_order);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO talent_templates (id, class_id, tree_order, name, prefix_id, required_prefix_rank, required_total_rank, equip_request, effect_type, effect_id, effect_value, is_percent, icon_x, icon_y, icon_width, icon_height, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $talents.Count; $i++) {
    $talent = $talents[$i]
    $suffix = if ($i -eq $talents.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine(
        "    (" +
        "$($talent.Id), $($talent.ClassId), $($talent.TreeOrder), $(ConvertTo-SqlString $talent.Name), " +
        "$($talent.PrefixId), $($talent.RequiredPrefixRank), $($talent.RequiredTotalRank), $($talent.EquipRequest), " +
        "$(ConvertTo-SqlString $talent.EffectType), $($talent.EffectId), $(ConvertTo-SqlNumeric $talent.EffectValue), $($talent.IsPercent.ToString().ToLowerInvariant()), " +
        "$($talent.IconX), $($talent.IconY), $($talent.IconWidth), $($talent.IconHeight), $(ConvertTo-SqlString $talent.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (id) DO UPDATE")
[void]$sql.AppendLine("SET class_id = EXCLUDED.class_id,")
[void]$sql.AppendLine("    tree_order = EXCLUDED.tree_order,")
[void]$sql.AppendLine("    name = EXCLUDED.name,")
[void]$sql.AppendLine("    prefix_id = EXCLUDED.prefix_id,")
[void]$sql.AppendLine("    required_prefix_rank = EXCLUDED.required_prefix_rank,")
[void]$sql.AppendLine("    required_total_rank = EXCLUDED.required_total_rank,")
[void]$sql.AppendLine("    equip_request = EXCLUDED.equip_request,")
[void]$sql.AppendLine("    effect_type = EXCLUDED.effect_type,")
[void]$sql.AppendLine("    effect_id = EXCLUDED.effect_id,")
[void]$sql.AppendLine("    effect_value = EXCLUDED.effect_value,")
[void]$sql.AppendLine("    is_percent = EXCLUDED.is_percent,")
[void]$sql.AppendLine("    icon_x = EXCLUDED.icon_x,")
[void]$sql.AppendLine("    icon_y = EXCLUDED.icon_y,")
[void]$sql.AppendLine("    icon_width = EXCLUDED.icon_width,")
[void]$sql.AppendLine("    icon_height = EXCLUDED.icon_height,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS skill_templates (")
[void]$sql.AppendLine("    skill_id integer PRIMARY KEY,")
[void]$sql.AppendLine("    display_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    base_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    skill_level smallint,")
[void]$sql.AppendLine("    class_ids smallint[] NOT NULL DEFAULT '{}',")
[void]$sql.AppendLine("    previous_skill_id integer,")
[void]$sql.AppendLine("    min_level integer,")
[void]$sql.AppendLine("    max_level integer,")
[void]$sql.AppendLine("    description text NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_templates_class_ids ON skill_templates USING gin (class_ids);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_templates_base_name ON skill_templates (base_name);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO skill_templates (skill_id, display_name, base_name, skill_level, class_ids, previous_skill_id, min_level, max_level, description, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $skills.Count; $i++) {
    $skill = $skills[$i]
    $suffix = if ($i -eq $skills.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine(
        "    (" +
        "$($skill.SkillId), $(ConvertTo-SqlString $skill.DisplayName), $(ConvertTo-SqlString $skill.BaseName), $(ConvertTo-SqlNullableSmallint $skill.SkillLevel), " +
        "$(ConvertTo-SqlSmallintArray $skill.ClassIds), $(ConvertTo-SqlNullableInt $skill.PreviousSkillId), $(ConvertTo-SqlNullableInt $skill.MinLevel), $(ConvertTo-SqlNullableInt $skill.MaxLevel), " +
        "$(ConvertTo-SqlString $skill.Description), $(ConvertTo-SqlString $skill.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (skill_id) DO UPDATE")
[void]$sql.AppendLine("SET display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    base_name = EXCLUDED.base_name,")
[void]$sql.AppendLine("    skill_level = EXCLUDED.skill_level,")
[void]$sql.AppendLine("    class_ids = EXCLUDED.class_ids,")
[void]$sql.AppendLine("    previous_skill_id = EXCLUDED.previous_skill_id,")
[void]$sql.AppendLine("    min_level = EXCLUDED.min_level,")
[void]$sql.AppendLine("    max_level = EXCLUDED.max_level,")
[void]$sql.AppendLine("    description = EXCLUDED.description,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS skill_book_templates (")
[void]$sql.AppendLine("    item_id integer PRIMARY KEY,")
[void]$sql.AppendLine("    name_key varchar(128) NOT NULL,")
[void]$sql.AppendLine("    display_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    skill_id integer NOT NULL REFERENCES skill_templates(skill_id),")
[void]$sql.AppendLine("    base_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    skill_level smallint,")
[void]$sql.AppendLine("    class_ids smallint[] NOT NULL DEFAULT '{}',")
[void]$sql.AppendLine("    min_level integer,")
[void]$sql.AppendLine("    max_level integer,")
[void]$sql.AppendLine("    previous_skill_id integer,")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_book_templates_skill_id ON skill_book_templates (skill_id);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_book_templates_class_ids ON skill_book_templates USING gin (class_ids);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO skill_book_templates (item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids, min_level, max_level, previous_skill_id, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $skillBooks.Count; $i++) {
    $book = $skillBooks[$i]
    $suffix = if ($i -eq $skillBooks.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine(
        "    (" +
        "$($book.ItemId), $(ConvertTo-SqlString $book.NameKey), $(ConvertTo-SqlString $book.DisplayName), $($book.SkillId), $(ConvertTo-SqlString $book.BaseName), " +
        "$(ConvertTo-SqlNullableSmallint $book.SkillLevel), $(ConvertTo-SqlSmallintArray $book.ClassIds), $(ConvertTo-SqlNullableInt $book.MinLevel), $(ConvertTo-SqlNullableInt $book.MaxLevel), " +
        "$(ConvertTo-SqlNullableInt $book.PreviousSkillId), $(ConvertTo-SqlString $book.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (item_id) DO UPDATE")
[void]$sql.AppendLine("SET name_key = EXCLUDED.name_key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    skill_id = EXCLUDED.skill_id,")
[void]$sql.AppendLine("    base_name = EXCLUDED.base_name,")
[void]$sql.AppendLine("    skill_level = EXCLUDED.skill_level,")
[void]$sql.AppendLine("    class_ids = EXCLUDED.class_ids,")
[void]$sql.AppendLine("    min_level = EXCLUDED.min_level,")
[void]$sql.AppendLine("    max_level = EXCLUDED.max_level,")
[void]$sql.AppendLine("    previous_skill_id = EXCLUDED.previous_skill_id,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS character_skills (")
[void]$sql.AppendLine("    user_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    skill_id integer NOT NULL REFERENCES skill_templates(skill_id),")
[void]$sql.AppendLine("    skill_level smallint NOT NULL DEFAULT 1,")
[void]$sql.AppendLine("    acquired_at timestamptz NOT NULL DEFAULT now(),")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT 'manual',")
[void]$sql.AppendLine("    PRIMARY KEY (user_id, skill_id)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_character_skills_skill_id ON character_skills (skill_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS character_talents (")
[void]$sql.AppendLine("    user_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    talent_id integer NOT NULL REFERENCES talent_templates(id),")
[void]$sql.AppendLine("    rank smallint NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    updated_at timestamptz NOT NULL DEFAULT now(),")
[void]$sql.AppendLine("    PRIMARY KEY (user_id, talent_id)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_character_talents_talent_id ON character_talents (talent_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW class_talents AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    ct.id AS class_id,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    tt.tree_order,")
[void]$sql.AppendLine("    tt.id AS talent_id,")
[void]$sql.AppendLine("    tt.name,")
[void]$sql.AppendLine("    tt.prefix_id,")
[void]$sql.AppendLine("    tt.required_prefix_rank,")
[void]$sql.AppendLine("    tt.required_total_rank,")
[void]$sql.AppendLine("    tt.equip_request,")
[void]$sql.AppendLine("    tt.effect_type,")
[void]$sql.AppendLine("    tet.display_name AS effect_name,")
[void]$sql.AppendLine("    tt.effect_value,")
[void]$sql.AppendLine("    tt.is_percent")
[void]$sql.AppendLine("FROM talent_templates tt")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = tt.class_id")
[void]$sql.AppendLine("JOIN talent_effect_templates tet ON tet.id = tt.effect_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW class_skills AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    ct.id AS class_id,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    st.skill_id,")
[void]$sql.AppendLine("    st.display_name,")
[void]$sql.AppendLine("    st.base_name,")
[void]$sql.AppendLine("    st.skill_level,")
[void]$sql.AppendLine("    st.previous_skill_id,")
[void]$sql.AppendLine("    st.min_level,")
[void]$sql.AppendLine("    st.max_level,")
[void]$sql.AppendLine("    st.description")
[void]$sql.AppendLine("FROM skill_templates st")
[void]$sql.AppendLine("CROSS JOIN LATERAL unnest(st.class_ids) AS skill_class(class_id)")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = skill_class.class_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW class_skill_books AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    ct.id AS class_id,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    sbt.item_id,")
[void]$sql.AppendLine("    sbt.name_key,")
[void]$sql.AppendLine("    sbt.display_name,")
[void]$sql.AppendLine("    sbt.skill_id,")
[void]$sql.AppendLine("    sbt.base_name,")
[void]$sql.AppendLine("    sbt.skill_level,")
[void]$sql.AppendLine("    sbt.min_level,")
[void]$sql.AppendLine("    sbt.max_level,")
[void]$sql.AppendLine("    sbt.previous_skill_id")
[void]$sql.AppendLine("FROM skill_book_templates sbt")
[void]$sql.AppendLine("CROSS JOIN LATERAL unnest(sbt.class_ids) AS book_class(class_id)")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = book_class.class_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW character_available_talents AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    cb.id AS user_id,")
[void]$sql.AppendLine("    cb.name AS character_name,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    tt.tree_order,")
[void]$sql.AppendLine("    tt.id AS talent_id,")
[void]$sql.AppendLine("    tt.name,")
[void]$sql.AppendLine("    COALESCE(chtt.rank, 0)::smallint AS current_rank,")
[void]$sql.AppendLine("    tt.required_prefix_rank,")
[void]$sql.AppendLine("    tt.required_total_rank,")
[void]$sql.AppendLine("    tt.effect_type,")
[void]$sql.AppendLine("    tt.effect_value,")
[void]$sql.AppendLine("    tt.is_percent")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = cb.profession")
[void]$sql.AppendLine("JOIN talent_templates tt ON tt.class_id = cb.profession")
[void]$sql.AppendLine("LEFT JOIN character_talents chtt ON chtt.user_id = cb.id AND chtt.talent_id = tt.id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW character_available_skills AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    cb.id AS user_id,")
[void]$sql.AppendLine("    cb.name AS character_name,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    st.skill_id,")
[void]$sql.AppendLine("    st.display_name,")
[void]$sql.AppendLine("    st.base_name,")
[void]$sql.AppendLine("    st.skill_level,")
[void]$sql.AppendLine("    st.previous_skill_id,")
[void]$sql.AppendLine("    st.min_level,")
[void]$sql.AppendLine("    cb.fighter_job_lv AS character_level,")
[void]$sql.AppendLine("    (COALESCE(st.min_level, 1) <= cb.fighter_job_lv) AS level_unlocked,")
[void]$sql.AppendLine("    (chs.skill_id IS NOT NULL) AS learned,")
[void]$sql.AppendLine("    chs.source AS learned_source")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = cb.profession")
[void]$sql.AppendLine("JOIN skill_templates st ON cb.profession = ANY(st.class_ids)")
[void]$sql.AppendLine("LEFT JOIN character_skills chs ON chs.user_id = cb.id AND chs.skill_id = st.skill_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO character_skills (user_id, skill_id, skill_level, source)")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    cb.id,")
[void]$sql.AppendLine("    st.skill_id,")
[void]$sql.AppendLine("    st.skill_level,")
[void]$sql.AppendLine("    'starter'")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN skill_templates st ON cb.profession = ANY(st.class_ids)")
[void]$sql.AppendLine("WHERE st.previous_skill_id IS NULL")
[void]$sql.AppendLine("  AND COALESCE(st.min_level, 1) <= cb.fighter_job_lv")
[void]$sql.AppendLine("  AND st.skill_level = 1")
[void]$sql.AppendLine("ON CONFLICT (user_id, skill_id) DO NOTHING;")

[System.IO.File]::WriteAllText($CSharpOutputPath, $csharp.ToString(), [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($SqlOutputPath, $sql.ToString(), [System.Text.Encoding]::UTF8)

Write-Host "Generated $($classes.Count) classes, $($talentEffects.Count) talent effects, $($talents.Count) talents, $($skills.Count) skills, and $($skillBooks.Count) skill books."
Write-Host "C#:  $CSharpOutputPath"
Write-Host "SQL: $SqlOutputPath"
