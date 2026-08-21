Set-StrictMode -Version Latest

function Get-ProgressiveTalentStockLines {
    return @{
        0 = 'MaxHP=0,60'; 1 = 'PhyAttack=4,8'; 2 = 'PhyDefend=5,6'
        3 = 'Hit=2,4'; 4 = 'MaxMP=1,8'; 5 = 'PhyDamage=11,0.004'
        6 = 'PhyDefend=5,12'; 7 = 'Miss=3,4'; 8 = 'DamageSorb=10,4'
        9 = 'HPResume=13,20'; 10 = 'StatusHit=15,1'
        11 = 'MaxMP=1,12'; 12 = 'PhyAttack=4,14'; 13 = 'MaxHP=0,100'
        14 = 'FrenzyMiss=9,1.6'; 15 = 'DamageSorb=10,7'
        16 = 'MagicDefend=7,10'; 17 = 'StatusMiss=16,1.2'
        50 = 'Hit=2,3'; 51 = 'PhyAttack=4,10'; 52 = 'PhyDefend=5,9'
        53 = 'MaxHP=0,50'; 54 = 'Miss=3,2'
        55 = 'PhyDamage=11,0.005'; 56 = 'Hit=2,5'
        57 = 'HPResume=13,16'; 58 = 'Miss=3,4'
        59 = 'MagicDefend=7,7'; 60 = 'MPResume=14,3'
        61 = 'Becure=18,0.01'; 62 = 'PhyAttack=4,20'
        63 = 'FrenzyHit=8,1.6'; 64 = 'MPResume=14,4'
        65 = 'FrenzyMiss=9,1.2'; 66 = 'DamageSorb=10,7'
        67 = 'MaxHP=0,90'; 68 = 'MaxHP=0,90'
        100 = 'MaxMP=1,12'; 101 = 'MagicAttack=6,8'; 102 = 'Hit=2,3'
        103 = 'DamageSorb=10,4'; 104 = 'MagicDefend=7,4'
        105 = 'MagicAttack=6,14'; 106 = 'Hit=2,4'
        107 = 'PhyDefend=5,8'; 108 = 'Miss=3,4'
        109 = 'HPResume=13,10'; 110 = 'MaxMP=1,20'
        111 = 'MagicDefend=7,8'; 112 = 'MagicDamage=12,0.006'
        113 = 'FrenzyHit=8,1.2'; 114 = 'DamageSorb=10,6'
        115 = 'FrenzyMiss=9,1'; 116 = 'MaxHP=0,80'
        117 = 'MPResume=14,4'; 150 = 'Miss=3,2'
        151 = 'MagicDefend=7,5'; 152 = 'PhyDefend=5,10'
        153 = 'MagicAttack=6,7'; 154 = 'MaxMP=1,10'
        155 = 'MagicDefend=7,9'; 156 = 'FrenzyMiss=9,1'
        157 = 'Hit=2,5'; 158 = 'MaxMP=1,14'
        159 = 'StatusMiss=16,1.4'; 160 = 'HPResume=13,10'
        161 = 'Cure=17,0.02'; 162 = 'DamageSorb=10,8'
        163 = 'Miss=3,5'; 164 = 'MagicAttack=6,12'
        165 = 'MPResume=14,3'; 166 = 'StatusHit=15,1.2'
        167 = 'MaxHP=0,90'
    }
}

function Get-ProgressiveTalentSkillProfile([string]$Locale) {
    $stock = Get-ProgressiveTalentStockLines
    if ($Locale -eq 'en_us') {
        return [pscustomobject]@{
            Locale = $Locale
            ExpectedIds = @($stock.Keys | ForEach-Object { [int]$_ } |
                Sort-Object)
            StockSha256 =
                'B837AF9450AC7130B64650E2302820336DB03FEF67B927C23818F9D9008C9A34'
            TooltipSha256 =
                '65211A130CC2C9EA7594898E9F90D9CE25637AC8538DA1E3A8EA60C7EC4AC0FD'
        }
    }
    if ($Locale -eq 'zh_cn') {
        return [pscustomobject]@{
            Locale = $Locale
            ExpectedIds = @($stock.Keys | ForEach-Object { [int]$_ } |
                Where-Object { $_ -ne 68 } | Sort-Object)
            StockSha256 =
                '25B8C5A4CB7F679769245241DD89295428C9F2F3B437E8B5F477162E9A8A8C4D'
            TooltipSha256 = $null
        }
    }
    throw "Unsupported Skill.ini locale '$Locale'."
}

function Get-ProgressiveTalentTooltipLine([string]$StockLine) {
    $separator = $StockLine.LastIndexOf(',')
    if ($separator -le 0 -or $separator -eq $StockLine.Length - 1) {
        throw "Invalid reviewed talent line '$StockLine'."
    }
    $value = [decimal]::Parse(
        $StockLine.Substring($separator + 1),
        [Globalization.CultureInfo]::InvariantCulture)
    $tooltip = ($value * [decimal]2.6).ToString(
        'G29', [Globalization.CultureInfo]::InvariantCulture)
    return $StockLine.Substring(0, $separator + 1) + $tooltip
}

function Read-ProgressiveTalentSkillText([byte[]]$Bytes, [string]$Label) {
    if ($Bytes.Length -lt 2 -or ($Bytes.Length -band 1) -ne 0 -or
        $Bytes[0] -ne 0xFF -or $Bytes[1] -ne 0xFE) {
        throw "$Label must be strict UTF-16LE with a BOM."
    }
    $encoding = [Text.UnicodeEncoding]::new($false, $true, $true)
    $text = $encoding.GetString($Bytes, 2, $Bytes.Length - 2)
    $withoutCrLf = $text.Replace("`r`n", '')
    if ($withoutCrLf.Contains("`r") -or $withoutCrLf.Contains("`n")) {
        throw "$Label must preserve the reviewed CRLF line endings."
    }
    return $text
}

function ConvertTo-ProgressiveTalentSkillBytes([string]$Text) {
    $encoding = [Text.UnicodeEncoding]::new($false, $true, $true)
    [byte[]]$body = $encoding.GetBytes($Text)
    [byte[]]$result = [byte[]]::new($body.Length + 2)
    $result[0] = 0xFF
    $result[1] = 0xFE
    [Array]::Copy($body, 0, $result, 2, $body.Length)
    return $result
}

function Get-ProgressiveTalentSectionEffect(
    [string]$Text,
    [int]$TalentId
) {
    $section = [regex]::Match(
        $Text,
        "(?ms)^\[$TalentId\]\r\n(?<body>.*?)(?=^\[|\z)")
    if (-not $section.Success) {
        throw "Skill.ini is missing reviewed talent section [$TalentId]."
    }
    $effects = [regex]::Matches(
        $section.Groups['body'].Value,
        '(?m)^(?!Icon(?:Pos|Size)=)[A-Za-z]+=-?[0-9]+,-?[0-9]+(?:\.[0-9]+)?(?=\r?$)')
    if ($effects.Count -ne 1) {
        throw "Skill.ini talent [$TalentId] must have one effect scalar."
    }
    return [pscustomobject]@{
        Section = $section
        Effect = $effects[0]
        AbsoluteEffectIndex = $section.Groups['body'].Index + $effects[0].Index
        Value = $effects[0].Value
    }
}

function Get-ProgressiveTalentSkillState(
    [byte[]]$Bytes,
    [object]$Profile
) {
    $hash = Get-ProgressiveTalentBytesSha256 $Bytes
    $knownHash = $hash -eq $Profile.StockSha256 -or
        ($null -ne $Profile.TooltipSha256 -and
            $hash -eq $Profile.TooltipSha256)
    if (-not $knownHash) {
        throw "Unsupported $($Profile.Locale) Skill.ini (SHA-256 $hash)."
    }
    $text = Read-ProgressiveTalentSkillText $Bytes (
        "$($Profile.Locale) Skill.ini")
    $numericSections = @([regex]::Matches($text, '(?m)^\[(\d+)\]\r?$') |
        ForEach-Object { [int]$_.Groups[1].Value })
    if ($numericSections.Count -ne $Profile.ExpectedIds.Count -or
        @(Compare-Object $numericSections $Profile.ExpectedIds).Count -ne 0) {
        throw "$($Profile.Locale) Skill.ini talent section set is not exact."
    }
    $stockLines = Get-ProgressiveTalentStockLines
    $championStates = @()
    foreach ($id in $Profile.ExpectedIds) {
        $actual = (Get-ProgressiveTalentSectionEffect $text $id).Value
        $stock = $stockLines[$id]
        if ($id -ge 50 -and $id -le 68) {
            $tooltip = Get-ProgressiveTalentTooltipLine $stock
            if ($actual -eq $stock) {
                $championStates += 'Stock'
            } elseif ($actual -eq $tooltip) {
                $championStates += 'Tooltip'
            } else {
                throw "Talent [$id] has unreviewed scalar '$actual'."
            }
        } elseif ($actual -ne $stock) {
            throw "Talent [$id] must keep stock scalar '$stock'."
        }
    }
    $unique = @($championStates | Select-Object -Unique)
    if ($unique.Count -ne 1) {
        throw "$($Profile.Locale) Skill.ini mixes Champion scalar states."
    }
    $state = if ($unique[0] -eq 'Tooltip') {
        'ChampionTooltip'
    } else { 'Stock' }
    $expectedHash = if ($state -eq 'Stock') {
        $Profile.StockSha256
    } else { $Profile.TooltipSha256 }
    if ($hash -ne $expectedHash) {
        throw "$($Profile.Locale) Skill.ini structure/hash state is inconsistent."
    }
    return [pscustomobject]@{
        State = $state
        Sha256 = $hash
        TalentCount = $Profile.ExpectedIds.Count
        Text = $text
    }
}

function Convert-ProgressiveTalentSkillBytes(
    [byte[]]$Bytes,
    [object]$Profile,
    [ValidateSet('Stock', 'ChampionTooltip')]
    [string]$TargetState
) {
    Get-ProgressiveTalentSkillState $Bytes $Profile | Out-Null
    if ($TargetState -eq 'ChampionTooltip' -and
        $null -eq $Profile.TooltipSha256) {
        throw "$($Profile.Locale) has no reviewed tooltip-scaled fixture."
    }
    $text = Read-ProgressiveTalentSkillText $Bytes (
        "$($Profile.Locale) Skill.ini")
    $stockLines = Get-ProgressiveTalentStockLines
    foreach ($id in @($Profile.ExpectedIds | Where-Object {
                $_ -ge 50 -and $_ -le 68 }) | Sort-Object -Descending) {
        $effect = Get-ProgressiveTalentSectionEffect $text $id
        $replacement = if ($TargetState -eq 'Stock') {
            $stockLines[$id]
        } else { Get-ProgressiveTalentTooltipLine $stockLines[$id] }
        $text = $text.Remove(
            $effect.AbsoluteEffectIndex, $effect.Value.Length).Insert(
            $effect.AbsoluteEffectIndex, $replacement)
    }
    [byte[]]$output = ConvertTo-ProgressiveTalentSkillBytes $text
    $state = Get-ProgressiveTalentSkillState $output $Profile
    if ($state.State -ne $TargetState) {
        throw 'Generated Skill.ini did not reach the requested scalar state.'
    }
    return $output
}
