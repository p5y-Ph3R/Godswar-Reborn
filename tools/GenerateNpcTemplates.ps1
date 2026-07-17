param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\NpcTemplateSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\007_npcs.sql"
)

$ErrorActionPreference = "Stop"

$npcIniPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\NPC.INI"
$npcNamePath = Join-Path $ClientRoot "Localization\en_us\Text\NpcName.dat"
$npcDescriptionPath = Join-Path $ClientRoot "Localization\en_us\Text\NPCDescription.dat"
$questPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\Quest.xml"
$npcFunPath = Join-Path $ClientRoot "Localization\en_us\UI\XML\NpcFun.lua"
$newbieGuideScriptPath = Join-Path $ClientRoot "Localization\en_us\UI\XML\NpcFun\NpcFunNewMan.lua"
$luaTextPath = Join-Path $ClientRoot "Localization\en_us\UI\Base\LuaText.lua"

foreach ($path in @($npcIniPath, $npcNamePath, $npcDescriptionPath, $questPath, $npcFunPath, $newbieGuideScriptPath, $luaTextPath)) {
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

function ConvertTo-CSharpFloat([float]$value) {
    return $value.ToString($invariantCulture) + "f"
}

function ConvertTo-SqlReal([float]$value) {
    return $value.ToString($invariantCulture)
}

function Get-AttributeValue($attributes, [string]$name) {
    if ($attributes.Contains($name)) {
        return $attributes[$name]
    }

    return $null
}

function Get-NpcKey([string]$templateKey) {
    if ($templateKey -match '^(?<scene>[A-Za-z]+_Newbie)_(?<id>\d+)') {
        return "$($Matches.scene)_$($Matches.id)"
    }

    if ($templateKey -match '^(?<scene>[A-Za-z]+)_(?<id>\d+)') {
        return "$($Matches.scene)_$($Matches.id)"
    }

    return $templateKey
}

function Get-SceneKey([string]$npcKey) {
    if ($npcKey -match '^(?<scene>.+)_\d+$') {
        return $Matches.scene
    }

    return ""
}

function ConvertTo-Position([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
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

function Read-TabFile([string]$path) {
    $values = @{}
    foreach ($line in Get-Content $path) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("//")) {
            continue
        }

        $parts = $line -split "`t", 2
        if ($parts.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($parts[0])) {
            $values[$parts[0].Trim()] = $parts[1].Trim()
        }
    }

    return $values
}

$names = Read-TabFile $npcNamePath
$descriptions = Read-TabFile $npcDescriptionPath

$npcTextKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($key in $names.Keys) {
    [void]$npcTextKeys.Add($key)
}

foreach ($key in $descriptions.Keys) {
    [void]$npcTextKeys.Add($key)
}

$npcTexts = @(
    foreach ($key in ($npcTextKeys | Sort-Object)) {
        [pscustomobject]@{
            NpcKey = $key
            SceneKey = Get-SceneKey $key
            DisplayName = if ($names.ContainsKey($key)) { $names[$key] } else { "" }
            Description = if ($descriptions.ContainsKey($key)) { $descriptions[$key] } else { "" }
        }
    }
)

$sections = [ordered]@{}
$currentSection = $null
foreach ($line in Get-Content $npcIniPath) {
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

$appearances = @(
    foreach ($templateKey in $sections.Keys) {
        $section = $sections[$templateKey]
        $npcKey = Get-NpcKey $templateKey
        $sceneKey = Get-SceneKey $npcKey
        $sexText = Get-AttributeValue $section "sex"

        [pscustomobject]@{
            TemplateKey = $templateKey
            NpcKey = $npcKey
            SceneKey = $sceneKey
            InternalName = Get-AttributeValue $section "name"
            Sex = if ([string]::IsNullOrWhiteSpace($sexText)) { $null } else { [Nullable[Int16]][Int16]$sexText }
            StatsJson = ($section | ConvertTo-Json -Compress)
        }
    }
) | Sort-Object TemplateKey

[xml]$questXml = Get-Content $questPath -Raw
$spawnReferences = @()
foreach ($quest in $questXml.Quest.ChildNodes) {
    if ($quest.NodeType -ne [System.Xml.XmlNodeType]::Element) {
        continue
    }

    $questId = [int]$quest.GetAttribute("ID")
    foreach ($role in @("Giver", "Responder")) {
        $npcKey = $quest.GetAttribute("${role}Name")
        $mapIdText = $quest.GetAttribute("${role}MapID")
        $position = ConvertTo-Position $quest.GetAttribute($role)
        if ([string]::IsNullOrWhiteSpace($npcKey) -or [string]::IsNullOrWhiteSpace($mapIdText) -or $null -eq $position) {
            continue
        }

        $spawnReferences += [pscustomobject]@{
            QuestId = $questId
            Role = $role.ToLowerInvariant()
            NpcKey = $npcKey
            MapId = [Int16]$mapIdText
            X = [float]$position.X
            Z = [float]$position.Z
            Source = "Quest.xml"
        }
    }
}

$spawnReferences = @(
    $spawnReferences |
        Sort-Object QuestId, Role, NpcKey, MapId, X, Z -Unique
)

$npcFunctions = @()
foreach ($line in Get-Content $npcFunPath) {
    if ($line -match '^(?<key>NPC_FLAG_[A-Za-z0-9_]+)\s*=\s*(?<flag>\d+)\s*(?:--+\s*(?<comment>.*))?') {
        $key = $Matches.key
        $flag = [int]$Matches.flag
        $comment = if ($Matches.ContainsKey("comment")) { $Matches.comment.Trim() } else { "" }
        $scriptFile = switch ($key) {
            "NPC_FLAG_SYS_NEWMAN" { "NpcFunNewMan.lua" }
            "NPC_FLAG_SYS_TRANMIT" { "NpcFunTranmit.lua" }
            "NPC_FLAG_SYS_WAR" { "NpcFunWar.lua" }
            "NPC_FLAG_SYS_BREAK" { "NpcFunBreak.lua" }
            "NPC_FLAG_SYS_ALTAR" { "NpcFunAltar.lua" }
            "NPC_FLAG_GUILDQUEST" { "NpcFunGuildQuest.lua" }
            "NPC_FLAG_ACTIVITY" { "NpcFunActivity.lua" }
            "NPC_FLAG_SYS_SKILLBOOK" { "NpcFunSkillbook.lua" }
            "NPC_FLAG_LivingSkill" { "NpcFunLifeSkill.lua" }
            "NPC_FLAG_SYS_REPETITION" { "NpcFunRepetition.lua" }
            "NPC_FLAG_SYS_REPREWARD" { "NpcFunRepetition.lua" }
            "NPC_FLAG_SYS_REPLEAVE" { "NpcFunRepetition.lua" }
            "NPC_FLAG_SYS_DESIDENTIFY" { "NpcFunDesidentify.lua" }
            "NPC_FLAG_SYS_DESAWARD" { "NpcFunDesaward.lua" }
            "NPC_FLAG_SYS_AWARD" { "NpcFunAward.lua" }
            "NPC_FLAG_SYS_SIGNACT" { "NpcFunSignact.lua" }
            "NPC_FLAG_SYS_MATERIALBACK" { "NpcFunMaterialBack.lua" }
            "NPC_FLAG_SYS_STAR" { "NpcFunStar.lua" }
            "NPC_FLAG_SYS_UNIONWAR" { "NpcFunUnionWar.lua" }
            "NPC_FLAG_SYS_ASSOCIATION" { "NpcFunAssociation.lua" }
            "NPC_FLAG_SYS_HEALTH" { "NpcFunHealth.lua" }
            "NPC_FLAG_SYS_OLDMAN" { "NpcFunOldMan.lua" }
            "NPC_FLAG_SYS_LOSTBOOK" { "NpcFunLostBook.lua" }
            "NPC_FLAG_SYS_REMAIN" { "NpcFunRemain.lua" }
            "NPC_FLAG_SYS_PAN" { "NpcFunPan.lua" }
            "NPC_FLAG_SYS_MESSENGER" { "NpcFunMessenger.lua" }
            default { "" }
        }

        $npcFunctions += [pscustomobject]@{
            FunctionFlag = $flag
            FunctionKey = $key
            DisplayName = $comment
            ScriptFile = $scriptFile
            Source = "NpcFun.lua"
        }
    }
}

$luaText = @{}
foreach ($line in Get-Content $luaTextPath) {
    if ($line -match '^(?<key>NF_[A-Za-z0-9_]+)\s*=\s*"(?<value>.*)"\s*$') {
        $luaText[$Matches.key] = $Matches.value.Replace('\"', '"').Replace('\n', "`n")
    }
}

$dialogs = @()
$functionName = ""
$index = $null
$subId = $null
$lineNumber = 0
foreach ($line in Get-Content $newbieGuideScriptPath) {
    $lineNumber++
    if ($line -match '^function\s+(?<name>\w+)\(') {
        $functionName = $Matches.name
        $index = $null
        $subId = $null
        continue
    }

    if ($line -match '^\s*(?:if|elseif)\s+Index\s*==\s*(?<index>\d+)') {
        $index = [Int16]$Matches.index
        $subId = $null
        continue
    }

    if ($line -match '^\s*(?:if|elseif)\s+SubID\s*==\s*(?<subId>\d+)') {
        $subId = [int]$Matches.subId
        continue
    }

    if ($line -match 'SetText\((?<key>NF_[A-Za-z0-9_]+)\)') {
        $textKey = $Matches.key
        $elementKind = if ($line -match 'Button:SetText') {
            "button"
        } elseif ($functionName -like "*SetMsg") {
            "message"
        } else {
            "text"
        }

        $stats = [ordered]@{
            SourceFile = "NpcFunNewMan.lua"
            Line = $lineNumber
            Raw = $line.Trim()
        }

        $dialogs += [pscustomobject]@{
            ScriptKey = "newbie_guide"
            FunctionName = $functionName
            DialogIndex = $index
            SubId = $subId
            ElementKind = $elementKind
            TextKey = $textKey
            Text = if ($luaText.ContainsKey($textKey)) { $luaText[$textKey] } else { "" }
            StatsJson = ($stats | ConvertTo-Json -Compress)
        }
    }
}

$dialogs = @(
    $dialogs |
        Where-Object { $null -ne $_.DialogIndex -and $null -ne $_.SubId } |
        Sort-Object FunctionName, DialogIndex, SubId, ElementKind, TextKey -Unique
)

$csharp = [System.Text.StringBuilder]::new()
[void]$csharp.AppendLine("// <auto-generated />")
[void]$csharp.AppendLine("// Generated from Godswar Origin NPC.INI, NpcName.dat, NPCDescription.dat, Quest.xml, NpcFun.lua, NpcFunNewMan.lua, and LuaText.lua.")
[void]$csharp.AppendLine("namespace Godswar.Server.State;")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct NpcTextTemplateSeed(string NpcKey, string SceneKey, string DisplayName, string Description);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct NpcAppearanceTemplateSeed(string TemplateKey, string NpcKey, string SceneKey, string InternalName, short? Sex, string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct NpcSpawnReferenceSeed(int QuestId, string Role, string NpcKey, short MapId, float X, float Z, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct NpcFunctionTemplateSeed(int FunctionFlag, string FunctionKey, string DisplayName, string ScriptFile, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct NpcDialogTemplateSeed(string ScriptKey, string FunctionName, short DialogIndex, int SubId, string ElementKind, string TextKey, string Text, string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal static class NpcTemplateSeeds")
[void]$csharp.AppendLine("{")
[void]$csharp.AppendLine("    public static IReadOnlyList<NpcTextTemplateSeed> Texts { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $npcTexts) {
    [void]$csharp.AppendLine("        new($(ConvertTo-CSharpString $row.NpcKey), $(ConvertTo-CSharpString $row.SceneKey), $(ConvertTo-CSharpString $row.DisplayName), $(ConvertTo-CSharpString $row.Description)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<NpcAppearanceTemplateSeed> Appearances { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $appearances) {
    [void]$csharp.AppendLine("        new($(ConvertTo-CSharpString $row.TemplateKey), $(ConvertTo-CSharpString $row.NpcKey), $(ConvertTo-CSharpString $row.SceneKey), $(ConvertTo-CSharpString $row.InternalName), $(ConvertTo-CSharpNullableShort $row.Sex), $(ConvertTo-CSharpString $row.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<NpcSpawnReferenceSeed> SpawnReferences { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $spawnReferences) {
    [void]$csharp.AppendLine("        new($($row.QuestId), $(ConvertTo-CSharpString $row.Role), $(ConvertTo-CSharpString $row.NpcKey), $($row.MapId), $(ConvertTo-CSharpFloat $row.X), $(ConvertTo-CSharpFloat $row.Z), $(ConvertTo-CSharpString $row.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<NpcFunctionTemplateSeed> Functions { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $npcFunctions) {
    [void]$csharp.AppendLine("        new($($row.FunctionFlag), $(ConvertTo-CSharpString $row.FunctionKey), $(ConvertTo-CSharpString $row.DisplayName), $(ConvertTo-CSharpString $row.ScriptFile), $(ConvertTo-CSharpString $row.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<NpcDialogTemplateSeed> Dialogs { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $dialogs) {
    [void]$csharp.AppendLine("        new($(ConvertTo-CSharpString $row.ScriptKey), $(ConvertTo-CSharpString $row.FunctionName), $($row.DialogIndex), $($row.SubId), $(ConvertTo-CSharpString $row.ElementKind), $(ConvertTo-CSharpString $row.TextKey), $(ConvertTo-CSharpString $row.Text), $(ConvertTo-CSharpString $row.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine("}")

$sql = [System.Text.StringBuilder]::new()
[void]$sql.AppendLine("-- <auto-generated />")
[void]$sql.AppendLine("-- Generated from Godswar Origin NPC.INI, NpcName.dat, NPCDescription.dat, Quest.xml, NpcFun.lua, NpcFunNewMan.lua, and LuaText.lua.")
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS npc_text_templates (")
[void]$sql.AppendLine("    npc_key varchar(96) PRIMARY KEY,")
[void]$sql.AppendLine("    scene_key varchar(64) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    display_name varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    description text NOT NULL DEFAULT ''")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_npc_text_templates_scene ON npc_text_templates (scene_key);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO npc_text_templates (npc_key, scene_key, display_name, description)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $npcTexts.Count; $i++) {
    $row = $npcTexts[$i]
    $suffix = if ($i -eq $npcTexts.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($(ConvertTo-SqlString $row.NpcKey), $(ConvertTo-SqlString $row.SceneKey), $(ConvertTo-SqlString $row.DisplayName), $(ConvertTo-SqlString $row.Description))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (npc_key) DO UPDATE")
[void]$sql.AppendLine("SET scene_key = EXCLUDED.scene_key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    description = EXCLUDED.description;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS npc_appearance_templates (")
[void]$sql.AppendLine("    template_key varchar(128) PRIMARY KEY,")
[void]$sql.AppendLine("    npc_key varchar(96) NOT NULL,")
[void]$sql.AppendLine("    scene_key varchar(64) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    internal_name varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    sex smallint,")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_npc_appearance_templates_npc_key ON npc_appearance_templates (npc_key);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_npc_appearance_templates_scene ON npc_appearance_templates (scene_key);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO npc_appearance_templates (template_key, npc_key, scene_key, internal_name, sex, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $appearances.Count; $i++) {
    $row = $appearances[$i]
    $suffix = if ($i -eq $appearances.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($(ConvertTo-SqlString $row.TemplateKey), $(ConvertTo-SqlString $row.NpcKey), $(ConvertTo-SqlString $row.SceneKey), $(ConvertTo-SqlString $row.InternalName), $(ConvertTo-SqlNullableSmallint $row.Sex), $(ConvertTo-SqlString $row.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (template_key) DO UPDATE")
[void]$sql.AppendLine("SET npc_key = EXCLUDED.npc_key,")
[void]$sql.AppendLine("    scene_key = EXCLUDED.scene_key,")
[void]$sql.AppendLine("    internal_name = EXCLUDED.internal_name,")
[void]$sql.AppendLine("    sex = EXCLUDED.sex,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS npc_spawn_references (")
[void]$sql.AppendLine("    quest_id integer NOT NULL,")
[void]$sql.AppendLine("    role varchar(16) NOT NULL,")
[void]$sql.AppendLine("    npc_key varchar(96) NOT NULL,")
[void]$sql.AppendLine("    map_id smallint NOT NULL,")
[void]$sql.AppendLine("    pos_x real NOT NULL,")
[void]$sql.AppendLine("    pos_z real NOT NULL,")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    PRIMARY KEY (quest_id, role, npc_key)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_npc_spawn_references_npc_key ON npc_spawn_references (npc_key);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_npc_spawn_references_map ON npc_spawn_references (map_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO npc_spawn_references (quest_id, role, npc_key, map_id, pos_x, pos_z, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $spawnReferences.Count; $i++) {
    $row = $spawnReferences[$i]
    $suffix = if ($i -eq $spawnReferences.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.QuestId), $(ConvertTo-SqlString $row.Role), $(ConvertTo-SqlString $row.NpcKey), $($row.MapId), $(ConvertTo-SqlReal $row.X), $(ConvertTo-SqlReal $row.Z), $(ConvertTo-SqlString $row.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (quest_id, role, npc_key) DO UPDATE")
[void]$sql.AppendLine("SET map_id = EXCLUDED.map_id,")
[void]$sql.AppendLine("    pos_x = EXCLUDED.pos_x,")
[void]$sql.AppendLine("    pos_z = EXCLUDED.pos_z,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS npc_function_templates (")
[void]$sql.AppendLine("    function_flag integer PRIMARY KEY,")
[void]$sql.AppendLine("    function_key varchar(64) NOT NULL UNIQUE,")
[void]$sql.AppendLine("    display_name varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    script_file varchar(128) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT ''")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO npc_function_templates (function_flag, function_key, display_name, script_file, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $npcFunctions.Count; $i++) {
    $row = $npcFunctions[$i]
    $suffix = if ($i -eq $npcFunctions.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.FunctionFlag), $(ConvertTo-SqlString $row.FunctionKey), $(ConvertTo-SqlString $row.DisplayName), $(ConvertTo-SqlString $row.ScriptFile), $(ConvertTo-SqlString $row.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (function_flag) DO UPDATE")
[void]$sql.AppendLine("SET function_key = EXCLUDED.function_key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    script_file = EXCLUDED.script_file,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS npc_dialog_templates (")
[void]$sql.AppendLine("    script_key varchar(64) NOT NULL,")
[void]$sql.AppendLine("    function_name varchar(96) NOT NULL,")
[void]$sql.AppendLine("    dialog_index smallint NOT NULL,")
[void]$sql.AppendLine("    sub_id integer NOT NULL,")
[void]$sql.AppendLine("    element_kind varchar(32) NOT NULL,")
[void]$sql.AppendLine("    text_key varchar(64) NOT NULL,")
[void]$sql.AppendLine("    text text NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb,")
[void]$sql.AppendLine("    PRIMARY KEY (script_key, function_name, dialog_index, sub_id, element_kind, text_key)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_npc_dialog_templates_script ON npc_dialog_templates (script_key, function_name);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO npc_dialog_templates (script_key, function_name, dialog_index, sub_id, element_kind, text_key, text, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $dialogs.Count; $i++) {
    $row = $dialogs[$i]
    $suffix = if ($i -eq $dialogs.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($(ConvertTo-SqlString $row.ScriptKey), $(ConvertTo-SqlString $row.FunctionName), $($row.DialogIndex), $($row.SubId), $(ConvertTo-SqlString $row.ElementKind), $(ConvertTo-SqlString $row.TextKey), $(ConvertTo-SqlString $row.Text), $(ConvertTo-SqlString $row.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (script_key, function_name, dialog_index, sub_id, element_kind, text_key) DO UPDATE")
[void]$sql.AppendLine("SET text = EXCLUDED.text,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW npc_template_summary AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    nt.npc_key,")
[void]$sql.AppendLine("    nt.scene_key,")
[void]$sql.AppendLine("    nt.display_name,")
[void]$sql.AppendLine("    nt.description,")
[void]$sql.AppendLine("    COUNT(DISTINCT na.template_key) AS appearance_count,")
[void]$sql.AppendLine("    COUNT(DISTINCT ns.quest_id) AS quest_reference_count")
[void]$sql.AppendLine("FROM npc_text_templates nt")
[void]$sql.AppendLine("LEFT JOIN npc_appearance_templates na ON na.npc_key = nt.npc_key")
[void]$sql.AppendLine("LEFT JOIN npc_spawn_references ns ON ns.npc_key = nt.npc_key")
[void]$sql.AppendLine("GROUP BY nt.npc_key, nt.scene_key, nt.display_name, nt.description;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW npc_guide_templates AS")
[void]$sql.AppendLine("SELECT *")
[void]$sql.AppendLine("FROM npc_template_summary")
[void]$sql.AppendLine("WHERE display_name ILIKE '%guide%'")
[void]$sql.AppendLine("   OR npc_key IN ('Athens_094', 'Sparta_094');")

[System.IO.File]::WriteAllText($CSharpOutputPath, $csharp.ToString(), [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($SqlOutputPath, $sql.ToString(), [System.Text.Encoding]::UTF8)

Write-Host "Generated $($npcTexts.Count) NPC text rows, $($appearances.Count) appearances, $($spawnReferences.Count) quest spawn references, $($npcFunctions.Count) function flags, and $($dialogs.Count) Newbie Guide dialog rows."
Write-Host "C#:  $CSharpOutputPath"
Write-Host "SQL: $SqlOutputPath"
