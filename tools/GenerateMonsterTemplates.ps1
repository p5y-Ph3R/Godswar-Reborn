param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\MonsterTemplateSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\009_monsters.sql"
)

$ErrorActionPreference = "Stop"

$monsterConfigPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\MonsterConfig.ini"
$questMonsterPath = Join-Path $ClientRoot "Localization\en_us\Text\QuestMonster.dat"
$questPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Quest.xml"

foreach ($path in @($monsterConfigPath, $questMonsterPath, $questPath)) {
    if (-not (Test-Path $path)) {
        throw "Required client file not found: $path"
    }
}

$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

function ConvertTo-CSharpString([string]$value) {
    if ($null -eq $value) {
        return 'string.Empty'
    }

    return '"' + $value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function ConvertTo-SqlString([string]$value) {
    if ($null -eq $value) {
        return 'NULL'
    }

    return "'" + $value.Replace("'", "''") + "'"
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

function ConvertTo-CSharpNullableFloat($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "null"
    }

    return ([float]$value).ToString($invariantCulture) + "f"
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

function ConvertTo-SqlNullableReal($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        return "NULL"
    }

    return ([float]$value).ToString($invariantCulture)
}

function Read-IniFile([string]$path) {
    $sections = [ordered]@{}
    $currentSection = ""
    $sections[$currentSection] = [ordered]@{}

    foreach ($line in Get-Content $path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith(";") -or $trimmed.StartsWith("#")) {
            continue
        }

        if ($trimmed -match '^\[(?<name>[^\]]+)\]') {
            $currentSection = $Matches.name.Trim()
            if (-not $sections.Contains($currentSection)) {
                $sections[$currentSection] = [ordered]@{}
            }

            continue
        }

        if ($trimmed -match '^(?<key>[^=]+)=(?<value>.*)$') {
            $sections[$currentSection][$Matches.key.Trim()] = $Matches.value.Trim().TrimEnd(";")
        }
    }

    return $sections
}

function Read-TabFile([string]$path) {
    $values = @{}
    foreach ($line in Get-Content $path) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("//")) {
            continue
        }

        $parts = $line -split "`t", 2
        if ($parts.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($parts[0])) {
            $values[[int]$parts[0].Trim()] = $parts[1].Trim()
        }
    }

    return $values
}

function Resolve-ClientPath([string]$path) {
    $normalized = $path.Trim().Replace('/', '\')
    if ($normalized.StartsWith(".\")) {
        $normalized = $normalized.Substring(2)
    }

    return Join-Path $ClientRoot $normalized
}

function Get-AttributeValue($attributes, [string]$name) {
    if ($attributes.Contains($name)) {
        return $attributes[$name]
    }

    return $null
}

function ConvertTo-Position([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value -eq "1,1") {
        return $null
    }

    $parts = $value.Replace("f", "").Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) {
        return $null
    }

    return @{
        X = [float]::Parse($parts[0].Trim(), $invariantCulture)
        Z = [float]::Parse($parts[1].Trim(), $invariantCulture)
    }
}

function Get-Rank([string]$templateKey, [string]$displayName) {
    if ($templateKey -match 'boss' -or $displayName -match '\[BOSS\]' -or $displayName -match 'Boss') {
        return "boss"
    }

    if ($templateKey -match 'elite' -or $displayName -match '\[Elite\]' -or $displayName -match '\[ELITE\]') {
        return "elite"
    }

    if ($displayName -match '\[Pet\]') {
        return "pet"
    }

    return "normal"
}

function Get-FloatValue($attributes, [string]$name) {
    $value = Get-AttributeValue $attributes $name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return [Nullable[Single]][float]::Parse($value, $invariantCulture)
}

function Get-ShortValue($attributes, [string]$name) {
    $value = Get-AttributeValue $attributes $name
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return [Nullable[Int16]][Int16]$value
}

$monsterConfig = Read-IniFile $monsterConfigPath
$monsterSources = @()
foreach ($key in $monsterConfig["config"].Keys) {
    if ($key -match '^MonsterCon(?<id>\d+)$') {
        $mapId = [int]$Matches.id
        $path = $monsterConfig["config"][$key]
        $monsterSources += [pscustomobject]@{
            MapId = $mapId
            SourceKey = if ($mapId -eq 500) { "global" } else { "map:$mapId" }
            SourceKind = if ($mapId -eq 500) { "global" } else { "map" }
            RelativePath = $path
            FullPath = Resolve-ClientPath $path
            SceneKey = Split-Path (Split-Path $path -Parent) -Leaf
        }
    }
}

$monsterSources = @($monsterSources | Sort-Object MapId)
$monsters = @()

foreach ($source in $monsterSources) {
    if (-not (Test-Path $source.FullPath)) {
        continue
    }

    $sections = Read-IniFile $source.FullPath
    foreach ($templateKey in $sections.Keys) {
        if ($templateKey -eq "" -or $templateKey -eq "default") {
            continue
        }

        $section = $sections[$templateKey]
        $displayName = Get-AttributeValue $section "Name"
        $rank = Get-Rank $templateKey $displayName
        $isPet = $rank -eq "pet" -or (Get-AttributeValue $section "Monster") -eq "1"
        $stats = [ordered]@{
            Source = $source.RelativePath
            SourceKind = $source.SourceKind
            SceneKey = $source.SceneKey
        }

        foreach ($entry in $section.GetEnumerator()) {
            $stats[$entry.Key] = $entry.Value
        }

        $monsters += [pscustomobject]@{
            SourceKey = $source.SourceKey
            SourceKind = $source.SourceKind
            SourceMapId = if ($source.MapId -eq 500) { $null } else { [Nullable[Int16]][Int16]$source.MapId }
            SceneKey = if ($source.MapId -eq 500) { "" } else { $source.SceneKey }
            TemplateKey = $templateKey
            DisplayName = if ($null -eq $displayName) { "" } else { $displayName }
            Rank = $rank
            IsBoss = $rank -eq "boss"
            IsElite = $rank -eq "elite"
            IsPet = [bool]$isPet
            AttackType = Get-ShortValue $section "AttackType"
            ModelFile = if ($section.Contains("FileName")) { $section["FileName"] } else { "" }
            TextureFile = if ($section.Contains("TextureName")) { $section["TextureName"] } else { "" }
            Scale = Get-FloatValue $section "Scale"
            CollisionRange = Get-FloatValue $section "Range"
            NameHeight = Get-FloatValue $section "NameHeight"
            StatsJson = ($stats | ConvertTo-Json -Compress)
        }
    }
}

$questMonsterNames = Read-TabFile $questMonsterPath
[xml]$questXml = Get-Content $questPath -Raw
$questMonsterReferences = @()

foreach ($quest in $questXml.Quest.ChildNodes) {
    if ($quest.NodeType -ne [System.Xml.XmlNodeType]::Element) {
        continue
    }

    $questId = [int]$quest.GetAttribute("ID")
    if (-not $questMonsterNames.ContainsKey($questId)) {
        continue
    }

    $mapIdText = $quest.GetAttribute("CreatureMapID")
    $position = ConvertTo-Position $quest.GetAttribute("CreatureMapPos")

    $questMonsterReferences += [pscustomobject]@{
        QuestId = $questId
        MonsterName = $questMonsterNames[$questId]
        MapId = if ([string]::IsNullOrWhiteSpace($mapIdText)) { $null } else { [Nullable[Int16]][Int16]$mapIdText }
        X = if ($null -eq $position) { $null } else { [Nullable[Single]][float]$position.X }
        Z = if ($null -eq $position) { $null } else { [Nullable[Single]][float]$position.Z }
        MinLevel = if ([string]::IsNullOrWhiteSpace($quest.GetAttribute("MinLevel"))) { $null } else { [Nullable[Int32]][int]$quest.GetAttribute("MinLevel") }
        MaxLevel = if ([string]::IsNullOrWhiteSpace($quest.GetAttribute("MaxLevel"))) { $null } else { [Nullable[Int32]][int]$quest.GetAttribute("MaxLevel") }
        Faction = if ([string]::IsNullOrWhiteSpace($quest.GetAttribute("Faction"))) { $null } else { [Nullable[Int16]][Int16]$quest.GetAttribute("Faction") }
        PetGroup = if ([string]::IsNullOrWhiteSpace($quest.GetAttribute("pet"))) { $null } else { [Nullable[Int16]][Int16]$quest.GetAttribute("pet") }
        Source = "QuestMonster.dat + Quest.xml"
    }
}

$csharp = [System.Text.StringBuilder]::new()
[void]$csharp.AppendLine("// <auto-generated />")
[void]$csharp.AppendLine("// Generated from Godswar Origin MonsterConfig.ini, Monster.ini files, QuestMonster.dat, and Quest.xml.")
[void]$csharp.AppendLine("namespace Godswar.Server.State;")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct MonsterTemplateSeed(string SourceKey, string SourceKind, short? SourceMapId, string SceneKey, string TemplateKey, string DisplayName, string Rank, bool IsBoss, bool IsElite, bool IsPet, short? AttackType, string ModelFile, string TextureFile, float? Scale, float? CollisionRange, float? NameHeight, string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct QuestMonsterReferenceSeed(int QuestId, string MonsterName, short? MapId, float? X, float? Z, int? MinLevel, int? MaxLevel, short? Faction, short? PetGroup, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal static class MonsterTemplateSeeds")
[void]$csharp.AppendLine("{")
[void]$csharp.AppendLine("    public static IReadOnlyList<MonsterTemplateSeed> Monsters { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $monsters) {
    $isBoss = if ($row.IsBoss) { "true" } else { "false" }
    $isElite = if ($row.IsElite) { "true" } else { "false" }
    $isPet = if ($row.IsPet) { "true" } else { "false" }
    [void]$csharp.AppendLine("        new($(ConvertTo-CSharpString $row.SourceKey), $(ConvertTo-CSharpString $row.SourceKind), $(ConvertTo-CSharpNullableShort $row.SourceMapId), $(ConvertTo-CSharpString $row.SceneKey), $(ConvertTo-CSharpString $row.TemplateKey), $(ConvertTo-CSharpString $row.DisplayName), $(ConvertTo-CSharpString $row.Rank), $isBoss, $isElite, $isPet, $(ConvertTo-CSharpNullableShort $row.AttackType), $(ConvertTo-CSharpString $row.ModelFile), $(ConvertTo-CSharpString $row.TextureFile), $(ConvertTo-CSharpNullableFloat $row.Scale), $(ConvertTo-CSharpNullableFloat $row.CollisionRange), $(ConvertTo-CSharpNullableFloat $row.NameHeight), $(ConvertTo-CSharpString $row.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<QuestMonsterReferenceSeed> QuestReferences { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $questMonsterReferences) {
    [void]$csharp.AppendLine("        new($($row.QuestId), $(ConvertTo-CSharpString $row.MonsterName), $(ConvertTo-CSharpNullableShort $row.MapId), $(ConvertTo-CSharpNullableFloat $row.X), $(ConvertTo-CSharpNullableFloat $row.Z), $(ConvertTo-CSharpNullableInt $row.MinLevel), $(ConvertTo-CSharpNullableInt $row.MaxLevel), $(ConvertTo-CSharpNullableShort $row.Faction), $(ConvertTo-CSharpNullableShort $row.PetGroup), $(ConvertTo-CSharpString $row.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine("}")

$sql = [System.Text.StringBuilder]::new()
[void]$sql.AppendLine("-- <auto-generated />")
[void]$sql.AppendLine("-- Generated from Godswar Origin MonsterConfig.ini, Monster.ini files, QuestMonster.dat, and Quest.xml.")
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS monster_templates (")
[void]$sql.AppendLine("    source_key varchar(32) NOT NULL,")
[void]$sql.AppendLine("    source_kind varchar(16) NOT NULL,")
[void]$sql.AppendLine("    source_map_id smallint,")
[void]$sql.AppendLine("    scene_key varchar(96) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    template_key varchar(128) NOT NULL,")
[void]$sql.AppendLine("    display_name varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    rank varchar(16) NOT NULL DEFAULT 'normal',")
[void]$sql.AppendLine("    is_boss boolean NOT NULL DEFAULT false,")
[void]$sql.AppendLine("    is_elite boolean NOT NULL DEFAULT false,")
[void]$sql.AppendLine("    is_pet boolean NOT NULL DEFAULT false,")
[void]$sql.AppendLine("    attack_type smallint,")
[void]$sql.AppendLine("    model_file varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    texture_file varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    scale real,")
[void]$sql.AppendLine("    collision_range real,")
[void]$sql.AppendLine("    name_height real,")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb,")
[void]$sql.AppendLine("    PRIMARY KEY (source_key, template_key)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_monster_templates_source_map ON monster_templates (source_map_id);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_monster_templates_name ON monster_templates (display_name);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_monster_templates_rank ON monster_templates (rank);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO monster_templates (source_key, source_kind, source_map_id, scene_key, template_key, display_name, rank, is_boss, is_elite, is_pet, attack_type, model_file, texture_file, scale, collision_range, name_height, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $monsters.Count; $i++) {
    $row = $monsters[$i]
    $suffix = if ($i -eq $monsters.Count - 1) { "" } else { "," }
    $isBoss = if ($row.IsBoss) { "true" } else { "false" }
    $isElite = if ($row.IsElite) { "true" } else { "false" }
    $isPet = if ($row.IsPet) { "true" } else { "false" }
    [void]$sql.AppendLine("    ($(ConvertTo-SqlString $row.SourceKey), $(ConvertTo-SqlString $row.SourceKind), $(ConvertTo-SqlNullableSmallint $row.SourceMapId), $(ConvertTo-SqlString $row.SceneKey), $(ConvertTo-SqlString $row.TemplateKey), $(ConvertTo-SqlString $row.DisplayName), $(ConvertTo-SqlString $row.Rank), $isBoss, $isElite, $isPet, $(ConvertTo-SqlNullableSmallint $row.AttackType), $(ConvertTo-SqlString $row.ModelFile), $(ConvertTo-SqlString $row.TextureFile), $(ConvertTo-SqlNullableReal $row.Scale), $(ConvertTo-SqlNullableReal $row.CollisionRange), $(ConvertTo-SqlNullableReal $row.NameHeight), $(ConvertTo-SqlString $row.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (source_key, template_key) DO UPDATE")
[void]$sql.AppendLine("SET source_kind = EXCLUDED.source_kind,")
[void]$sql.AppendLine("    source_map_id = EXCLUDED.source_map_id,")
[void]$sql.AppendLine("    scene_key = EXCLUDED.scene_key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    rank = EXCLUDED.rank,")
[void]$sql.AppendLine("    is_boss = EXCLUDED.is_boss,")
[void]$sql.AppendLine("    is_elite = EXCLUDED.is_elite,")
[void]$sql.AppendLine("    is_pet = EXCLUDED.is_pet,")
[void]$sql.AppendLine("    attack_type = EXCLUDED.attack_type,")
[void]$sql.AppendLine("    model_file = EXCLUDED.model_file,")
[void]$sql.AppendLine("    texture_file = EXCLUDED.texture_file,")
[void]$sql.AppendLine("    scale = EXCLUDED.scale,")
[void]$sql.AppendLine("    collision_range = EXCLUDED.collision_range,")
[void]$sql.AppendLine("    name_height = EXCLUDED.name_height,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS quest_monster_references (")
[void]$sql.AppendLine("    quest_id integer PRIMARY KEY,")
[void]$sql.AppendLine("    monster_name varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    map_id smallint,")
[void]$sql.AppendLine("    pos_x real,")
[void]$sql.AppendLine("    pos_z real,")
[void]$sql.AppendLine("    min_level integer,")
[void]$sql.AppendLine("    max_level integer,")
[void]$sql.AppendLine("    faction smallint,")
[void]$sql.AppendLine("    pet_group smallint,")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT ''")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_quest_monster_references_map ON quest_monster_references (map_id);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_quest_monster_references_name ON quest_monster_references (monster_name);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO quest_monster_references (quest_id, monster_name, map_id, pos_x, pos_z, min_level, max_level, faction, pet_group, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $questMonsterReferences.Count; $i++) {
    $row = $questMonsterReferences[$i]
    $suffix = if ($i -eq $questMonsterReferences.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.QuestId), $(ConvertTo-SqlString $row.MonsterName), $(ConvertTo-SqlNullableSmallint $row.MapId), $(ConvertTo-SqlNullableReal $row.X), $(ConvertTo-SqlNullableReal $row.Z), $(ConvertTo-SqlNullableInt $row.MinLevel), $(ConvertTo-SqlNullableInt $row.MaxLevel), $(ConvertTo-SqlNullableSmallint $row.Faction), $(ConvertTo-SqlNullableSmallint $row.PetGroup), $(ConvertTo-SqlString $row.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (quest_id) DO UPDATE")
[void]$sql.AppendLine("SET monster_name = EXCLUDED.monster_name,")
[void]$sql.AppendLine("    map_id = EXCLUDED.map_id,")
[void]$sql.AppendLine("    pos_x = EXCLUDED.pos_x,")
[void]$sql.AppendLine("    pos_z = EXCLUDED.pos_z,")
[void]$sql.AppendLine("    min_level = EXCLUDED.min_level,")
[void]$sql.AppendLine("    max_level = EXCLUDED.max_level,")
[void]$sql.AppendLine("    faction = EXCLUDED.faction,")
[void]$sql.AppendLine("    pet_group = EXCLUDED.pet_group,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW monster_template_summary AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    COALESCE(mt.source_map_id, -1)::smallint AS map_id,")
[void]$sql.AppendLine("    mt.scene_key,")
[void]$sql.AppendLine("    COUNT(*) AS monster_count,")
[void]$sql.AppendLine("    COUNT(*) FILTER (WHERE mt.is_boss) AS boss_count,")
[void]$sql.AppendLine("    COUNT(*) FILTER (WHERE mt.is_elite) AS elite_count,")
[void]$sql.AppendLine("    COUNT(*) FILTER (WHERE mt.is_pet) AS pet_count")
[void]$sql.AppendLine("FROM monster_templates mt")
[void]$sql.AppendLine("GROUP BY COALESCE(mt.source_map_id, -1), mt.scene_key;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW boss_templates AS")
[void]$sql.AppendLine("SELECT *")
[void]$sql.AppendLine("FROM monster_templates")
[void]$sql.AppendLine("WHERE is_boss")
[void]$sql.AppendLine("ORDER BY source_map_id NULLS FIRST, display_name, template_key;")

[System.IO.File]::WriteAllText($CSharpOutputPath, $csharp.ToString(), [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($SqlOutputPath, $sql.ToString(), [System.Text.Encoding]::UTF8)

Write-Host "Generated $($monsters.Count) monster template rows and $($questMonsterReferences.Count) quest monster references."
Write-Host "Bosses: $(($monsters | Where-Object IsBoss).Count), elites: $(($monsters | Where-Object IsElite).Count), pets: $(($monsters | Where-Object IsPet).Count)."
Write-Host "C#:  $CSharpOutputPath"
Write-Host "SQL: $SqlOutputPath"
