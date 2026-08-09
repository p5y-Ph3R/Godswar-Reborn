[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$items = @(
    [pscustomobject]@{
        Id = 9032; Key = 'Stone9032'; Name = 'Zephyr Holy Stone'
        Icon = '612,0'; Overlap = 1; SpecialFlag = 'PreStone'
        Description = 'A Zephyr-affinity Holy Stone for mount gear. The Holy Stone Artisan can drill two Zephyr sockets into mount gear, implement Zephyr Spirits, and mount the finished stone. Its effects remain active while a compatible mount is equipped, even when you are not riding.'
    },
    [pscustomobject]@{
        Id = 9090; Key = 'Zephyrholiness1'
        Name = 'Daedalus Spirit of Attunement'; Icon = '648,0'
        Overlap = 99; SpecialFlag = $null
        Description = "A Zephyr Spirit that increases only the native quality stat of its host mount gear. Implement it into a Zephyr Holy Stone before mounting."
    },
    [pscustomobject]@{
        Id = 9091; Key = 'Zephyrholiness2'
        Name = 'Hephaestus Spirit of Tempering'; Icon = '684,0'
        Overlap = 99; SpecialFlag = $null
        Description = "A Zephyr Spirit that increases only the ordinary grade attributes of its host mount gear. Implement it into a Zephyr Holy Stone before mounting."
    },
    [pscustomobject]@{
        Id = 9092; Key = 'Zephyrholiness3'
        Name = 'Mnemosyne Spirit of Preservation'; Icon = '720,0'
        Overlap = 99; SpecialFlag = $null
        Description = 'A Zephyr Spirit that reduces MP removed by eligible hostile mana-burn effects. It cannot restore or generate mana.'
    },
    [pscustomobject]@{
        Id = 9093; Key = 'Zephyrholiness4'
        Name = 'Themis Spirit of Continuity'; Icon = '756,0'
        Overlap = 99; SpecialFlag = $null
        Description = "A Zephyr Spirit that reduces only extra cooldown imposed by eligible hostile cooldown-extension effects. It cannot shorten a skill's ordinary cooldown."
    }
)

$socketText = [ordered]@{
    EquipStoneName21 = 'Daedalus Spirit of Attunement'
    EquipStoneName22 = 'Hephaestus Spirit of Tempering'
    EquipStoneName23 = 'Mnemosyne Spirit of Preservation'
    EquipStoneName24 = 'Themis Spirit of Continuity'
    EquipStoneDesc21 = "Increase the host mount gear's native quality stat"
    EquipStoneDesc22 = "Increase the host mount gear's ordinary grade attributes"
    EquipStoneDesc23 = 'Reduce hostile mana burn'
    EquipStoneDesc24 = 'Reduce hostile cooldown extension'
}

function Read-PreservedText {
    param([Parameter(Mandatory)][string]$Path)

    $rawBytes = [IO.File]::ReadAllBytes($Path)
    $reader = [IO.StreamReader]::new($Path, $true)
    try {
        $text = $reader.ReadToEnd()
        [byte[]]$encodingPreamble = $reader.CurrentEncoding.GetPreamble()
        $hasPreamble = $encodingPreamble.Length -gt 0 -and
            $rawBytes.Length -ge $encodingPreamble.Length
        for ($index = 0; $hasPreamble -and
            $index -lt $encodingPreamble.Length; $index++) {
            if ($rawBytes[$index] -ne $encodingPreamble[$index]) {
                $hasPreamble = $false
            }
        }
        return [pscustomobject]@{
            Text = $text
            Encoding = $reader.CurrentEncoding
            HasPreamble = $hasPreamble
            NewLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Write-PreservedText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][Text.Encoding]$Encoding,
        [Parameter(Mandatory)][bool]$HasPreamble
    )

    $temporaryPath = Join-Path (Split-Path -Parent $Path) (
        '.{0}.{1}.tmp' -f [IO.Path]::GetFileName($Path),
            [Guid]::NewGuid().ToString('N'))
    try {
        [byte[]]$preamble = @()
        if ($HasPreamble) {
            $preamble = $Encoding.GetPreamble()
        }
        [byte[]]$body = $Encoding.GetBytes($Text)
        [byte[]]$output = [byte[]]::new($preamble.Length + $body.Length)
        if ($preamble.Length -gt 0) {
            [Array]::Copy($preamble, 0, $output, 0, $preamble.Length)
        }
        [Array]::Copy($body, 0, $output, $preamble.Length, $body.Length)
        [IO.File]::WriteAllBytes($temporaryPath, $output)
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Add-OrValidateDatEntry {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$NewLine
    )

    $expected = "$Key`t$Value"
    $matches = [regex]::Matches(
        $Text,
        '(?m)^' + [regex]::Escape($Key) + "`t[^`r`n]*(?=`r?$)")
    if ($matches.Count -gt 1) {
        throw "Duplicate localization key $Key."
    }
    if ($matches.Count -eq 1) {
        if ($matches[0].Value -cne $expected) {
            throw "Localization key $Key conflicts with the Zephyr definition."
        }
        return $Text
    }

    return $Text.TrimEnd("`r", "`n") + $NewLine + $expected + $NewLine
}

function Add-OrValidateLuaAssignment {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$AnchorKey,
        [Parameter(Mandatory)][string]$NewLine
    )

    $expected = $Key + ' = "' + $Value + '"'
    $matches = [regex]::Matches(
        $Text,
        '(?m)^[ \t]*' + [regex]::Escape($Key) +
            '[ \t]*=[ \t]*"[^"]*"[ \t]*(?=\r?$)')
    if ($matches.Count -gt 1) {
        throw "Duplicate Lua localization key $Key."
    }
    if ($matches.Count -eq 1) {
        if ($matches[0].Value.Trim() -cne $expected) {
            throw "Lua localization key $Key conflicts with the Zephyr definition."
        }
        return $Text
    }

    $anchor = [regex]::Match(
        $Text,
        '(?m)^[ \t]*' + [regex]::Escape($AnchorKey) +
            '[ \t]*=[ \t]*"[^"]*"[ \t]*(?=\r?$)')
    if (-not $anchor.Success) {
        throw "Lua localization anchor $AnchorKey is missing."
    }
    return $Text.Insert(
        $anchor.Index + $anchor.Length,
        $NewLine + $expected)
}

function Replace-OrValidateToken {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Original,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    $expectedCount = [regex]::Matches(
        $Text,
        [regex]::Escape($Expected)).Count
    if ($expectedCount -eq 1) {
        return $Text
    }
    if ($expectedCount -ne 0) {
        throw "$Label has duplicate installed definitions."
    }

    $originalCount = [regex]::Matches(
        $Text,
        [regex]::Escape($Original)).Count
    if ($originalCount -ne 1) {
        throw "$Label does not match the reviewed stock client definition."
    }
    return $Text.Replace($Original, $Expected)
}

function Add-ZephyrStoneResultBranch {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][ValidateSet(4, 5)][int]$Suffix,
        [Parameter(Mandatory)][string]$ExistingTextKey,
        [Parameter(Mandatory)][string]$ZephyrTextKey,
        [Parameter(Mandatory)][string]$NewLine
    )

    $sentinel =
        'elseif \( SubID - ' + $Suffix +
        ' \) / 100 == 9032 then'
    $sentinelCount = [regex]::Matches($Text, $sentinel).Count
    if ($sentinelCount -eq 1) {
        return $Text
    }
    if ($sentinelCount -ne 0) {
        throw "Zephyr result suffix $Suffix has duplicate branches."
    }

    $pattern =
        '(?ms)(^[ \t]*elseif \( SubID - ' + $Suffix +
        ' \) / 100 == 9031 then\r?\n' +
        '[ \t]*FirstWin_Text1:SetText\(' +
        [regex]::Escape($ExistingTextKey) + '\);\r?\n' +
        '[ \t]*FirstWin_Text1:Visible\(true\);\r?\n' +
        '[ \t]*FirstWin_Text1:SetPosition\(25,220\);)' +
        '(\r?\n[ \t]*end;)'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success -or
        [regex]::Matches($Text, $pattern).Count -ne 1) {
        throw "Zephyr result suffix $Suffix stock branch is missing or ambiguous."
    }

    $branch =
        $NewLine + "`t   elseif ( SubID - $Suffix ) / 100 == 9032 then" +
        $NewLine + "`t      FirstWin_Text1:SetText($ZephyrTextKey);" +
        $NewLine + "`t      FirstWin_Text1:Visible(true);" +
        $NewLine + "`t      FirstWin_Text1:SetPosition(25,220);"
    $replacement =
        $match.Groups[1].Value + $branch + $match.Groups[2].Value
    return $Text.Remove($match.Index, $match.Length).Insert(
        $match.Index,
        $replacement)
}

function Install-ZephyrResultDecoder {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$NewLine
    )

    $updated = Replace-OrValidateToken `
        -Text $Text `
        -Original '[14]={40,100}}' `
        -Expected '[14]={40,100},[21]={15,30},[22]={10,20},[23]={100,200},[24]={75,150}}' `
        -Label 'Zephyr effectiveness table'
    $updated = Replace-OrValidateToken `
        -Text $updated `
        -Original '[14]=NF_L0_ZBXQ1403 }' `
        -Expected '[14]=NF_L0_ZBXQ1403, [21]=NF_L0_ZBXQ2103, [22]=NF_L0_ZBXQ2203, [23]=NF_L0_ZBXQ2303, [24]=NF_L0_ZBXQ2403 }' `
        -Label 'Zephyr result-text table'
    $updated = Replace-OrValidateToken `
        -Text $updated `
        -Original 'or stil_2 ==13 or stil_2 ==3 or stil_2 ==4 then' `
        -Expected 'or stil_2 ==13 or stil_2 ==3 or stil_2 ==4 or stil_2 ==21 or stil_2 ==22 or stil_2 ==23 or stil_2 ==24 then' `
        -Label 'Zephyr percentage-result branch'
    $updated = Add-ZephyrStoneResultBranch `
        -Text $updated `
        -Suffix 4 `
        -ExistingTextKey 'NF_L0_ZBXQ903104' `
        -ZephyrTextKey 'NF_L0_ZBXQ903204' `
        -NewLine $NewLine
    return Add-ZephyrStoneResultBranch `
        -Text $updated `
        -Suffix 5 `
        -ExistingTextKey 'NF_L0_ZBXQ903105' `
        -ZephyrTextKey 'NF_L0_ZBXQ903205' `
        -NewLine $NewLine
}

function Update-Locale {
    param([Parameter(Mandatory)][string]$LocaleRoot)

    $itemPath = Join-Path $LocaleRoot 'Settings\Sys\ItemBaseAttribute.xml'
    $namePath = Join-Path $LocaleRoot 'Text\EquipName.dat'
    $descriptionPath = Join-Path $LocaleRoot 'Text\EquipDescription.dat'
    $luaTextPath = Join-Path $LocaleRoot 'UI\Base\LuaText.lua'
    $npcFunctionPath = Join-Path $LocaleRoot `
        'UI\XML\NpcFun\NpcFunEment.lua'
    foreach ($path in @(
        $itemPath,
        $namePath,
        $descriptionPath,
        $luaTextPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required client localization file is missing: $path"
        }
    }

    $itemFile = Read-PreservedText -Path $itemPath
    $itemText = $itemFile.Text
    $expectedDefinitions = [Collections.Generic.List[string]]::new()
    foreach ($item in $items) {
        $idMatches = [regex]::Matches(
            $itemText,
            '(?m)^\s*<[^>]+\sID="' + $item.Id + '"[^>]*/>\s*$')
        if ($idMatches.Count -gt 1) {
            throw "Duplicate ItemBaseAttribute ID $($item.Id)."
        }
        $flag = if ($null -eq $item.SpecialFlag) {
            ''
        }
        else {
            ' SpecialFlag="' + $item.SpecialFlag + '"'
        }
        $expected = '        <' + $item.Key + ' ID="' + $item.Id +
            '" Type="consume item" Texture="./Localization/en_us/UI/Texture/Icon5.gwo"' +
            ' Icon="' + $item.Icon + '" Random="0" Distribution="0,0"' +
            ' Money="0" Overlap="' + $item.Overlap + '"' + $flag + '/>'
        $expectedDefinitions.Add($expected)
        if ($idMatches.Count -eq 1) {
            if ($idMatches[0].Value.Trim() -cne $expected.Trim()) {
                throw "ItemBaseAttribute ID $($item.Id) conflicts with the Zephyr definition."
            }
        }
    }

    # The original client indexes consumables only when they are children of
    # an Item section. Relocate old direct-root installs before inserting the
    # definitions beside the native Holy Stone entries.
    foreach ($item in $items) {
        $pattern =
            '[ \t]*<[^>]+\sID="' + $item.Id +
            '"[^>]*/>[ \t]*(?:\r?\n)?'
        $itemText = [regex]::Replace($itemText, $pattern, '')
    }

    $stoneAnchor = [regex]::Match(
        $itemText,
        '<[^>]+\sID="9031"[^>]*/>')
    $spiritAnchor = [regex]::Match(
        $itemText,
        '<[^>]+\sID="9089"[^>]*/>')
    if (-not $stoneAnchor.Success -or -not $spiritAnchor.Success) {
        throw "Native Holy Stone insertion anchors are missing: $itemPath"
    }

    $stoneInsertIndex = $stoneAnchor.Index + $stoneAnchor.Length
    $stoneSuffix = if ($itemText.Substring($stoneInsertIndex).StartsWith(
        $itemFile.NewLine,
        [StringComparison]::Ordinal)) {
        ''
    }
    else {
        $itemFile.NewLine
    }
    $itemText = $itemText.Insert(
        $stoneInsertIndex,
        $itemFile.NewLine + $expectedDefinitions[0] + $stoneSuffix)
    $spiritAnchor = [regex]::Match(
        $itemText,
        '<[^>]+\sID="9089"[^>]*/>')
    $spiritInsertIndex = $spiritAnchor.Index + $spiritAnchor.Length
    $spiritSuffix = if ($itemText.Substring($spiritInsertIndex).StartsWith(
        $itemFile.NewLine,
        [StringComparison]::Ordinal)) {
        ''
    }
    else {
        $itemFile.NewLine
    }
    $itemText = $itemText.Insert(
        $spiritInsertIndex,
        $itemFile.NewLine +
            ($expectedDefinitions.GetRange(1, 4) -join $itemFile.NewLine) +
            $spiritSuffix)

    [xml]$parsedItemText = $itemText
    foreach ($item in $items) {
        $node = $parsedItemText.SelectSingleNode(
            "/ItemBaseAttribute/Item/*[@ID='$($item.Id)']")
        if ($null -eq $node) {
            throw "Zephyr item $($item.Id) is not inside an Item section: $itemPath"
        }
    }

    $nameFile = Read-PreservedText -Path $namePath
    $nameText = $nameFile.Text
    foreach ($item in $items) {
        $nameText = Add-OrValidateDatEntry -Text $nameText -Key $item.Key `
            -Value $item.Name -NewLine $nameFile.NewLine
    }

    $descriptionFile = Read-PreservedText -Path $descriptionPath
    $descriptionText = $descriptionFile.Text
    foreach ($item in $items) {
        $descriptionText = Add-OrValidateDatEntry `
            -Text $descriptionText `
            -Key $item.Key `
            -Value $item.Description `
            -NewLine $descriptionFile.NewLine
    }
    foreach ($entry in $socketText.GetEnumerator()) {
        $descriptionText = Add-OrValidateDatEntry `
            -Text $descriptionText `
            -Key $entry.Key `
            -Value $entry.Value `
            -NewLine $descriptionFile.NewLine
    }

    $luaTextFile = Read-PreservedText -Path $luaTextPath
    $luaText = Add-OrValidateLuaAssignment `
        -Text $luaTextFile.Text `
        -Key 'hallo_9032' `
        -Value 'Zephyr Holy Stone (Level 5)' `
        -AnchorKey 'hallo_9031' `
        -NewLine $luaTextFile.NewLine
    $zephyrResultText = @(
        [pscustomobject]@{
            Key = 'NF_L0_ZBXQ2103'
            Value = "You have implemented the |cff39D8B8Daedalus Spirit of Attunement|cffffffff onto your Zephyr Holy Stone, increasing the host mount gear's |cff39D8B8native quality stat.|cffffffff"
            Anchor = 'NF_L0_ZBXQ2003'
        },
        [pscustomobject]@{
            Key = 'NF_L0_ZBXQ2203'
            Value = "You have implemented the |cff39D8B8Hephaestus Spirit of Tempering|cffffffff onto your Zephyr Holy Stone, increasing the host mount gear's |cff39D8B8ordinary grade attributes.|cffffffff"
            Anchor = 'NF_L0_ZBXQ2103'
        },
        [pscustomobject]@{
            Key = 'NF_L0_ZBXQ2303'
            Value = 'You have implemented the |cff39D8B8Mnemosyne Spirit of Preservation|cffffffff onto your Zephyr Holy Stone, reducing |cff39D8B8eligible hostile mana burn.|cffffffff'
            Anchor = 'NF_L0_ZBXQ2203'
        },
        [pscustomobject]@{
            Key = 'NF_L0_ZBXQ2403'
            Value = 'You have implemented the |cff39D8B8Themis Spirit of Continuity|cffffffff onto your Zephyr Holy Stone, reducing |cff39D8B8eligible hostile cooldown extension.|cffffffff'
            Anchor = 'NF_L0_ZBXQ2303'
        },
        [pscustomobject]@{
            Key = 'NF_L0_ZBXQ903204'
            Value = '|cffF14187A Zephyr Holy Stone can only be mounted on mount gear.|cffffffff'
            Anchor = 'NF_L0_ZBXQ903105'
        },
        [pscustomobject]@{
            Key = 'NF_L0_ZBXQ903205'
            Value = '|cffF14187Only a Zephyr Spirit can be implemented onto a Zephyr Holy Stone.|cffffffff'
            Anchor = 'NF_L0_ZBXQ903204'
        }
    )
    foreach ($entry in $zephyrResultText) {
        $luaText = Add-OrValidateLuaAssignment `
            -Text $luaText `
            -Key $entry.Key `
            -Value $entry.Value `
            -AnchorKey $entry.Anchor `
            -NewLine $luaTextFile.NewLine
    }

    $npcFunctionFile = $null
    $npcFunctionText = $null
    $npcFunctionChanged = $false
    if (Test-Path -LiteralPath $npcFunctionPath -PathType Leaf) {
        $npcFunctionFile = Read-PreservedText -Path $npcFunctionPath
        $npcFunctionText = Install-ZephyrResultDecoder `
            -Text $npcFunctionFile.Text `
            -NewLine $npcFunctionFile.NewLine
        $npcFunctionChanged =
            $npcFunctionText -cne $npcFunctionFile.Text
    }

    $changes = @(
        $itemText -cne $itemFile.Text
        $nameText -cne $nameFile.Text
        $descriptionText -cne $descriptionFile.Text
        $luaText -cne $luaTextFile.Text
        $npcFunctionChanged
    )
    if ($Check) {
        if ($changes -contains $true) {
            throw (
                "Zephyr client content is not installed in $LocaleRoot " +
                "(items=$($changes[0]), names=$($changes[1]), " +
                "descriptions=$($changes[2]), lua=$($changes[3]), " +
                "decoder=$($changes[4])).")
        }
        Write-Host "Verified Zephyr client content: $LocaleRoot"
        return
    }

    if ($changes[0]) {
        Write-PreservedText $itemPath $itemText $itemFile.Encoding `
            $itemFile.HasPreamble
    }
    if ($changes[1]) {
        Write-PreservedText $namePath $nameText $nameFile.Encoding `
            $nameFile.HasPreamble
    }
    if ($changes[2]) {
        Write-PreservedText $descriptionPath $descriptionText `
            $descriptionFile.Encoding $descriptionFile.HasPreamble
    }
    if ($changes[3]) {
        Write-PreservedText $luaTextPath $luaText $luaTextFile.Encoding `
            $luaTextFile.HasPreamble
    }
    if ($changes[4]) {
        Write-PreservedText $npcFunctionPath $npcFunctionText `
            $npcFunctionFile.Encoding $npcFunctionFile.HasPreamble
    }
    Write-Host "Installed Zephyr client content: $LocaleRoot"
}

$resolvedRoot = [IO.Path]::GetFullPath($ClientRoot)
$localizationRoot = Join-Path $resolvedRoot 'Localization'
$locales = @('en_us', 'zh_cn') | Where-Object {
    Test-Path -LiteralPath (Join-Path $localizationRoot $_) -PathType Container
}
if ($locales -notcontains 'en_us') {
    throw "The en_us client localization is missing below $resolvedRoot."
}
foreach ($locale in $locales) {
    Update-Locale -LocaleRoot (Join-Path $localizationRoot $locale)
}

& (Join-Path $PSScriptRoot 'PatchClientZephyrSocketDisplay.ps1') `
    -ClientRoot $resolvedRoot -Check:$Check
