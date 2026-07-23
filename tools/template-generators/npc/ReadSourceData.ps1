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
