Set-StrictMode -Version Latest

function Update-RebornPersonalInfoRectangle(
    [string]$Text,
    [string]$FromPattern,
    [string]$Replacement,
    [string]$Label
) {
    return Update-RegexOnce $Text (
        '(?m)^[ \t]*<PersonalInfo\b[^\r\n]*>[ \t]*(?=\r?\n|\z)') {
        param($line)
        Replace-RegexOnce $line $FromPattern $Replacement $Label
    } "$Label root"
}

function Test-RebornCallbackMultiset(
    [string[]]$Actual,
    [string[]]$Expected
) {
    if ($Actual.Count -ne $Expected.Count) { return $false }
    $counts = [Collections.Generic.Dictionary[string,int]]::new(
        [StringComparer]::Ordinal)
    foreach ($value in $Actual) {
        if ($counts.ContainsKey($value)) { $counts[$value]++ }
        else { $counts[$value] = 1 }
    }
    foreach ($value in $Expected) {
        if (-not $counts.ContainsKey($value) -or $counts[$value] -eq 0) {
            return $false
        }
        $counts[$value]--
    }
    return $true
}

function Get-RebornPersonalInfoXmlValidation([string]$Text) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 4MB
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.PreserveWhitespace = $true
    $stringReader = [IO.StringReader]::new($Text)
    $xmlReader = $null
    try {
        $xmlReader = [Xml.XmlReader]::Create($stringReader, $settings)
        $document.Load($xmlReader)
    }
    catch {
        throw [IO.InvalidDataException]::new(
            'PersonalInfoUI.xml is not secure, well-formed XML.', $_.Exception)
    }
    finally {
        if ($null -ne $xmlReader) { $xmlReader.Dispose() }
        $stringReader.Dispose()
    }
    if ($null -eq $document.DocumentElement -or
        $document.DocumentElement.Name -cne 'UIConfig') {
        throw 'PersonalInfoUI.xml must have one exact UIConfig root.'
    }

    $allElements = @($document.SelectNodes('//*'))
    $structureValid = $true
    foreach ($name in @(
        'PersonalInfo', 'BaseBack', 'FightBack', 'Recommend', 'spouse',
        'spouseText', 'SpeedBack', 'MovementSpeedPercent', 'RidingSpeed',
        'RidingSpeedPercent', 'Penetration',
        'RebornPersonalInfoStatsUpdater')) {
        $textMatches = Get-RebornXmlElementLines $Text $name
        $liveMatches = @($allElements | Where-Object {
                $_.LocalName -ieq $name
            })
        if ($textMatches.Count -ne $liveMatches.Count) {
            $structureValid = $false
            continue
        }
        foreach ($node in $liveMatches) {
            $expectedParent = if ($name -ceq 'PersonalInfo') {
                'UIConfig'
            } else { 'PersonalInfo' }
            if ($node.LocalName -cne $name -or
                $node.ParentNode.LocalName -cne $expectedParent) {
                $structureValid = $false
            }
        }
    }

    $ownedScriptText = [regex]::Matches($Text,
        '(?m)^[ \t]*<Script\b[^\r\n]*(?i:PersonalInfoSpeedStats\.lua)[^\r\n]*/>[ \t]*(?=\r?\n|\z)')
    $ownedScriptNodes = @($allElements | Where-Object {
            $_.LocalName -ieq 'Script' -and
            $_.GetAttribute('File') -imatch 'PersonalInfoSpeedStats\.lua'
        })
    if ($ownedScriptText.Count -ne $ownedScriptNodes.Count) {
        $structureValid = $false
    }
    foreach ($node in $ownedScriptNodes) {
        if ($node.LocalName -cne 'Script' -or
            $node.ParentNode.LocalName -cne 'UIConfig') {
            $structureValid = $false
        }
    }

    $commentTokenPattern = '(?i)\b(?:PersonalInfo|BaseBack|FightBack|Recommend|spouse|spouseText|SpeedBack|MovementSpeedPercent|RidingSpeed|RidingSpeedPercent|Penetration|RebornPersonalInfo[A-Za-z0-9_]*|PersonalInfoSpeedStats)\b'
    $nonLiveNodes = @($document.SelectNodes('//comment()')) + @(
        $document.SelectNodes('//processing-instruction()')) + @(
        $document.SelectNodes('//text()') | Where-Object {
            $_.NodeType -eq [Xml.XmlNodeType]::CDATA
        })
    foreach ($node in $nonLiveNodes) {
        if ($node.Value -imatch $commentTokenPattern) {
            $structureValid = $false
        }
    }

    $callbacks = [Collections.Generic.List[string]]::new()
    foreach ($element in $allElements) {
        foreach ($attribute in @($element.Attributes)) {
            foreach ($match in [regex]::Matches($attribute.Value,
                    '(?i)\bRebornPersonalInfo[A-Za-z0-9_]*\b')) {
                $callbacks.Add($match.Value)
            }
        }
    }
    $legacyCallbacks = @(
        'RebornPersonalInfoMovementSpeedHovered',
        'RebornPersonalInfoRidingSpeedHovered',
        'RebornPersonalInfoSpeedLeft',
        'RebornPersonalInfoSpeedLeft')
    $sid200Callbacks = @(
        'RebornPersonalInfoStatsLoad',
        'RebornPersonalInfoStatsClose',
        'RebornPersonalInfoSpeedHovered',
        'RebornPersonalInfoStatsLeft',
        'RebornPersonalInfoPenetrationHovered',
        'RebornPersonalInfoStatsLeft',
        'RebornPersonalInfoStatsUpdate')
    return [pscustomobject]@{
        Document = $document
        StructureValid = $structureValid
        NoOwnedCallbacks = $callbacks.Count -eq 0
        LegacyCallbacks = Test-RebornCallbackMultiset (
            $callbacks.ToArray()) $legacyCallbacks
        Sid200Callbacks = Test-RebornCallbackMultiset (
            $callbacks.ToArray()) $sid200Callbacks
    }
}
