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
