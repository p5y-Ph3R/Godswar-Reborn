function Get-XmlPatchState([string]$Text, [string]$Label) {
    $marker = '<!-- Gear Enhancement forge tab (GWGE1) -->'
    $hasMarker = $Text.IndexOf($marker, [StringComparison]::Ordinal) -ge 0
    $hasOriginalRoot = $Text.IndexOf(
        '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="300,188,650,770" BtnRect="283,13,321,50"',
        [StringComparison]::Ordinal) -ge 0
    $hasWideRoot = $Text.IndexOf(
        '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="222,188,650,770" BtnRect="361,13,399,50"',
        [StringComparison]::Ordinal) -ge 0
    $hasOriginalLayout = $Text.IndexOf(
        '<Bag0 Type="Tab" Rectangle="11,-28,71,-5"',
        [StringComparison]::Ordinal) -ge 0
    $hasNarrowLayout = $Text.IndexOf(
        '<Bag0 Type="Tab" Rectangle="11,-28,53,-5"',
        [StringComparison]::Ordinal) -ge 0
    $hasNarrowBag4 = $Text.IndexOf(
        '<Bag4 Type="Tab" Rectangle="187,-28,295,-5"',
        [StringComparison]::Ordinal) -ge 0
    $hasWideBag4 = $Text.IndexOf(
        '<Bag4 Type="Tab" Rectangle="262,-28,370,-5"',
        [StringComparison]::Ordinal) -ge 0

    if (-not $hasMarker -and $hasOriginalRoot -and -not $hasWideRoot -and
        $hasOriginalLayout -and -not $hasNarrowLayout -and
        -not $hasNarrowBag4 -and -not $hasWideBag4) {
        return 'Original'
    }
    if ($hasMarker -and $hasOriginalRoot -and -not $hasWideRoot -and
        $hasNarrowLayout -and -not $hasOriginalLayout -and
        $hasNarrowBag4 -and -not $hasWideBag4) {
        return 'PatchedNarrow'
    }
    if ($hasMarker -and $hasWideRoot -and -not $hasOriginalRoot -and
        $hasOriginalLayout -and -not $hasNarrowLayout -and
        $hasWideBag4 -and -not $hasNarrowBag4) {
        return 'Patched'
    }
    throw "$Label is neither an exact original, legacy narrow, nor wide GWGE1 XML state."
}

function Set-XmlPatch([string]$Text, [bool]$Apply, [string]$Label) {
    $newLine = "`r`n"
    $state = Get-XmlPatchState $Text $Label
    $originalRoot = '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="300,188,650,770" BtnRect="283,13,321,50"'
    $wideRoot = '<EquipForge Template="T_NormalWindow" ID="370000" Modal="0" Rectangle="222,188,650,770" BtnRect="361,13,399,50"'
    $originalTabs = @(
        '<Bag0 Type="Tab" Rectangle="11,-28,71,-5"',
        '<Bag1 Type="Tab" Rectangle="74,-28,134,-5"',
        '<Bag2 Type="Tab" Rectangle="136,-28,197,-5"',
        '<Bag3 Type="Tab" Rectangle="198,-28,259,-5"'
    )
    $narrowTabs = @(
        '<Bag0 Type="Tab" Rectangle="11,-28,53,-5"',
        '<Bag1 Type="Tab" Rectangle="55,-28,97,-5"',
        '<Bag2 Type="Tab" Rectangle="99,-28,141,-5"',
        '<Bag3 Type="Tab" Rectangle="143,-28,185,-5"'
    )
    $originalPoints = @'
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="106,67,122,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="169,67,185,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="232,67,248,73" Text=""/>
'@ -replace "`n", $newLine
    $narrowPoints = @'
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="88,67,104,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="132,67,148,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="176,67,192,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="220,67,236,73" Text=""/>
'@ -replace "`n", $newLine
    $widePoints = @'
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="106,67,122,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="169,67,185,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="232,67,248,73" Text=""/>
   <Tp1 Type="Text" ID="-1" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="161,179" Rectangle="295,67,311,73" Text=""/>
'@ -replace "`n", $newLine
    $narrowBag4 = @'
     <!-- Gear Enhancement forge tab (GWGE1) -->
     <Bag4 Type="Tab" Rectangle="187,-28,295,-5" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="244,450" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_60">
       <EnhanceTitle Type="Text" Rectangle="18,42,244,82" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_61"/>
       <EnhanceInfo Type="Text" Rectangle="18,96,244,154" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_62"/>
     </Bag4>
     <!-- /Gear Enhancement forge tab (GWGE1) -->
'@ -replace "`n", $newLine
    $wideBag4 = @'
     <!-- Gear Enhancement forge tab (GWGE1) -->
     <Bag4 Type="Tab" Rectangle="262,-28,370,-5" Texture="./Localization/en_us/UI/Texture/main.gwo" TexturePos="244,450" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_60">
       <EnhanceTitle Type="Text" Rectangle="18,42,322,82" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_61"/>
       <EnhanceInfo Type="Text" Rectangle="18,96,322,154" TexturePos="1024,1024" Font="MainMap2" FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" SText="EF_X0_62"/>
     </Bag4>
     <!-- /Gear Enhancement forge tab (GWGE1) -->
'@ -replace "`n", $newLine

    if ($Apply) {
        if ($state -eq 'Patched') {
            return $Text
        }

        $Text = Replace-ExactOnce $Text $originalRoot $wideRoot $Label
        if ($state -eq 'PatchedNarrow') {
            for ($index = 0; $index -lt $originalTabs.Count; $index++) {
                $Text = Replace-ExactOnce $Text $narrowTabs[$index] $originalTabs[$index] $Label
            }
            $Text = Replace-ExactOnce $Text $narrowPoints $widePoints $Label
            $Text = Replace-ExactOnce $Text $narrowBag4 $wideBag4 $Label
            return $Text
        }

        $Text = Replace-ExactOnce $Text $originalPoints $widePoints $Label
        $Text = Replace-ExactOnce $Text ("     </Bag3>${newLine}   </Bags>") (
            "     </Bag3>${newLine}${wideBag4}   </Bags>") $Label
        return $Text
    }

    if ($state -eq 'Original') {
        return $Text
    }
    if ($state -eq 'PatchedNarrow') {
        for ($index = 0; $index -lt $originalTabs.Count; $index++) {
            $Text = Replace-ExactOnce $Text $narrowTabs[$index] $originalTabs[$index] $Label
        }
        $Text = Replace-ExactOnce $Text $narrowPoints $originalPoints $Label
        $Text = Replace-ExactOnce $Text $narrowBag4 '' $Label
        return $Text
    }

    $Text = Replace-ExactOnce $Text $wideRoot $originalRoot $Label
    $Text = Replace-ExactOnce $Text $widePoints $originalPoints $Label
    $Text = Replace-ExactOnce $Text $wideBag4 '' $Label
    return $Text
}

function Get-TextPatchBlock([string]$Locale, [bool]$LegacyLabel) {
    $newLine = "`r`n"
    $values = if ($Locale -eq 'zh_cn') {
        $titleBase64 = if ($LegacyLabel) {
            '6KOF5aSH5bGe5oCn5by65YyW'
        }
        else {
            '6KOF5aSH'
        }
        @(
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
                $titleBase64)),
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
                '5by65YyW44CB5re75Yqg5oiW56e76Zmk6KOF5aSH6ZmE5Yqg5bGe5oCn44CC')),
            [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
                '5q2j5Zyo6L+e5o6l6KOF5aSH5bGe5oCn5by65YyW55WM6Z2i4oCm4oCm'))
        )
    }
    else {
        $title = if ($LegacyLabel) { 'Gear Enhancement' } else { 'Gear' }
        @($title,
            'Enhance, add, or remove gear attributes.',
            'Connecting to Gear Enhancement...')
    }
    return @(
        '-- Gear Enhancement forge tab (GWGE1)',
        ('EF_X0_60 = "{0}"' -f $values[0]),
        ('EF_X0_61 = "{0}"' -f $values[1]),
        ('EF_X0_62 = "{0}"' -f $values[2]),
        '-- /Gear Enhancement forge tab (GWGE1)',
        ''
    ) -join $newLine
}

function Test-OccursExactlyOnce([string]$Text, [string]$Value) {
    $first = $Text.IndexOf($Value, [StringComparison]::Ordinal)
    return $first -ge 0 -and $Text.IndexOf(
        $Value,
        $first + $Value.Length,
        [StringComparison]::Ordinal) -lt 0
}

function Get-TextPatchState(
    [string]$Text,
    [string]$Locale,
    [string]$Label
) {
    $tokens = @(
        '-- Gear Enhancement forge tab (GWGE1)',
        'EF_X0_60',
        'EF_X0_61',
        'EF_X0_62',
        '-- /Gear Enhancement forge tab (GWGE1)'
    )
    $hasAnyToken = @($tokens | Where-Object {
        $Text.IndexOf($_, [StringComparison]::Ordinal) -ge 0
    }).Count -gt 0
    if (-not $hasAnyToken) { return 'Original' }

    $hasExactEnvelope = @($tokens | Where-Object {
        -not (Test-OccursExactlyOnce $Text $_)
    }).Count -eq 0
    if ($hasExactEnvelope -and (Test-OccursExactlyOnce $Text (
            Get-TextPatchBlock $Locale $false))) {
        return 'Patched'
    }
    if ($hasExactEnvelope -and (Test-OccursExactlyOnce $Text (
            Get-TextPatchBlock $Locale $true))) {
        return 'PatchedLegacyLabel'
    }
    throw "$Label has a partial or conflicting GWGE1 text patch."
}

function Set-TextPatch(
    [string]$Text,
    [bool]$Apply,
    [string]$Locale,
    [string]$Label
) {
    $state = Get-TextPatchState $Text $Locale $Label
    $desiredBlock = Get-TextPatchBlock $Locale $false
    $legacyBlock = Get-TextPatchBlock $Locale $true

    if ($Apply) {
        if ($state -eq 'Patched') { return $Text }
        if ($state -eq 'PatchedLegacyLabel') {
            return Replace-ExactOnce $Text $legacyBlock $desiredBlock $Label
        }
        return Replace-ExactOnce $Text '--Event.xml' (
            $desiredBlock + '--Event.xml') $Label
    }
    if ($state -eq 'Original') { return $Text }
    $block = if ($state -eq 'Patched') { $desiredBlock } else { $legacyBlock }
    return Replace-ExactOnce $Text $block '' $Label
}
