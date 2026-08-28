[CmdletBinding()]
param(
    [string]$ClientPath = 'C:\Godswar Origin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

$sourceXml = Join-Path $ClientPath `
    'Localization\en_us\UI\XML\BattleUI.xml'
$sourceLua = Join-Path $ClientPath `
    'Localization\en_us\UI\XML\Battle.lua'
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path (
            [IO.Path]::GetTempPath()) (
            'reborn-instance-tab-' + [guid]::NewGuid().ToString('N'))))
$systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $temporaryRoot.StartsWith(
        $systemTemp,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary instance-tab fixture escaped the temp directory.'
}

try {
    $fixtureXml = Join-Path $temporaryRoot `
        'Localization\en_us\UI\XML\BattleUI.xml'
    $fixtureLua = Join-Path $temporaryRoot `
        'Localization\en_us\UI\XML\Battle.lua'
    [IO.Directory]::CreateDirectory((Split-Path $fixtureXml)) | Out-Null
    $xml = [IO.File]::ReadAllText($sourceXml)
    $xml = [regex]::Replace(
        $xml,
        '<ViewGoup\s+Template="T_Button4"(?<body>[^>]*?)' +
            'OnClick="OpenInstanceTeamTab\(\)"\s*/>',
        '<ViewGoup Template="T_Button4"${body}/>')
    [IO.File]::WriteAllText($fixtureXml, $xml)
    $lua = [IO.File]::ReadAllText($sourceLua)
    $lua = [regex]::Replace(
        $lua,
        '(?s)\s*function OpenInstanceTeamTab\(\).*?\bend\s*$',
        '')
    [IO.File]::WriteAllText($fixtureLua, $lua)

    $patcher = Join-Path $PSScriptRoot `
        'PatchClientInstanceTeamTab.ps1'
    & $patcher -ClientPath $temporaryRoot | Out-Null
    & $patcher -ClientPath $temporaryRoot -ValidateOnly | Out-Null

    $actualXml = [IO.File]::ReadAllText($fixtureXml)
    $actualLua = [IO.File]::ReadAllText($fixtureLua)
    Assert-True (
        ([regex]::Matches(
            $actualXml,
            '<ViewGoup\s+Template="T_Button4"[^>]*' +
                'OnClick="OpenInstanceTeamTab\(\)"\s*/>')).Count -eq 2) `
        'both native Check team controls use the explicit instance-tab callback'
    Assert-True (-not $actualXml.Contains('<InstanceTeam ')) `
        'the patch preserves the native ViewGoup control name'
    Assert-True ($actualLua.Contains('UIAPI:ActiveTab(4, tabs)')) `
        'the callback selects the fifth Relation/F tab (Instance)'
    Assert-True ($actualLua.Contains('relation:Visible(true)')) `
        'the callback opens the Relation/F window'

    Write-Output 'Client instance Check team tab patch checks passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
