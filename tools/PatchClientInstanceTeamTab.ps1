[CmdletBinding()]
param(
    [string]$ClientPath = 'C:\Godswar Origin',
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$xmlPath = Join-Path $ClientPath `
    'Localization\en_us\UI\XML\BattleUI.xml'
$luaPath = Join-Path $ClientPath `
    'Localization\en_us\UI\XML\Battle.lua'
foreach ($path in @($xmlPath, $luaPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing client battle UI file: $path"
    }
    if ((Get-Item -LiteralPath $path).Length -ge 20000) {
        throw "Client battle UI file exceeds 20 KB: $path"
    }
}

$xml = [IO.File]::ReadAllText($xmlPath)
$stockPattern = '<ViewGoup\s+Template="T_Button4"' +
    '(?![^>]*OnClick=)(?<body>[^>]*?)/>'
$patchedPattern = '<ViewGoup\s+Template="T_Button4"' +
    '(?<body>[^>]*?)OnClick="OpenInstanceTeamTab\(\)"\s*/>'
$stockMatches = [regex]::Matches($xml, $stockPattern)
$patchedMatches = [regex]::Matches($xml, $patchedPattern)
if ($stockMatches.Count -eq 2 -and $patchedMatches.Count -eq 0) {
    $nextXml = [regex]::Replace(
        $xml,
        $stockPattern,
        '<ViewGoup Template="T_Button4"${body} ' +
            'OnClick="OpenInstanceTeamTab()"/>')
}
elseif ($stockMatches.Count -eq 0 -and $patchedMatches.Count -eq 2) {
    $nextXml = $xml
}
else {
    throw 'BattleUI.xml has a partial or unsupported Check team layout.'
}

$lua = [IO.File]::ReadAllText($luaPath)
$function = @'

function OpenInstanceTeamTab()
	local relation = UIAPI:GetElement("RelationUI")
	if relation == nil then
		return
	end
	local tabs = relation:GetChild("RelationTabBar")
	relation:Visible(true)
	relation:Top()
	UIAPI:ActiveTab(4, tabs)
end
'@
$hasFunction = $lua.IndexOf(
    'function OpenInstanceTeamTab()',
    [StringComparison]::Ordinal) -ge 0
$nextLua = if ($hasFunction) { $lua } else { $lua.TrimEnd() + $function }

$changed = $nextXml -ne $xml -or $nextLua -ne $lua
if ($ValidateOnly -and $changed) {
    throw 'Client Check team routing requires an update.'
}
if ($changed) {
    [IO.File]::WriteAllText($xmlPath, $nextXml)
    [IO.File]::WriteAllText($luaPath, $nextLua)
}

foreach ($path in @($xmlPath, $luaPath)) {
    if ((Get-Item -LiteralPath $path).Length -ge 20000) {
        throw "Patched client battle UI file exceeds 20 KB: $path"
    }
}
Write-Output "Instance Check team tab ready: $ClientPath"
