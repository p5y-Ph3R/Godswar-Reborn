param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\MapTemplateSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\008_maps.sql"
)

$ErrorActionPreference = "Stop"

$addressConfigPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\AddressConfig.ini"
$mapIdToNamePath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\MapIdToNameConfig.ini"
$mapSoundPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\MapSoundConfig.xml"
$spanMapPath = Join-Path $ClientRoot "Localization\en_us\Settings\Sys\SpanMapConfig.xml"
$scenesPath = Join-Path $ClientRoot "Localization\en_us\Text\Scenes.dat"

foreach ($path in @($addressConfigPath, $mapIdToNamePath, $mapSoundPath, $spanMapPath, $scenesPath)) {
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

function ConvertTo-CSharpShortArray($values) {
    if ($null -eq $values -or $values.Count -eq 0) {
        return "[]"
    }

    return "new short[] { " + (($values | ForEach-Object { ([Int16]$_).ToString($invariantCulture) }) -join ", ") + " }"
}

function ConvertTo-SqlSmallintArray($values) {
    if ($null -eq $values -or $values.Count -eq 0) {
        return "'{}'::smallint[]"
    }

    return "ARRAY[" + (($values | ForEach-Object { ([Int16]$_).ToString($invariantCulture) }) -join ",") + "]::smallint[]"
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

        if ($trimmed -match '^\[(?<name>[^\]]+)\]\s*;?(?<comment>.*)$') {
            $currentSection = $Matches.name.Trim()
            if (-not $sections.Contains($currentSection)) {
                $sections[$currentSection] = [ordered]@{}
            }

            if (-not [string]::IsNullOrWhiteSpace($Matches.comment)) {
                $sections[$currentSection]["__comment"] = $Matches.comment.Trim()
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
            $values[$parts[0].Trim()] = $parts[1].Trim()
        }
    }

    return $values
}

function ConvertTo-Position([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $parts = $value.Replace("f", "").TrimEnd(";").Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) {
        return $null
    }

    return @{
        X = [float]::Parse($parts[0].Trim(), $invariantCulture)
        Z = [float]::Parse($parts[1].Trim(), $invariantCulture)
    }
}

function ConvertTo-Area([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    $parts = $value.Replace("f", "").TrimEnd(";").Split(',', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 4) {
        return $null
    }

    return @{
        X1 = [float]::Parse($parts[0].Trim(), $invariantCulture)
        Z1 = [float]::Parse($parts[1].Trim(), $invariantCulture)
        X2 = [float]::Parse($parts[2].Trim(), $invariantCulture)
        Z2 = [float]::Parse($parts[3].Trim(), $invariantCulture)
    }
}

function Resolve-ClientPath([string]$path) {
    $normalized = $path.Trim().Replace('/', '\')
    if ($normalized.StartsWith(".\")) {
        $normalized = $normalized.Substring(2)
    }

    return Join-Path $ClientRoot $normalized
}

$scenes = Read-TabFile $scenesPath
$addressIni = Read-IniFile $addressConfigPath
$mapIdIni = Read-IniFile $mapIdToNamePath

$addressPaths = @{}
foreach ($key in $addressIni["Address"].Keys) {
    if ($key -match '^Address(?<id>\d+)$') {
        $addressPaths[[int]$Matches.id] = $addressIni["Address"][$key]
    }
}

$mapIdToName = @{}
foreach ($key in $mapIdIni["config"].Keys) {
    if ($key -match '^MapID(?<id>\d+)$') {
        $parts = $mapIdIni["config"][$key].Split(',', [StringSplitOptions]::RemoveEmptyEntries)
        if ($parts.Count -ge 2) {
            $mapIdToName[[int]$Matches.id] = [pscustomobject]@{
                SceneKey = $parts[0].Trim()
                ClientSceneId = [int]$parts[1].Trim()
            }
        }
    }
}

$musicByClientSceneId = @{}
$eventSceneByClientSceneId = @{}
$soundBlock = ""
foreach ($line in Get-Content $mapSoundPath) {
    if ($line -match '<MapSound') {
        $soundBlock = "sound"
        continue
    }

    if ($line -match '</MapSound') {
        $soundBlock = ""
        continue
    }

    if ($line -match '<EventMap') {
        $soundBlock = "event"
        continue
    }

    if ($line -match '</EventMap') {
        $soundBlock = ""
        continue
    }

    if ($line -match '<Config\s+ID="(?<id>\d+)"\s+Name="(?<name>[^"]+)"') {
        if ($soundBlock -eq "sound") {
            $musicByClientSceneId[[int]$Matches.id] = $Matches.name
        } elseif ($soundBlock -eq "event") {
            $eventSceneByClientSceneId[[int]$Matches.id] = $Matches.name
        }
    }
}

$mapIds = [System.Collections.Generic.HashSet[int]]::new()
foreach ($id in $addressPaths.Keys) { [void]$mapIds.Add($id) }
foreach ($id in $mapIdToName.Keys) { [void]$mapIds.Add($id) }
foreach ($section in $addressIni.Keys) {
    if ($section -match '^AddressConfig(?<id>\d+)$') {
        [void]$mapIds.Add([int]$Matches.id)
    }
}

$maps = @()
$safeAreas = @()
$addressPoints = @()

foreach ($mapId in ($mapIds | Sort-Object)) {
    $config = if ($addressIni.Contains("AddressConfig$mapId")) { $addressIni["AddressConfig$mapId"] } else { @{} }
    $mapping = if ($mapIdToName.ContainsKey($mapId)) { $mapIdToName[$mapId] } else { $null }
    $sceneKey = if ($null -ne $mapping) { $mapping.SceneKey } elseif ($config.Contains("name")) { $config["name"] } else { "" }
    $displayName = if ($scenes.ContainsKey($sceneKey)) { $scenes[$sceneKey] } else { $sceneKey }
    $clientSceneId = if ($null -ne $mapping) { $mapping.ClientSceneId } else { $null }
    $addressPath = if ($addressPaths.ContainsKey($mapId)) { $addressPaths[$mapId] } else { "" }
    $musicName = if ($null -ne $clientSceneId -and $musicByClientSceneId.ContainsKey($clientSceneId)) { $musicByClientSceneId[$clientSceneId] } else { "" }
    $eventSceneKey = if ($null -ne $clientSceneId -and $eventSceneByClientSceneId.ContainsKey($clientSceneId)) { $eventSceneByClientSceneId[$clientSceneId] } else { "" }

    $stats = [ordered]@{
        Source = "AddressConfig.ini"
        ConfigSection = "AddressConfig$mapId"
        AddressPath = $addressPath
    }

    $maps += [pscustomobject]@{
        MapId = [Int16]$mapId
        SceneKey = $sceneKey
        DisplayName = $displayName
        ClientSceneId = if ($null -eq $clientSceneId) { $null } else { [int]$clientSceneId }
        MapMode = if ($config.Contains("MapMode")) { [Nullable[Int16]][Int16]$config["MapMode"] } else { $null }
        AddressFile = $addressPath
        MusicName = $musicName
        EventSceneKey = $eventSceneKey
        StatsJson = ($stats | ConvertTo-Json -Compress)
    }

    if ($config.Contains("SafeAreaNum")) {
        $count = [int]$config["SafeAreaNum"]
        for ($i = 1; $i -le $count; $i++) {
            $area = ConvertTo-Area $config["SafeArea$i"]
            if ($null -eq $area) {
                continue
            }

            $safeAreas += [pscustomobject]@{
                MapId = [Int16]$mapId
                AreaIndex = [Int16]$i
                X1 = [float]$area.X1
                Z1 = [float]$area.Z1
                X2 = [float]$area.X2
                Z2 = [float]$area.Z2
                Attribute = if ($config.Contains("SafeAreaAtt$i")) { [Nullable[Int16]][Int16]$config["SafeAreaAtt$i"] } else { $null }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($addressPath)) {
        $resolvedAddressPath = Resolve-ClientPath $addressPath
        if (Test-Path $resolvedAddressPath) {
            $addressSections = Read-IniFile $resolvedAddressPath
            foreach ($sectionKey in $addressSections.Keys) {
                if ($sectionKey -notmatch '^\d+$') {
                    continue
                }

                $section = $addressSections[$sectionKey]
                $groupIndex = [int]$sectionKey
                $groupName = if ($section.Contains("__comment")) { $section["__comment"] } else { "" }
                $count = if ($section.Contains("AddressCount")) { [int]$section["AddressCount"] } else { 0 }
                for ($i = 1; $i -le $count; $i++) {
                    $position = ConvertTo-Position $section["Coordinate$i"]
                    if ($null -eq $position) {
                        continue
                    }

                    $addressPoints += [pscustomobject]@{
                        MapId = [Int16]$mapId
                        GroupIndex = [Int16]$groupIndex
                        PointIndex = [Int16]$i
                        GroupName = $groupName
                        Name = if ($section.Contains("AddressName$i")) { $section["AddressName$i"] } else { "" }
                        X = [float]$position.X
                        Z = [float]$position.Z
                        Source = $addressPath
                    }
                }
            }
        }
    }
}

[xml]$spanXml = Get-Content $spanMapPath -Raw
$mapLinks = @()
foreach ($node in $spanXml.SpanMapConfig.MapConfig.ChildNodes) {
    if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element -or -not $node.HasAttribute("ID")) {
        continue
    }

    $mapId = [Int16]$node.GetAttribute("ID")
    foreach ($attribute in $node.Attributes) {
        if ($attribute.Name -match '^Map(?<index>\d+)$') {
            $parts = $attribute.Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries)
            if ($parts.Count -lt 3) {
                continue
            }

            $mapLinks += [pscustomobject]@{
                MapId = $mapId
                LinkIndex = [Int16]$Matches.index
                TargetMapId = [Int16]$parts[0].Trim()
                X = [float]::Parse($parts[1].Trim(), $invariantCulture)
                Z = [float]::Parse($parts[2].Trim(), $invariantCulture)
                Source = "SpanMapConfig.xml"
            }
        }
    }
}

$mapLinks = @(
    $mapLinks | Sort-Object MapId, LinkIndex, TargetMapId, X, Z -Unique
)

$mapRoutes = @()
foreach ($node in $spanXml.SpanMapConfig.RouteConfig.ChildNodes) {
    if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element -or -not $node.HasAttribute("Camp")) {
        continue
    }

    $camp = [Int16]$node.GetAttribute("Camp")
    foreach ($attribute in $node.Attributes) {
        if ($attribute.Name -match '^Route(?<index>\d+)$') {
            $ids = @(
                $attribute.Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
                    ForEach-Object { [Int16]$_.Trim() }
            )

            $mapRoutes += [pscustomobject]@{
                Camp = $camp
                RouteIndex = [Int16]$Matches.index
                MapIds = $ids
                Source = "SpanMapConfig.xml"
            }
        }
    }
}

$csharp = [System.Text.StringBuilder]::new()
[void]$csharp.AppendLine("// <auto-generated />")
[void]$csharp.AppendLine("// Generated from Godswar Origin AddressConfig.ini, MapIdToNameConfig.ini, Scenes.dat, MapSoundConfig.xml, SpanMapConfig.xml, and map Address.ini files.")
[void]$csharp.AppendLine("namespace Godswar.Server.State;")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct MapTemplateSeed(short MapId, string SceneKey, string DisplayName, int? ClientSceneId, short? MapMode, string AddressFile, string MusicName, string EventSceneKey, string StatsJson);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct MapSafeAreaSeed(short MapId, short AreaIndex, float X1, float Z1, float X2, float Z2, short? Attribute);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct MapAddressPointSeed(short MapId, short GroupIndex, short PointIndex, string GroupName, string Name, float X, float Z, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct MapLinkSeed(short MapId, short LinkIndex, short TargetMapId, float X, float Z, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal readonly record struct MapRouteSeed(short Camp, short RouteIndex, short[] MapIds, string Source);")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("internal static class MapTemplateSeeds")
[void]$csharp.AppendLine("{")
[void]$csharp.AppendLine("    public static IReadOnlyList<MapTemplateSeed> Maps { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $maps) {
    [void]$csharp.AppendLine("        new($($row.MapId), $(ConvertTo-CSharpString $row.SceneKey), $(ConvertTo-CSharpString $row.DisplayName), $(ConvertTo-CSharpNullableInt $row.ClientSceneId), $(ConvertTo-CSharpNullableShort $row.MapMode), $(ConvertTo-CSharpString $row.AddressFile), $(ConvertTo-CSharpString $row.MusicName), $(ConvertTo-CSharpString $row.EventSceneKey), $(ConvertTo-CSharpString $row.StatsJson)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<MapSafeAreaSeed> SafeAreas { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $safeAreas) {
    [void]$csharp.AppendLine("        new($($row.MapId), $($row.AreaIndex), $(ConvertTo-CSharpFloat $row.X1), $(ConvertTo-CSharpFloat $row.Z1), $(ConvertTo-CSharpFloat $row.X2), $(ConvertTo-CSharpFloat $row.Z2), $(ConvertTo-CSharpNullableShort $row.Attribute)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<MapAddressPointSeed> AddressPoints { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $addressPoints) {
    [void]$csharp.AppendLine("        new($($row.MapId), $($row.GroupIndex), $($row.PointIndex), $(ConvertTo-CSharpString $row.GroupName), $(ConvertTo-CSharpString $row.Name), $(ConvertTo-CSharpFloat $row.X), $(ConvertTo-CSharpFloat $row.Z), $(ConvertTo-CSharpString $row.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<MapLinkSeed> Links { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $mapLinks) {
    [void]$csharp.AppendLine("        new($($row.MapId), $($row.LinkIndex), $($row.TargetMapId), $(ConvertTo-CSharpFloat $row.X), $(ConvertTo-CSharpFloat $row.Z), $(ConvertTo-CSharpString $row.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine()
[void]$csharp.AppendLine("    public static IReadOnlyList<MapRouteSeed> Routes { get; } =")
[void]$csharp.AppendLine("    [")
foreach ($row in $mapRoutes) {
    [void]$csharp.AppendLine("        new($($row.Camp), $($row.RouteIndex), $(ConvertTo-CSharpShortArray $row.MapIds), $(ConvertTo-CSharpString $row.Source)),")
}
[void]$csharp.AppendLine("    ];")
[void]$csharp.AppendLine("}")

$sql = [System.Text.StringBuilder]::new()
[void]$sql.AppendLine("-- <auto-generated />")
[void]$sql.AppendLine("-- Generated from Godswar Origin AddressConfig.ini, MapIdToNameConfig.ini, Scenes.dat, MapSoundConfig.xml, SpanMapConfig.xml, and map Address.ini files.")
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS map_templates (")
[void]$sql.AppendLine("    map_id smallint PRIMARY KEY,")
[void]$sql.AppendLine("    scene_key varchar(96) NOT NULL,")
[void]$sql.AppendLine("    display_name varchar(128) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    client_scene_id integer,")
[void]$sql.AppendLine("    map_mode smallint,")
[void]$sql.AppendLine("    address_file varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    music_name varchar(128) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    event_scene_key varchar(128) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_map_templates_scene_key ON map_templates (scene_key);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_map_templates_client_scene_id ON map_templates (client_scene_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO map_templates (map_id, scene_key, display_name, client_scene_id, map_mode, address_file, music_name, event_scene_key, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $maps.Count; $i++) {
    $row = $maps[$i]
    $suffix = if ($i -eq $maps.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.MapId), $(ConvertTo-SqlString $row.SceneKey), $(ConvertTo-SqlString $row.DisplayName), $(ConvertTo-SqlNullableInt $row.ClientSceneId), $(ConvertTo-SqlNullableSmallint $row.MapMode), $(ConvertTo-SqlString $row.AddressFile), $(ConvertTo-SqlString $row.MusicName), $(ConvertTo-SqlString $row.EventSceneKey), $(ConvertTo-SqlString $row.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (map_id) DO UPDATE")
[void]$sql.AppendLine("SET scene_key = EXCLUDED.scene_key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    client_scene_id = EXCLUDED.client_scene_id,")
[void]$sql.AppendLine("    map_mode = EXCLUDED.map_mode,")
[void]$sql.AppendLine("    address_file = EXCLUDED.address_file,")
[void]$sql.AppendLine("    music_name = EXCLUDED.music_name,")
[void]$sql.AppendLine("    event_scene_key = EXCLUDED.event_scene_key,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS map_safe_areas (")
[void]$sql.AppendLine("    map_id smallint NOT NULL REFERENCES map_templates(map_id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    area_index smallint NOT NULL,")
[void]$sql.AppendLine("    x1 real NOT NULL,")
[void]$sql.AppendLine("    z1 real NOT NULL,")
[void]$sql.AppendLine("    x2 real NOT NULL,")
[void]$sql.AppendLine("    z2 real NOT NULL,")
[void]$sql.AppendLine("    attribute smallint,")
[void]$sql.AppendLine("    PRIMARY KEY (map_id, area_index)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO map_safe_areas (map_id, area_index, x1, z1, x2, z2, attribute)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $safeAreas.Count; $i++) {
    $row = $safeAreas[$i]
    $suffix = if ($i -eq $safeAreas.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.MapId), $($row.AreaIndex), $(ConvertTo-SqlReal $row.X1), $(ConvertTo-SqlReal $row.Z1), $(ConvertTo-SqlReal $row.X2), $(ConvertTo-SqlReal $row.Z2), $(ConvertTo-SqlNullableSmallint $row.Attribute))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (map_id, area_index) DO UPDATE")
[void]$sql.AppendLine("SET x1 = EXCLUDED.x1,")
[void]$sql.AppendLine("    z1 = EXCLUDED.z1,")
[void]$sql.AppendLine("    x2 = EXCLUDED.x2,")
[void]$sql.AppendLine("    z2 = EXCLUDED.z2,")
[void]$sql.AppendLine("    attribute = EXCLUDED.attribute;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS map_address_points (")
[void]$sql.AppendLine("    map_id smallint NOT NULL REFERENCES map_templates(map_id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    group_index smallint NOT NULL,")
[void]$sql.AppendLine("    point_index smallint NOT NULL,")
[void]$sql.AppendLine("    group_name varchar(128) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    name varchar(128) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    pos_x real NOT NULL,")
[void]$sql.AppendLine("    pos_z real NOT NULL,")
[void]$sql.AppendLine("    source varchar(255) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    PRIMARY KEY (map_id, group_index, point_index)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_map_address_points_name ON map_address_points (name);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO map_address_points (map_id, group_index, point_index, group_name, name, pos_x, pos_z, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $addressPoints.Count; $i++) {
    $row = $addressPoints[$i]
    $suffix = if ($i -eq $addressPoints.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.MapId), $($row.GroupIndex), $($row.PointIndex), $(ConvertTo-SqlString $row.GroupName), $(ConvertTo-SqlString $row.Name), $(ConvertTo-SqlReal $row.X), $(ConvertTo-SqlReal $row.Z), $(ConvertTo-SqlString $row.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (map_id, group_index, point_index) DO UPDATE")
[void]$sql.AppendLine("SET group_name = EXCLUDED.group_name,")
[void]$sql.AppendLine("    name = EXCLUDED.name,")
[void]$sql.AppendLine("    pos_x = EXCLUDED.pos_x,")
[void]$sql.AppendLine("    pos_z = EXCLUDED.pos_z,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS map_links (")
[void]$sql.AppendLine("    map_id smallint NOT NULL REFERENCES map_templates(map_id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    link_index smallint NOT NULL,")
[void]$sql.AppendLine("    target_map_id smallint NOT NULL,")
[void]$sql.AppendLine("    pos_x real NOT NULL,")
[void]$sql.AppendLine("    pos_z real NOT NULL,")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    PRIMARY KEY (map_id, link_index, target_map_id)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_map_links_target ON map_links (target_map_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO map_links (map_id, link_index, target_map_id, pos_x, pos_z, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $mapLinks.Count; $i++) {
    $row = $mapLinks[$i]
    $suffix = if ($i -eq $mapLinks.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.MapId), $($row.LinkIndex), $($row.TargetMapId), $(ConvertTo-SqlReal $row.X), $(ConvertTo-SqlReal $row.Z), $(ConvertTo-SqlString $row.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (map_id, link_index, target_map_id) DO UPDATE")
[void]$sql.AppendLine("SET pos_x = EXCLUDED.pos_x,")
[void]$sql.AppendLine("    pos_z = EXCLUDED.pos_z,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS map_routes (")
[void]$sql.AppendLine("    camp smallint NOT NULL,")
[void]$sql.AppendLine("    route_index smallint NOT NULL,")
[void]$sql.AppendLine("    map_ids smallint[] NOT NULL DEFAULT '{}',")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    PRIMARY KEY (camp, route_index)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO map_routes (camp, route_index, map_ids, source)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $mapRoutes.Count; $i++) {
    $row = $mapRoutes[$i]
    $suffix = if ($i -eq $mapRoutes.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine("    ($($row.Camp), $($row.RouteIndex), $(ConvertTo-SqlSmallintArray $row.MapIds), $(ConvertTo-SqlString $row.Source))$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (camp, route_index) DO UPDATE")
[void]$sql.AppendLine("SET map_ids = EXCLUDED.map_ids,")
[void]$sql.AppendLine("    source = EXCLUDED.source;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW map_template_summary AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    mt.map_id,")
[void]$sql.AppendLine("    mt.scene_key,")
[void]$sql.AppendLine("    mt.display_name,")
[void]$sql.AppendLine("    mt.client_scene_id,")
[void]$sql.AppendLine("    mt.map_mode,")
[void]$sql.AppendLine("    mt.music_name,")
[void]$sql.AppendLine("    mt.event_scene_key,")
[void]$sql.AppendLine("    COUNT(DISTINCT msa.area_index) AS safe_area_count,")
[void]$sql.AppendLine("    COUNT(DISTINCT map.group_index || ':' || map.point_index) AS address_point_count,")
[void]$sql.AppendLine("    COUNT(DISTINCT ml.link_index || ':' || ml.target_map_id) AS link_count")
[void]$sql.AppendLine("FROM map_templates mt")
[void]$sql.AppendLine("LEFT JOIN map_safe_areas msa ON msa.map_id = mt.map_id")
[void]$sql.AppendLine("LEFT JOIN map_address_points map ON map.map_id = mt.map_id")
[void]$sql.AppendLine("LEFT JOIN map_links ml ON ml.map_id = mt.map_id")
[void]$sql.AppendLine("GROUP BY mt.map_id, mt.scene_key, mt.display_name, mt.client_scene_id, mt.map_mode, mt.music_name, mt.event_scene_key;")

[System.IO.File]::WriteAllText($CSharpOutputPath, $csharp.ToString(), [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($SqlOutputPath, $sql.ToString(), [System.Text.Encoding]::UTF8)

Write-Host "Generated $($maps.Count) maps, $($safeAreas.Count) safe areas, $($addressPoints.Count) address points, $($mapLinks.Count) map links, and $($mapRoutes.Count) routes."
Write-Host "C#:  $CSharpOutputPath"
Write-Host "SQL: $SqlOutputPath"
