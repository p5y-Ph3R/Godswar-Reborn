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

$magicSections = [ordered]@{}
$currentMagicSection = $null
foreach ($line in Get-Content $magicIniPath) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith(";") -or $trimmed.StartsWith("#")) {
        continue
    }

    if ($trimmed -match '^\[(.+)\]$') {
        $currentMagicSection = $Matches[1]
        $magicSections[$currentMagicSection] = [ordered]@{}
        continue
    }

    if ($currentMagicSection -and $trimmed -match '^([^=]+)=(.*)$') {
        $magicSections[$currentMagicSection][$Matches[1].Trim()] = $Matches[2].Trim()
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

$backhaulMagicIds = @()
foreach ($line in Get-Content $skillIniPath) {
    if ($line.Trim() -match '^backhaul_magic=([^;]+)') {
        $backhaulMagicIds = @(
            $Matches[1].Split(
                ',',
                [StringSplitOptions]::RemoveEmptyEntries) |
                ForEach-Object { [int]$_.Trim() }
        )
        break
    }
}
if ($backhaulMagicIds.Count -eq 0) {
    throw "Skill.ini does not define any backhaul_magic skills."
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

$bookBackedSkillIds =
    [System.Collections.Generic.HashSet[int]]::new(
        [int[]]@($skillBooks | ForEach-Object { $_.SkillId }))
$skills = @(
    foreach ($group in ($skillBooks | Group-Object SkillId | Sort-Object { [int]$_.Name })) {
        $books = @($group.Group | Sort-Object ItemId)
        $first = $books[0]
        $classIds = [Int16[]]@($books | ForEach-Object { $_.ClassIds } | Sort-Object -Unique)
        $bookItemIds = [int[]]@($books | ForEach-Object { $_.ItemId } | Sort-Object -Unique)
        $nameKeys = [string[]]@($books | ForEach-Object { $_.NameKey } | Sort-Object -Unique)
        $skillId = [int]$group.Name
        $magic = $magicSections[[string]$skillId]
        if ($null -eq $magic) {
            throw "Magic.ini does not contain a combat definition for skill ID $skillId."
        }

        $target = ConvertTo-RequiredMagicInt $magic "Target" $skillId
        $affectObj = ConvertTo-RequiredMagicInt $magic "AffectObj" $skillId
        $distance = ConvertTo-RequiredMagicDecimal $magic "Distance" $skillId
        $range = ConvertTo-RequiredMagicDecimal $magic "Range" $skillId
        $property = ConvertTo-RequiredMagicInt $magic "Property" $skillId
        $mp = ConvertTo-RequiredMagicInt $magic "MP" $skillId
        $power1 = ConvertTo-RequiredMagicDecimal $magic "Power1" $skillId
        $power2 = ConvertTo-RequiredMagicDecimal $magic "Power2" $skillId
        $intonateTime = ConvertTo-RequiredMagicDecimal $magic "IntonateTime" $skillId
        $coolingTime = ConvertTo-RequiredMagicDecimal $magic "CoolingTime" $skillId
        $stats = [ordered]@{
            Source = "Magic.ini+ItemBaseAttribute.SkillBook+SkillInfo.dat"
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
            Target = $target
            AffectObj = $affectObj
            Distance = $distance
            Range = $range
            Property = $property
            Mp = $mp
            Power1 = $power1
            Power2 = $power2
            IntonateTime = $intonateTime
            CoolingTime = $coolingTime
            StatsJson = ($stats | ConvertTo-Json -Compress)
        }
    }

    # The client includes permanent return skills that are learned without an
    # item-backed skill-book row. Preserve those native protocol IDs in the
    # authoritative template catalog instead of silently dropping them.
    foreach ($skillId in ($backhaulMagicIds | Sort-Object -Unique)) {
        if ($bookBackedSkillIds.Contains($skillId)) {
            continue
        }

        $magic = $magicSections[[string]$skillId]
        if ($null -eq $magic) {
            throw "Magic.ini does not contain a backhaul definition for skill ID $skillId."
        }

        $stats = [ordered]@{
            Source = "Magic.ini+Skill.ini.backhaul_magic+SkillInfo.dat"
            ScriptID = Get-AttributeValue $magic "ScriptID"
            Kind = ConvertTo-RequiredMagicInt $magic "Kind" $skillId
            IntonateTime = ConvertTo-RequiredMagicDecimal $magic "IntonateTime" $skillId
            CoolingTime = ConvertTo-RequiredMagicDecimal $magic "CoolingTime" $skillId
        }
        $displayName = Get-AttributeValue $magic "Name"

        [pscustomobject]@{
            SkillId = $skillId
            DisplayName = $displayName
            BaseName = $displayName
            SkillLevel = $null
            ClassIds = [Int16[]]@(0, 1, 2, 3)
            PreviousSkillId = $null
            MinLevel = 1
            MaxLevel = 200
            Description = if ($skillDescriptions.ContainsKey($skillId)) { $skillDescriptions[$skillId] } else { "" }
            Target = ConvertTo-RequiredMagicInt $magic "Target" $skillId
            AffectObj = ConvertTo-RequiredMagicInt $magic "AffectObj" $skillId
            Distance = ConvertTo-RequiredMagicDecimal $magic "Distance" $skillId
            Range = ConvertTo-RequiredMagicDecimal $magic "Range" $skillId
            Property = ConvertTo-RequiredMagicInt $magic "Property" $skillId
            Mp = ConvertTo-RequiredMagicInt $magic "MP" $skillId
            Power1 = ConvertTo-RequiredMagicDecimal $magic "Power1" $skillId
            Power2 = ConvertTo-RequiredMagicDecimal $magic "Power2" $skillId
            IntonateTime = ConvertTo-RequiredMagicDecimal $magic "IntonateTime" $skillId
            CoolingTime = ConvertTo-RequiredMagicDecimal $magic "CoolingTime" $skillId
            StatsJson = ($stats | ConvertTo-Json -Compress)
        }
    }
) | Sort-Object SkillId
