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
