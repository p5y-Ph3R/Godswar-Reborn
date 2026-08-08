$script:QualityPalette = @(
    @{ Name = 'QUALITY_Q01'; Label = 'Common'; R = 220; G = 224; B = 232 },
    @{ Name = 'QUALITY_Q02'; Label = 'Enhanced'; R = 168; G = 208; B = 232 },
    @{ Name = 'QUALITY_Q03'; Label = 'Delicate'; R = 83; G = 214; B = 199 },
    @{ Name = 'QUALITY_Q04'; Label = 'Good'; R = 92; G = 220; B = 112 },
    @{ Name = 'QUALITY_Q05'; Label = 'Superior'; R = 158; G = 222; B = 70 },
    @{ Name = 'QUALITY_Q06'; Label = 'Classic'; R = 229; G = 194; B = 62 },
    @{ Name = 'QUALITY_Q07'; Label = 'Eternal'; R = 255; G = 218; B = 77 },
    @{ Name = 'QUALITY_Q08'; Label = 'Epic'; R = 255; G = 139; B = 223 },
    @{ Name = 'QUALITY_Q09'; Label = 'Legendary'; R = 255; G = 105; B = 55 },
    @{ Name = 'QUALITY_Q10'; Label = 'Mystic'; R = 218; G = 85; B = 238 },
    @{ Name = 'QUALITY_Q11'; Label = 'Divine'; R = 255; G = 231; B = 153 },
    @{ Name = 'QUALITY_Q12'; Label = 'Celestial'; R = 143; G = 196; B = 255 },
    @{ Name = 'QUALITY_Q13'; Label = 'Mythical'; R = 83; G = 189; B = 255 },
    @{ Name = 'QUALITY_Q14'; Label = 'Astral'; R = 202; G = 113; B = 255 },
    @{ Name = 'QUALITY_Q15'; Label = 'Arcane'; R = 255; G = 80; B = 179 },
    @{ Name = 'QUALITY_Q16'; Label = 'Ethereal'; R = 165; G = 245; B = 255 },
    @{ Name = 'QUALITY_Q17'; Label = 'Transcendent'; R = 255; G = 202; B = 58 },
    @{ Name = 'QUALITY_Q18'; Label = 'Ancient'; R = 255; G = 67; B = 91 },
    @{ Name = 'QUALITY_Q19'; Label = 'Primordial'; R = 167; G = 105; B = 255 },
    # Cap quality must read as a rarity color, not as Common/white text.
    @{ Name = 'QUALITY_Q20'; Label = 'Boundless'; R = 255; G = 72; B = 226 }
)

$script:GradePalette = @(
    @{ Name = 'GRADE_G01'; Family = 'Silver'; R = 176; G = 184; B = 200 },
    @{ Name = 'GRADE_G02'; Family = 'Silver'; R = 190; G = 198; B = 214 },
    @{ Name = 'GRADE_G03'; Family = 'Silver'; R = 204; G = 212; B = 226 },
    @{ Name = 'GRADE_G04'; Family = 'Silver'; R = 220; G = 226; B = 236 },
    @{ Name = 'GRADE_G05'; Family = 'Jade'; R = 66; G = 170; B = 118 },
    @{ Name = 'GRADE_G06'; Family = 'Jade'; R = 70; G = 186; B = 127 },
    @{ Name = 'GRADE_G07'; Family = 'Jade'; R = 75; G = 202; B = 137 },
    @{ Name = 'GRADE_G08'; Family = 'Jade'; R = 82; G = 220; B = 148 },
    @{ Name = 'GRADE_G09'; Family = 'Azure'; R = 64; G = 132; B = 220 },
    @{ Name = 'GRADE_G10'; Family = 'Azure'; R = 66; G = 147; B = 234 },
    @{ Name = 'GRADE_G11'; Family = 'Azure'; R = 72; G = 162; B = 246 },
    @{ Name = 'GRADE_G12'; Family = 'Azure'; R = 82; G = 180; B = 255 },
    @{ Name = 'GRADE_G13'; Family = 'Amethyst'; R = 150; G = 86; B = 218 },
    @{ Name = 'GRADE_G14'; Family = 'Amethyst'; R = 165; G = 92; B = 230 },
    @{ Name = 'GRADE_G15'; Family = 'Amethyst'; R = 182; G = 102; B = 242 },
    @{ Name = 'GRADE_G16'; Family = 'Amethyst'; R = 201; G = 115; B = 255 },
    @{ Name = 'GRADE_G17'; Family = 'Crimson'; R = 210; G = 52; B = 78 },
    @{ Name = 'GRADE_G18'; Family = 'Crimson'; R = 225; G = 57; B = 84 },
    @{ Name = 'GRADE_G19'; Family = 'Crimson'; R = 240; G = 64; B = 92 },
    @{ Name = 'GRADE_G20'; Family = 'Crimson'; R = 255; G = 76; B = 102 },
    @{ Name = 'GRADE_G21'; Family = 'Solar'; R = 230; G = 126; B = 28 },
    @{ Name = 'GRADE_G22'; Family = 'Solar'; R = 240; G = 140; B = 30 },
    @{ Name = 'GRADE_G23'; Family = 'Solar'; R = 250; G = 155; B = 34 },
    @{ Name = 'GRADE_G24'; Family = 'Solar'; R = 255; G = 174; B = 45 },
    # The terminal grade uses diamond cyan so it remains distinct from both
    # Common and Boundless when the two labels appear in the same tooltip.
    @{ Name = 'GRADE_G25'; Family = 'Diamond'; R = 56; G = 232; B = 255 }
)

$script:ElementalSentinels = @(
    'ELEMENT_FIRE_COLOR',
    'ELEMENT_WATER_COLOR',
    'ELEMENT_LIGHTNING_COLOR',
    'ELEMENT_EARTH_COLOR',
    'ELEMENT_WIND_COLOR',
    'ELEMENT_LIGHT_COLOR',
    'ELEMENT_DARK_COLOR'
)

$script:PaletteBlockBegin = '-- Reborn gear palette: BEGIN managed block'
$script:PaletteBlockEnd = '-- Reborn gear palette: END managed block'

function Get-GearPaletteLuaBlock([string]$NewLine) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add($script:PaletteBlockBegin)
    $lines.Add('-- Item quality controls the equipment name color only.')
    foreach ($color in $script:QualityPalette) {
        $lines.Add(('{0}={{r={1},g={2},b={3},a=255}}' -f
                $color.Name, $color.R, $color.G, $color.B))
    }
    $lines.Add('')
    $lines.Add('-- Grade families brighten within each four-grade milestone.')
    foreach ($color in $script:GradePalette) {
        $lines.Add(('{0}={{r={1},g={2},b={3},a=255}}' -f
                $color.Name, $color.R, $color.G, $color.B))
    }
    $lines.Add($script:PaletteBlockEnd)
    return [string]::Join($NewLine, $lines)
}
