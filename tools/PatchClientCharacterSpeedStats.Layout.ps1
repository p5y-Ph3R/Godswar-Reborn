Set-StrictMode -Version Latest

function Get-RebornPersonalInfoRectanglePairs {
    return [ordered]@{
        StatusBack = @('19,78,243,173', '19,78,334,173')
        UnionBack = @('19,176,243,250', '19,176,334,250')
        LevelBack = @('19,253,243,327', '19,253,334,327')
        RoleNameText = @('5,51,258,67', '5,51,334,67')
        HP = @('24,334,62,350', '24,334,80,350')
        HPText = @('85,334,125,350', '84,334,160,350')
        MP = @('24,360,62,376', '24,360,80,376')
        MPText = @('85,360,125,376', '84,360,160,376')
        Attack = @('24,386,78,402', '24,386,80,402')
        AttackText = @('85,386,125,402', '84,386,160,402')
        Defend = @('24,412,78,428', '24,412,80,428')
        DefendText = @('85,412,125,428', '84,412,160,428')
        MagicAttack = @('24,438,78,454', '24,438,80,454')
        MagicAttackText = @('85,438,125,454', '84,438,160,454')
        MagicDefend = @('24,464,78,480', '24,464,80,480')
        MagicDefendText = @('85,464,125,480', '84,464,160,480')
        Cure = @('24,491,78,507', '24,491,80,507')
        CureText = @('85,491,125,507', '84,491,160,507')
        Hit = @('137,334,170,350', '173,334,253,350')
        HitText = @('210,334,273,350', '257,334,333,350')
        Dodge = @('137,360,170,376', '173,360,253,376')
        DodgeText = @('210,360,273,376', '257,360,333,376')
        CritAppend = @('137,386,170,402', '173,386,253,402')
        CritAppendText = @('210,386,273,402', '257,386,333,402')
        CritDefend = @('137,412,170,428', '173,412,253,428')
        CritDefendText = @('210,412,273,428', '257,412,333,428')
        PhyDamageAppend = @('137,438,200,454', '173,438,253,454')
        PhyDamageAppendText = @('210,438,246,454', '257,438,333,454')
        MagicDamageAppend = @('137,464,200,480', '173,464,253,480')
        MagicDamageAppendText = @('210,464,246,480', '257,464,333,480')
        DamageSorb = @('137,491,200,507', '173,491,253,507')
        DamageSorbText = @('210,491,246,507', '257,491,333,507')
    }
}

function Get-RebornPersonalInfoLayoutTemplate([string]$Name) {
    if ($Name -in 'StatusBack', 'UnionBack', 'LevelBack') {
        return 'T_BgWindow'
    }
    if ($Name -eq 'RoleNameText') { return 'T_NoBackgroundText' }
    return 'T_Money'
}

function ConvertFrom-RebornRectangle([string]$Rectangle) {
    $match = [regex]::Match(
        $Rectangle, '^(-?\d+),(-?\d+),(-?\d+),(-?\d+)$')
    if (-not $match.Success) { return $null }
    return ,([int[]]@(
            $match.Groups[1].Value,
            $match.Groups[2].Value,
            $match.Groups[3].Value,
            $match.Groups[4].Value))
}

function Test-RebornPersonalInfoFrameInsets([Xml.XmlDocument]$Document) {
    $roots = @($Document.SelectNodes('/UIConfig/PersonalInfo'))
    if ($roots.Count -ne 1) { return $false }
    $root = $roots[0]
    $rootRectangle = ConvertFrom-RebornRectangle (
        $root.GetAttribute('Rectangle'))
    $buttonRectangle = ConvertFrom-RebornRectangle (
        $root.GetAttribute('BtnRect'))
    if ($null -eq $rootRectangle -or $null -eq $buttonRectangle) {
        return $false
    }

    $backgroundRight = [int]::MinValue
    $backgroundBottom = [int]::MinValue
    foreach ($name in 'StatusBack', 'UnionBack', 'LevelBack', 'BaseBack',
        'FightBack') {
        $nodes = @($root.SelectNodes("./$name"))
        if ($nodes.Count -ne 1) { return $false }
        $rectangle = ConvertFrom-RebornRectangle (
            $nodes[0].GetAttribute('Rectangle'))
        if ($null -eq $rectangle) { return $false }
        $backgroundRight = [math]::Max($backgroundRight, $rectangle[2])
        $backgroundBottom = [math]::Max($backgroundBottom, $rectangle[3])
    }

    $width = $rootRectangle[2] - $rootRectangle[0]
    $height = $rootRectangle[3] - $rootRectangle[1]
    return $width - $backgroundRight -eq 20 -and
        $height - $backgroundBottom -eq 16 -and
        $width - $buttonRectangle[2] -eq 30
}

function Test-RebornPersonalInfoRectangles(
    [Xml.XmlDocument]$Document,
    [bool]$Wide,
    [string]$Text
) {
    $index = if ($Wide) { 1 } else { 0 }
    $allElements = @($Document.SelectNodes('//*'))
    $nonLiveNodes = @($Document.SelectNodes('//comment()')) + @(
        $Document.SelectNodes('//processing-instruction()')) + @(
        $Document.SelectNodes('//text()') | Where-Object {
            $_.NodeType -eq [Xml.XmlNodeType]::CDATA
        })
    foreach ($entry in (Get-RebornPersonalInfoRectanglePairs).GetEnumerator()) {
        $name = $entry.Key
        $nodes = @($allElements | Where-Object { $_.LocalName -ieq $name })
        if ($nodes.Count -ne 1) { return $false }
        $node = $nodes[0]
        $expectedTemplate = Get-RebornPersonalInfoLayoutTemplate $name
        $expectedRectangle = $entry.Value[$index]
        $templateAttributes = @($node.Attributes | Where-Object {
                $_.LocalName -ieq 'Template'
            })
        $rectangleAttributes = @($node.Attributes | Where-Object {
                $_.LocalName -ieq 'Rectangle'
            })
        if ($node.Name -cne $name -or
            -not [string]::IsNullOrEmpty($node.NamespaceURI) -or
            $node.ParentNode.Name -cne 'PersonalInfo' -or
            $templateAttributes.Count -ne 1 -or
            $templateAttributes[0].LocalName -cne 'Template' -or
            $rectangleAttributes.Count -ne 1 -or
            $rectangleAttributes[0].LocalName -cne 'Rectangle' -or
            $node.GetAttribute('Template') -cne $expectedTemplate -or
            $node.GetAttribute('Rectangle') -cne $expectedRectangle) {
            return $false
        }
        $escapedName = [regex]::Escape($name)
        $rawLines = [regex]::Matches($Text,
            "(?m)^[ \t]*<$escapedName\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)")
        $canonicalLines = @($rawLines | Where-Object {
                $_.Value.Contains("Template=`"$expectedTemplate`"") -and
                $_.Value.Contains("Rectangle=`"$expectedRectangle`"")
            })
        if ($rawLines.Count -ne 1 -or $canonicalLines.Count -ne 1) {
            return $false
        }
        foreach ($nonLive in $nonLiveNodes) {
            if ($nonLive.Value -imatch "<$escapedName\b" -and
                $nonLive.Value.Contains("Template=`"$expectedTemplate`"") -and
                $nonLive.Value.Contains(
                    "Rectangle=`"$expectedRectangle`"")) {
                return $false
            }
        }
    }
    return $true
}

function Convert-RebornPersonalInfoRectangles(
    [string]$Text,
    [bool]$ToWide
) {
    $from = if ($ToWide) { 0 } else { 1 }
    $to = if ($ToWide) { 1 } else { 0 }
    foreach ($entry in (Get-RebornPersonalInfoRectanglePairs).GetEnumerator()) {
        $name = $entry.Key
        $escapedName = [regex]::Escape($name)
        $template = Get-RebornPersonalInfoLayoutTemplate $name
        $fromRectangle = $entry.Value[$from]
        $toRectangle = $entry.Value[$to]
        $Text = Update-RegexOnce $Text (
            "(?m)^[ \t]*<$escapedName\b[^\r\n]*/>[ \t]*(?=\r?\n|\z)") {
            param($line)
            $fromText = "Rectangle=`"$fromRectangle`""
            $templateText = "Template=`"$template`""
            if (([regex]::Matches($line, '(?i:\sTemplate\s*=)')).Count -ne 1 -or
                -not $line.Contains($templateText) -or
                ([regex]::Matches($line, '(?i:\sRectangle\s*=)')).Count -ne 1 -or
                -not $line.Contains($fromText)) {
                throw "$name has a noncanonical source Rectangle."
            }
            $line.Replace($fromText, "Rectangle=`"$toRectangle`"")
        } "$name layout control"
    }
    return $Text
}
