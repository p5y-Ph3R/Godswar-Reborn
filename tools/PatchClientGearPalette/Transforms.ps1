function Get-TextNewLine([string]$Text) {
    if ($Text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

function Assert-PaletteDefinitions {
    if ($script:QualityPalette.Count -ne 20) {
        throw 'The quality palette must define exactly Q01 through Q20.'
    }
    if ($script:GradePalette.Count -ne 25) {
        throw 'The grade palette must define exactly G01 through G25.'
    }

    $names = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal
    )
    foreach ($color in @($script:QualityPalette) + @($script:GradePalette)) {
        if (-not $names.Add([string]$color.Name)) {
            throw "Duplicate palette constant: $($color.Name)"
        }
        foreach ($channel in @('R', 'G', 'B')) {
            $value = [int]$color[$channel]
            if ($value -lt 0 -or $value -gt 255) {
                throw "$($color.Name) has an invalid $channel channel."
            }
        }
    }

    for ($level = 1; $level -le 20; $level++) {
        $expected = 'QUALITY_Q{0:D2}' -f $level
        if ($script:QualityPalette[$level - 1].Name -cne $expected) {
            throw "Quality palette entry $level must be named $expected."
        }
    }
    for ($level = 1; $level -le 25; $level++) {
        $expected = 'GRADE_G{0:D2}' -f $level
        if ($script:GradePalette[$level - 1].Name -cne $expected) {
            throw "Grade palette entry $level must be named $expected."
        }
    }

    # Terminal quality and grade are both displayed beside pale Common text in
    # the legacy UI. Keep them saturated and mutually distinct so a future
    # palette edit cannot silently turn either cap back into off-white.
    $common = $script:QualityPalette[0]
    $boundless = $script:QualityPalette[19]
    $grade25 = $script:GradePalette[24]
    foreach ($terminal in @($boundless, $grade25)) {
        $channels = @(
            [int]$terminal.R,
            [int]$terminal.G,
            [int]$terminal.B
        )
        $channelRange = $channels | Measure-Object -Maximum -Minimum
        if (($channelRange.Maximum - $channelRange.Minimum) -lt 120) {
            throw "$($terminal.Name) must remain a saturated cap color."
        }

        $distanceFromCommon =
            [Math]::Pow(([int]$terminal.R - [int]$common.R), 2) +
            [Math]::Pow(([int]$terminal.G - [int]$common.G), 2) +
            [Math]::Pow(([int]$terminal.B - [int]$common.B), 2)
        if ($distanceFromCommon -lt 10000) {
            throw "$($terminal.Name) is too close to Common/white."
        }
    }

    $terminalDistance =
        [Math]::Pow(([int]$boundless.R - [int]$grade25.R), 2) +
        [Math]::Pow(([int]$boundless.G - [int]$grade25.G), 2) +
        [Math]::Pow(([int]$boundless.B - [int]$grade25.B), 2)
    if ($terminalDistance -lt 10000) {
        throw 'Boundless and G25 must remain visually distinct.'
    }
}

function Set-SingleXmlAttributeText(
    [string]$Text,
    [string]$ElementName,
    [string]$AttributeName,
    [string]$Value,
    [string]$Label
) {
    $pattern = '(?s)(?<' +
        'prefix><' + [regex]::Escape($ElementName) +
        '\b[^>]*?\b' + [regex]::Escape($AttributeName) +
        '=")(?<value>[^"]*)(?<suffix>")'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw "$Label must have exactly one $ElementName/$AttributeName; found $($matches.Count)."
    }

    $match = $matches[0]
    $valueGroup = $match.Groups['value']
    return $Text.Substring(0, $valueGroup.Index) + $Value +
        $Text.Substring($valueGroup.Index + $valueGroup.Length)
}

function Get-PaletteNeutralItemColorText([string]$Text, [string]$Label) {
    $neutral = $Text
    for ($level = 1; $level -le 20; $level++) {
        $neutral = Set-SingleXmlAttributeText $neutral "BaseLevel$level" `
            'BaseColor' "__QUALITY_$level`__" $Label
    }
    for ($level = 1; $level -le 25; $level++) {
        $neutral = Set-SingleXmlAttributeText $neutral "AppLevel$level" `
            'AppendColor' "__GRADE_$level`__" $Label
    }
    return $neutral
}

function Assert-ItemColorText(
    [string]$Text,
    [string]$Locale,
    [bool]$RequireManagedMappings
) {
    try {
        [xml]$document = $Text
    }
    catch {
        throw "ItemColor.xml is not valid XML for $Locale`: $($_.Exception.Message)"
    }

    $baseNodes = @($document.SelectNodes('/ItemColor/Equip/Base/*'))
    $appendNodes = @($document.SelectNodes('/ItemColor/Equip/Append/*'))
    if ($baseNodes.Count -ne 20) {
        throw "ItemColor.xml $Locale must contain exactly 20 quality rows."
    }
    if ($appendNodes.Count -ne 32) {
        throw "ItemColor.xml $Locale must contain 25 grade rows and 7 elemental sentinels."
    }

    for ($level = 1; $level -le 20; $level++) {
        $nodes = @($document.SelectNodes(
                "/ItemColor/Equip/Base/BaseLevel$level"
            ))
        if ($nodes.Count -ne 1 -or
            $nodes[0].GetAttribute('BaseLv') -cne [string]$level) {
            throw "ItemColor.xml $Locale has an invalid quality row $level."
        }
        if ($RequireManagedMappings) {
            $expected = 'QUALITY_Q{0:D2}' -f $level
            if ($nodes[0].GetAttribute('BaseColor') -cne $expected) {
                throw "ItemColor.xml $Locale quality $level must use $expected."
            }
        }
    }

    for ($level = 1; $level -le 25; $level++) {
        $nodes = @($document.SelectNodes(
                "/ItemColor/Equip/Append/AppLevel$level"
            ))
        if ($nodes.Count -ne 1 -or
            $nodes[0].GetAttribute('AppendLv') -cne [string]$level -or
            $nodes[0].GetAttribute('AppendStar') -cne [string]$level) {
            throw "ItemColor.xml $Locale has an invalid grade row $level."
        }
        if ($RequireManagedMappings) {
            $expected = 'GRADE_G{0:D2}' -f $level
            if ($nodes[0].GetAttribute('AppendColor') -cne $expected) {
                throw "ItemColor.xml $Locale grade $level must use $expected."
            }
        }
    }

    for ($offset = 0; $offset -lt $script:ElementalSentinels.Count; $offset++) {
        $level = 26 + $offset
        $expected = $script:ElementalSentinels[$offset]
        $nodes = @($document.SelectNodes(
                "/ItemColor/Equip/Append/AppLevel$level"
            ))
        if ($nodes.Count -ne 1 -or
            $nodes[0].GetAttribute('AppendLv') -cne [string]$level -or
            $nodes[0].GetAttribute('AppendStar') -cne '25' -or
            $nodes[0].GetAttribute('AppendColor') -cne $expected -or
            $nodes[0].GetAttribute('AppAttributeColor') -cne $expected) {
            throw "ItemColor.xml $Locale elemental sentinel $level is invalid."
        }
    }
}

function Convert-ItemColorPaletteText([string]$Text, [string]$Locale) {
    Assert-ItemColorText $Text $Locale $false
    $neutralBefore = Get-PaletteNeutralItemColorText $Text $Locale
    $result = $Text

    for ($level = 1; $level -le 20; $level++) {
        $name = 'QUALITY_Q{0:D2}' -f $level
        $result = Set-SingleXmlAttributeText $result "BaseLevel$level" `
            'BaseColor' $name $Locale
    }
    for ($level = 1; $level -le 25; $level++) {
        $name = 'GRADE_G{0:D2}' -f $level
        $result = Set-SingleXmlAttributeText $result "AppLevel$level" `
            'AppendColor' $name $Locale
    }

    Assert-ItemColorText $result $Locale $true
    $neutralAfter = Get-PaletteNeutralItemColorText $result $Locale
    if ($neutralAfter -cne $neutralBefore) {
        throw "ItemColor.xml $Locale transform changed data outside gear palette references."
    }
    return $result
}

function Get-ManagedPaletteBlockMatch([string]$Text, [string]$Label) {
    $pattern = '(?ms)^' + [regex]::Escape($script:PaletteBlockBegin) +
        '\r?\n.*?^' + [regex]::Escape($script:PaletteBlockEnd) +
        '[ \t]*(?=\r?$)'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -gt 1) {
        throw "$Label contains multiple managed gear-palette blocks."
    }
    return $(if ($matches.Count -eq 1) { $matches[0] } else { $null })
}

function Remove-ManagedPaletteBlock([string]$Text, [string]$Label) {
    $match = Get-ManagedPaletteBlockMatch $Text $Label
    if ($null -eq $match) { return $Text }

    $end = $match.Index + $match.Length
    $newlineLength = 0
    for ($count = 0; $count -lt 2; $count++) {
        if ($end + 2 -le $Text.Length -and
            $Text.Substring($end, 2) -ceq "`r`n") {
            $newlineLength += 2
            $end += 2
        }
        elseif ($end -lt $Text.Length -and $Text[$end] -eq "`n") {
            $newlineLength++
            $end++
        }
        else { break }
    }
    return $Text.Substring(0, $match.Index) + $Text.Substring(
        $match.Index + $match.Length + $newlineLength
    )
}

function Get-SingleLuaAssignmentLine(
    [string]$Text,
    [string]$Name,
    [string]$Label
) {
    $pattern = '(?m)^[ \t]*' + [regex]::Escape($Name) +
        '[ \t]*=[^\r\n]*(?=\r?$)'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw "$Label must contain exactly one $Name assignment; found $($matches.Count)."
    }
    return $matches[0].Value
}

function Assert-FontPaletteText(
    [string]$Text,
    [string]$Locale,
    [bool]$RequireManagedPalette
) {
    foreach ($name in $script:ElementalSentinels) {
        [void](Get-SingleLuaAssignmentLine $Text $name "font.lua $Locale")
    }

    $managed = Get-ManagedPaletteBlockMatch $Text "font.lua $Locale"
    if (-not $RequireManagedPalette) { return }
    if ($null -eq $managed) {
        throw "font.lua $Locale does not contain the managed gear palette."
    }

    foreach ($color in @($script:QualityPalette) + @($script:GradePalette)) {
        $line = Get-SingleLuaAssignmentLine $Text $color.Name "font.lua $Locale"
        $expected = '{0}={{r={1},g={2},b={3},a=255}}' -f
            $color.Name, $color.R, $color.G, $color.B
        if ($line -cne $expected) {
            throw "font.lua $Locale has an invalid $($color.Name) value."
        }
    }
}

function Convert-FontPaletteText([string]$Text, [string]$Locale) {
    Assert-FontPaletteText $Text $Locale $false
    $elementalBefore = @{}
    foreach ($name in $script:ElementalSentinels) {
        $elementalBefore[$name] = Get-SingleLuaAssignmentLine $Text $name `
            "font.lua $Locale"
    }

    $newLine = Get-TextNewLine $Text
    $block = Get-GearPaletteLuaBlock $newLine
    $managed = Get-ManagedPaletteBlockMatch $Text "font.lua $Locale"
    $neutralBefore = Remove-ManagedPaletteBlock $Text "font.lua $Locale"

    if ($null -ne $managed) {
        $result = $Text.Substring(0, $managed.Index) + $block +
            $Text.Substring($managed.Index + $managed.Length)
    }
    else {
        $anchorPattern = '(?m)^[ \t]*BRONZE_COLOR[ \t]*='
        $anchors = [regex]::Matches($Text, $anchorPattern)
        if ($anchors.Count -ne 1) {
            throw "font.lua $Locale must contain exactly one BRONZE_COLOR anchor."
        }
        $result = $Text.Substring(0, $anchors[0].Index) + $block +
            $newLine + $newLine + $Text.Substring($anchors[0].Index)
    }

    Assert-FontPaletteText $result $Locale $true
    foreach ($name in $script:ElementalSentinels) {
        $after = Get-SingleLuaAssignmentLine $result $name "font.lua $Locale"
        if ($after -cne $elementalBefore[$name]) {
            throw "font.lua $Locale transform changed elemental color $name."
        }
    }
    $neutralAfter = Remove-ManagedPaletteBlock $result "font.lua $Locale"
    if ($neutralAfter -cne $neutralBefore) {
        throw "font.lua $Locale transform changed unrelated UI definitions."
    }
    return $result
}
