[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Rollback')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups'),

    [string]$BackupPath,

    [switch]$AllowMutation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$marker = 'Reborn logical warehouse pages v2'
$rollbackMarkers = @(
    'Reborn logical warehouse pages v1',
    $marker
)
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Newline([string]$Text) {
    if ($Text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

function Write-TextAtomically(
    [string]$Path,
    [string]$Text
) {
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $replaceBackup = "$Path.$([guid]::NewGuid().ToString('N')).replaced"
    try {
        [IO.File]::WriteAllText($temporary, $Text, $utf8)
        [IO.File]::Replace($temporary, $Path, $replaceBackup, $true)
    }
    finally {
        foreach ($transient in @($temporary, $replaceBackup)) {
            if (Test-Path -LiteralPath $transient -PathType Leaf) {
                Remove-Item -LiteralPath $transient -Force
            }
        }
    }
}

function Get-PatchedStorageXml([string]$Text) {
    if ($Text.Contains($marker)) { return $Text }

    $normalPattern = '(?s)    <NormalBags\b.*?    </NormalBags>'
    $normalMatches = [regex]::Matches($Text, $normalPattern)
    if ($normalMatches.Count -ne 1) {
        throw 'StorageUI.xml does not contain one stock NormalBags block.'
    }
    $normal = $normalMatches[0].Value
    $tabCount = [regex]::Matches($normal, '<StorBags[0-9]+\b').Count
    $controls = @([regex]::Matches(
        $normal,
        '(?m)^[ \t]*<C(?<slot>[0-9]+)\b[^\r\n]*/>[ \t]*\r?$') |
        Sort-Object {
            [int]$_.Groups['slot'].Value
        })
    if ($tabCount -ne 4 -or $controls.Count -ne 160 -or
        ($controls | Select-Object -ExpandProperty Value -Unique).Count -ne 160) {
        throw 'StorageUI.xml is not the audited four-box predecessor.'
    }
    for ($slot = 0; $slot -lt 160; $slot++) {
        if ([int]$controls[$slot].Groups['slot'].Value -ne $slot) {
            throw "StorageUI.xml is missing stock control C$slot."
        }
    }

    $opening = ($normal -split '\r?\n', 2)[0]
    $functional = [regex]::Match(
        $normal,
        '(?ms)^[ \t]*<Save\b.*?^[ \t]*<MoneyText\b[^\r\n]*/>[ \t]*\r?$')
    if (-not $functional.Success) {
        throw 'StorageUI.xml stock warehouse controls were not found.'
    }

    $newline = Get-Newline $Text
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add($opening)
    $lines.Add("      <!-- $marker -->")
    for ($page = 0; $page -lt 9; $page++) {
        $left = 41 + ($page * 51)
        $right = $left + 45
        $textKey = 8 + $page
        $tabLine = (
            '      <StorBags{0} Type="Tab" Rectangle="{1},4,{2},31" ' +
            'Texture="./Localization/en_us/UI/Texture/main.gwo" ' +
            'TexturePos="42,540" Font="MainMap2" ' +
            'FontColor="DEFAULT_TEXTCOLOR" TextFormat="5" ' +
            'SText="S_X0_{3}" />') -f @(
                $page, $left, $right, $textKey)
        $lines.Add($tabLine)
    }
    $lines.Add('')
    foreach ($control in $controls) {
        $line = $control.Value.Trim()
        if ([int]$control.Groups['slot'].Value -ge 40) {
            $line = [regex]::Replace(
                $line,
                'Rectangle="[^"]*"',
                'Rectangle="-10000,-10000,-9999,-9999"',
                1)
        }
        $lines.Add(('      ' + $line))
    }
    $lines.Add('')
    $lines.Add('      <!-- Normal warehouse actions -->')
    foreach ($line in ($functional.Value -split '\r?\n')) {
        $lines.Add(('      ' + $line.Trim()))
    }
    $lines.Add('    </NormalBags>')
    $replacement = $lines -join $newline
    return $Text.Substring(0, $normalMatches[0].Index) +
        $replacement +
        $Text.Substring($normalMatches[0].Index + $normalMatches[0].Length)
}

function Get-PatchedStorageText([string]$Text) {
    $pattern = '(?m)^S_X0_8 = "Storage Box 1"\r?\n' +
        '^S_X0_9 = "Storage Box 2"\r?\n' +
        '^S_X0_10 = "Storage Box 3"\r?\n' +
        '^S_X0_11 = "Storage Box 4"(?:\r?\n)?'
    if ($Text -match '(?m)^S_X0_16 = "SB-9"$') { return $Text }
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw 'text.lua does not contain the audited Storage Box labels.'
    }
    $newline = Get-Newline $Text
    $labels = 1..9 | ForEach-Object {
        'S_X0_{0} = "SB-{1}"' -f (7 + $_), $_
    }
    return [regex]::Replace(
        $Text,
        $pattern,
        (($labels -join $newline) + $newline),
        1)
}

function Get-PatchedManagerLua([string]$Text) {
    if ($Text.Contains("-- $marker")) { return $Text }
    if (-not $Text.Contains('function NpcFunWarehouse_SetText') -or
        -not $Text.Contains('GameAPI.SetStorageNum(160)')) {
        throw 'NpcFunWarehouse.lua is not the audited stock predecessor.'
    }
    $newline = Get-Newline $Text
    return (@(
        "-- $marker",
        'local win = UIAPI:GetElement("FirstWin");',
        '',
        'function NpcFunWarehouse_SetUI(Type,Index)',
        '    FirstWin_ButtonA1:Visible(true);',
        '    FirstWin_ButtonA2:Visible(true);',
        '    win:Visible(true);',
        'end',
        '',
        'local function WarehousePlural(count)',
        '    if count == 1 then return " Key" end',
        '    return " Keys"',
        'end',
        '',
        'local function WarehouseShow(text, finish)',
        '    FirstWin_Text1:SetText(text);',
        '    FirstWin_Text1:Visible(true);',
        '    if finish then NPCFUN:EndMessage(true); end',
        'end',
        '',
        'function NpcFunWarehouse_SetText(Type,Index,BtnID,SubID)',
        '    if Index == 1 then',
        '        if SubID == 100 then',
        '            local Button = win:GetChild("FirstWin_Button" .. BtnID);',
        '            Button:SetText(warehouse100B);',
        '            Button:Visible(true);',
        '            Button:SetPosition(25,135);',
        '        elseif SubID == 998 then',
        '            WarehouseShow(warehouse998T, true);',
        '        elseif SubID >= 100000 and SubID < 101000 then',
        '            local encoded = SubID - 100000;',
        '            local currentBox = math.floor(encoded / 100);',
        '            local keyCost = encoded - currentBox * 100;',
        '            WarehouseShow("Expand to SB-" .. (currentBox + 1) ..',
        '                " using " .. keyCost .. " Storage Box" ..',
        '                WarehousePlural(keyCost) .. ".", false);',
        '        end',
        '    elseif Index == 2 then',
        '        if SubID >= 201 and SubID <= 208 then',
        '            local targetBox = SubID - 199;',
        '            WarehouseShow("SB-" .. targetBox ..',
        '                " is now unlocked.", true);',
        '        elseif SubID > 900000 and SubID < 901000 then',
        '            local encoded = SubID - 900000;',
        '            local targetBox = math.floor(encoded / 100);',
        '            local keyCost = encoded - targetBox * 100;',
        '            WarehouseShow("SB-" .. targetBox .. " requires " ..',
        '                keyCost .. " Storage Box" ..',
        '                WarehousePlural(keyCost) .. ".", true);',
        '        elseif SubID == 998 then',
        '            WarehouseShow(warehouse998T, true);',
        '        elseif SubID == 999 then',
        '            WarehouseShow(',
        '                "The warehouse could not be enlarged. Please try again.",',
        '                true);',
        '        end',
        '    end',
        'end',
        ''
    ) -join $newline)
}

$root = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
$driveRoot = [IO.Path]::GetPathRoot($root).TrimEnd('\')
if (-not $root -or $root.Equals(
        $driveRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ClientRoot cannot be a filesystem root.'
}

$relativePaths = @(
    'Localization\en_us\UI\XML\StorageUI.xml',
    'Localization\en_us\UI\Base\text.lua',
    'Localization\en_us\UI\XML\NpcFun\NpcFunWarehouse.lua'
)
$paths = @($relativePaths | ForEach-Object { Join-Path $root $_ })
foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required client asset is missing: $path"
    }
}

$texts = @($paths | ForEach-Object {
    [IO.File]::ReadAllText($_, $utf8)
})
$applied = $texts[0].Contains($marker) -and
    $texts[1] -match '(?m)^S_X0_16 = "SB-9"\r?$' -and
    $texts[2].Contains("-- $marker")

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        State = if ($applied) { 'Applied' } else { 'StockOrUnknown' }
        ClientRoot = $root
        StorageXmlSha256 = Get-Hash $paths[0]
        TextSha256 = Get-Hash $paths[1]
        ManagerLuaSha256 = Get-Hash $paths[2]
    }
    return
}
if (-not $AllowMutation) {
    throw "$Mode requires -AllowMutation."
}
$running = Get-Process -Name Origin -ErrorAction SilentlyContinue
if ($running) {
    throw 'Origin.exe must be closed while warehouse assets are changed.'
}

if ($Mode -eq 'Rollback') {
    if (-not $BackupPath) {
        throw 'Rollback requires -BackupPath from Apply.'
    }
    $resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
    $manifestPath = Join-Path $resolvedBackup 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The warehouse asset backup manifest is missing.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    if ($manifest.marker -cnotin $rollbackMarkers) {
        throw 'The backup does not belong to this warehouse patch.'
    }
    for ($index = 0; $index -lt $paths.Count; $index++) {
        $source = Join-Path $resolvedBackup $relativePaths[$index]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-Hash $source) -cne $manifest.files[$index].sha256) {
            throw "The backup file is missing or corrupt: $source"
        }
    }
    for ($index = 0; $index -lt $paths.Count; $index++) {
        [IO.File]::Copy(
            (Join-Path $resolvedBackup $relativePaths[$index]),
            $paths[$index],
            $true)
    }
    [pscustomobject]@{ State = 'RolledBack'; BackupPath = $resolvedBackup }
    return
}

if ($applied) {
    [pscustomobject]@{ State = 'AlreadyApplied'; ClientRoot = $root }
    return
}
$patched = @(
    Get-PatchedStorageXml $texts[0]
    Get-PatchedStorageText $texts[1]
    Get-PatchedManagerLua $texts[2]
)
$backup = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-warehouse-pages-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
New-Item -ItemType Directory -Path $backup | Out-Null
$manifestFiles = @()
for ($index = 0; $index -lt $paths.Count; $index++) {
    $destination = Join-Path $backup $relativePaths[$index]
    $null = New-Item -ItemType Directory -Path (
        Split-Path -Parent $destination) -Force
    [IO.File]::Copy($paths[$index], $destination, $false)
    $manifestFiles += [ordered]@{
        path = $relativePaths[$index]
        sha256 = Get-Hash $destination
    }
}
[IO.File]::WriteAllText(
    (Join-Path $backup 'manifest.json'),
    ([ordered]@{
        marker = $marker
        createdUtc = [DateTime]::UtcNow.ToString('O')
        clientRoot = $root
        files = $manifestFiles
    } | ConvertTo-Json -Depth 5),
    $utf8)

try {
    for ($index = 0; $index -lt $paths.Count; $index++) {
        Write-TextAtomically $paths[$index] $patched[$index]
    }
}
catch {
    for ($index = 0; $index -lt $paths.Count; $index++) {
        [IO.File]::Copy(
            (Join-Path $backup $relativePaths[$index]),
            $paths[$index],
            $true)
    }
    throw
}

[pscustomobject]@{
    State = 'Applied'
    ClientRoot = $root
    BackupPath = $backup
    StorageXmlSha256 = Get-Hash $paths[0]
    TextSha256 = Get-Hash $paths[1]
    ManagerLuaSha256 = Get-Hash $paths[2]
}
