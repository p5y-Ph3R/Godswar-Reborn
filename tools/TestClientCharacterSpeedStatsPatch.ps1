[CmdletBinding()]
param(
    [string]$FixtureRoot = 'C:\Godswar Origin'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$speedPatcher = Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.ps1'
$questPatcher = Join-Path $PSScriptRoot 'PatchClientQuestViewFrameGuard.ps1'
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Core.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'character-speed-stats-test-' + [guid]::NewGuid().ToString('N'))
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Assert-Throws(
    [scriptblock]$Operation,
    [string]$Fragment,
    [string]$Label
) {
    try { & $Operation }
    catch {
        Assert-True ($_.Exception.Message -like "*$Fragment*") (
            "$Label error message")
        return
    }
    throw "Expected failure: $Label"
}

function Copy-Bytes([byte[]]$Source, [byte[]]$Destination, [int]$Offset) {
    [Array]::Copy($Source, 0, $Destination, $Offset, $Source.Length)
}

function Convert-HexBytes([string]$Hex) {
    $normalized = $Hex -replace '\s', ''
    [byte[]]$result = for ($index = 0; $index -lt $normalized.Length;
        $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $result
}

function Test-OffsetAllowed([int]$Offset, [object[]]$Ranges) {
    foreach ($range in $Ranges) {
        if ($Offset -ge $range.Offset -and
            $Offset -lt $range.Offset + $range.Length) { return $true }
    }
    return $false
}

function Get-BackupFileCount([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0 }
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File).Count
}

function New-ClientFixture([string]$Name) {
    $root = Join-Path $testRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination (Join-Path $root 'Origin.exe')
    foreach ($locale in 'en_us', 'zh_cn') {
        $relative = "Localization\$locale\UI\XML"
        $targetDirectory = Join-Path $root $relative
        [IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
        Copy-Item -LiteralPath (
            Join-Path $FixtureRoot "$relative\PersonalInfoUI.xml") `
            -Destination (Join-Path $targetDirectory 'PersonalInfoUI.xml')
        $sourceLua = Join-Path $FixtureRoot (
            "$relative\PersonalInfoSpeedStats.lua")
        if (Test-Path -LiteralPath $sourceLua -PathType Leaf) {
            Copy-Item -LiteralPath $sourceLua -Destination (
                Join-Path $targetDirectory 'PersonalInfoSpeedStats.lua')
        }
    }
    return $root
}

function Normalize-Original([string]$Root, [string]$BackupRoot) {
    $speed = & $speedPatcher -ClientRoot $Root -Mode Status
    if ($speed.State -in 'PatchedV1', 'PatchedV2', 'PatchedV3') {
        & $speedPatcher -ClientRoot $Root -Mode Revert `
            -BackupRoot $BackupRoot | Out-Null
    }
    $quest = & $questPatcher -ClientExe (Join-Path $Root 'Origin.exe') `
        -Mode Status
    if ($quest.State -eq 'Patched') {
        & $questPatcher -ClientExe (Join-Path $Root 'Origin.exe') `
            -Mode Revert -BackupRoot $BackupRoot | Out-Null
    }
    Assert-Equal (& $speedPatcher -ClientRoot $Root -Mode Status).State `
        'Original' "$Root normalized speed state"
    Assert-Equal (& $questPatcher -ClientExe (
            Join-Path $Root 'Origin.exe') -Mode Status).State `
        'Original' "$Root normalized QuestView state"
    foreach ($locale in 'en_us', 'zh_cn') {
        $xmlPath = Join-Path $Root (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $xml = [IO.File]::ReadAllText(
            $xmlPath, [Text.UTF8Encoding]::new($false, $true))
        Assert-Equal (Convert-PersonalInfoXml $xml $locale $false) $xml (
            "$locale direct original conversion idempotence")
    }
}

function Assert-BothPatched([string]$Root) {
    $speed = & $speedPatcher -ClientRoot $Root -Mode Status
    $quest = & $questPatcher -ClientExe (Join-Path $Root 'Origin.exe') `
        -Mode Status
    Assert-Equal $speed.State 'PatchedV3' 'Speed patch state'
    Assert-Equal $quest.State 'Patched' 'QuestView patch state'
    Assert-Equal $speed.CaveReserveBytes 128 'Speed cave ownership'
    Assert-Equal $quest.CaveReserveBytes 32 'Quest cave ownership'
    Assert-Equal $speed.MovementWireOffset 56 'Movement wire offset'
    Assert-Equal $speed.RidingWireOffset 60 'Riding wire offset'
    foreach ($locale in 'en_us', 'zh_cn') {
        $xmlPath = Join-Path $Root (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $xml = [IO.File]::ReadAllText(
            $xmlPath, [Text.UTF8Encoding]::new($false, $true))
        Assert-True (-not $xml.Contains('<SpeedBack ')) (
            "$locale has no separate speed background")
        Assert-True $xml.Contains('<MovementSpeedPercent ') (
            "$locale movement value suffix")
        Assert-True $xml.Contains('<RidingSpeed ') "$locale riding label"
        Assert-True $xml.Contains('Rectangle="100,100,363,652"') (
            "$locale native-grid character-window bounds")
        Assert-True $xml.Contains(
            '<BaseBack Template="T_BgWindow" ID="-1" Rectangle="19,330,127,536" />') (
            "$locale extended left-stat background")
        Assert-True $xml.Contains(
            '<FightBack Template="T_BgWindow" ID="-1" Rectangle="129,330,243,536" />') (
            "$locale extended right-stat background")
        Assert-True $xml.Contains('Rectangle="24,517,78,533"') (
            "$locale movement label geometry")
        Assert-True $xml.Contains('Rectangle="85,517,111,533"') (
            "$locale movement value geometry")
        Assert-True $xml.Contains('Rectangle="113,517,125,533"') (
            "$locale movement suffix geometry")
        Assert-True $xml.Contains('Rectangle="137,517,200,533"') (
            "$locale riding label geometry")
        Assert-True $xml.Contains('Rectangle="210,517,234,533"') (
            "$locale riding value geometry")
        Assert-True $xml.Contains('Rectangle="236,517,246,533"') (
            "$locale riding suffix geometry")
        Assert-True $xml.Contains(
            'OnHovered="RebornPersonalInfoMovementSpeedHovered()"') (
            "$locale movement hover callback")
        Assert-True $xml.Contains(
            'OnHovered="RebornPersonalInfoRidingSpeedHovered()"') (
            "$locale riding hover callback")
        $movement = Get-SpeedCompactLabel $locale $true
        $riding = Get-SpeedCompactLabel $locale $false
        Assert-True $xml.Contains("Text=`"$movement`" Visible=`"1`"") (
            "$locale compact movement label")
        Assert-True $xml.Contains("Text=`"$riding`" CanHovered=`"1`"") (
            "$locale compact riding label")
        Assert-True $xml.Contains(
            "./Localization/$locale/UI/XML/PersonalInfoSpeedStats.lua") (
            "$locale hover script include")
        $luaPath = Join-Path $Root (
            "Localization\$locale\UI\XML\PersonalInfoSpeedStats.lua")
        Assert-True (Test-Path -LiteralPath $luaPath -PathType Leaf) (
            "$locale owned hover script exists")
        $lua = [IO.File]::ReadAllText(
            $luaPath, [Text.UTF8Encoding]::new($false, $true))
        Assert-Equal $lua (Get-PersonalInfoSpeedLua $locale) (
            "$locale exact hover script")
        Assert-True $lua.Contains('local uiapi=UIAPI') (
            "$locale helper API binding")
    }
}

function Set-SpeedV1Fixture([string]$Root) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($locale in 'en_us', 'zh_cn') {
        $directory = Join-Path $Root "Localization\$locale\UI\XML"
        $xmlPath = Join-Path $directory 'PersonalInfoUI.xml'
        $xml = [IO.File]::ReadAllText($xmlPath, $encoding)
        $newLine = if ($xml.Contains("`r`n")) { "`r`n" } else { "`n" }
        if ((Get-PersonalInfoXmlState $xml) -eq 'PatchedV3') {
            $xml = Convert-SpeedV3ToV2 $xml (
                Get-SpeedCompactLabel $locale $true) (
                Get-SpeedCompactLabel $locale $false) $newLine
        }
        $xml = Convert-SpeedV2ToV1 $xml (
            Get-SpeedFullLabel $locale $true) (
            Get-SpeedFullLabel $locale $false) $newLine
        [IO.File]::WriteAllText($xmlPath, $xml, $encoding)
        $luaPath = Join-Path $directory 'PersonalInfoSpeedStats.lua'
        if (Test-Path -LiteralPath $luaPath -PathType Leaf) {
            Remove-Item -LiteralPath $luaPath -Force
        }
    }
    Assert-Equal (& $speedPatcher -ClientRoot $Root -Mode Status).State (
        'PatchedV1') 'Synthetic predecessor state'
}

function Set-SpeedV2Fixture([string]$Root) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($locale in 'en_us', 'zh_cn') {
        $xmlPath = Join-Path $Root (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $xml = [IO.File]::ReadAllText($xmlPath, $encoding)
        $newLine = if ($xml.Contains("`r`n")) { "`r`n" } else { "`n" }
        $xml = Convert-SpeedV3ToV2 $xml (
            Get-SpeedCompactLabel $locale $true) (
            Get-SpeedCompactLabel $locale $false) $newLine
        [IO.File]::WriteAllText($xmlPath, $xml, $encoding)
    }
    Assert-Equal (& $speedPatcher -ClientRoot $Root -Mode Status).State (
        'PatchedV2') 'Synthetic V2 predecessor state'
}

if (-not (Test-Path -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -PathType Leaf)) {
    throw "Origin.exe fixture is missing under $FixtureRoot."
}
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$fixtureExe = Join-Path $FixtureRoot 'Origin.exe'
$fixtureHash = (Get-FileHash $fixtureExe -Algorithm SHA256).Hash

try {
    $questFirst = New-ClientFixture 'quest-first'
    $questFirstBackups = Join-Path $testRoot 'backups-quest-first'
    Normalize-Original $questFirst $questFirstBackups
    [byte[]]$baseline = [IO.File]::ReadAllBytes(
        (Join-Path $questFirst 'Origin.exe'))

    & $questPatcher -ClientExe (Join-Path $questFirst 'Origin.exe') `
        -Mode Apply -BackupRoot $questFirstBackups | Out-Null
    & $speedPatcher -ClientRoot $questFirst -Mode Apply `
        -BackupRoot $questFirstBackups | Out-Null
    Assert-BothPatched $questFirst
    $backupCount = Get-BackupFileCount $questFirstBackups
    & $questPatcher -ClientExe (Join-Path $questFirst 'Origin.exe') `
        -Mode Apply -BackupRoot $questFirstBackups | Out-Null
    & $speedPatcher -ClientRoot $questFirst -Mode Apply `
        -BackupRoot $questFirstBackups | Out-Null
    Assert-Equal (Get-BackupFileCount $questFirstBackups) $backupCount (
        'Both patches are idempotent')

    [byte[]]$both = [IO.File]::ReadAllBytes(
        (Join-Path $questFirst 'Origin.exe'))
    $allowed = @(
        [pscustomobject]@{ Offset = 0x1DA4C0; Length = 5 },
        [pscustomobject]@{ Offset = 0x5C3F00; Length = 0x20 },
        [pscustomobject]@{ Offset = 0x1B5B97; Length = 5 },
        [pscustomobject]@{ Offset = 0x5C3F20; Length = 0x80 }
    )
    $changed = 0
    for ($offset = 0; $offset -lt $both.Length; $offset++) {
        if ($baseline[$offset] -eq $both[$offset]) { continue }
        $changed++
        Assert-True (Test-OffsetAllowed $offset $allowed) (
            "allowlisted binary offset 0x$('{0:X}' -f $offset)")
    }
    Assert-True ($changed -gt 120) 'Expected audited binary mutations exist'

    & $speedPatcher -ClientRoot $questFirst -Mode Revert `
        -BackupRoot $questFirstBackups | Out-Null
    Assert-Equal (& $questPatcher -ClientExe (
            Join-Path $questFirst 'Origin.exe') -Mode Status).State `
        'Patched' 'Speed revert preserves QuestView patch'
    Assert-Equal (& $speedPatcher -ClientRoot $questFirst -Mode Status).State `
        'Original' 'Speed revert state'
    foreach ($locale in 'en_us', 'zh_cn') {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $questFirst (
                "Localization\$locale\UI\XML\PersonalInfoSpeedStats.lua")) `
                -PathType Leaf)) "$locale hover script removed on revert"
    }

    $speedFirst = New-ClientFixture 'speed-first'
    $speedFirstBackups = Join-Path $testRoot 'backups-speed-first'
    Normalize-Original $speedFirst $speedFirstBackups
    & $speedPatcher -ClientRoot $speedFirst -Mode Apply `
        -BackupRoot $speedFirstBackups | Out-Null
    & $questPatcher -ClientExe (Join-Path $speedFirst 'Origin.exe') `
        -Mode Apply -BackupRoot $speedFirstBackups | Out-Null
    Assert-BothPatched $speedFirst
    & $questPatcher -ClientExe (Join-Path $speedFirst 'Origin.exe') `
        -Mode Revert -BackupRoot $speedFirstBackups | Out-Null
    Assert-Equal (& $speedPatcher -ClientRoot $speedFirst -Mode Status).State `
        'PatchedV3' 'QuestView revert preserves speed patch'

    $upgrade = New-ClientFixture 'v1-upgrade'
    $upgradeBackups = Join-Path $testRoot 'backups-v1-upgrade'
    Normalize-Original $upgrade $upgradeBackups
    & $questPatcher -ClientExe (Join-Path $upgrade 'Origin.exe') `
        -Mode Apply -BackupRoot $upgradeBackups | Out-Null
    & $speedPatcher -ClientRoot $upgrade -Mode Apply `
        -BackupRoot $upgradeBackups | Out-Null
    $upgradeExe = Join-Path $upgrade 'Origin.exe'
    $finalBinaryHash = (Get-FileHash $upgradeExe -Algorithm SHA256).Hash
    Set-SpeedV1Fixture $upgrade
    & $speedPatcher -ClientRoot $upgrade -Mode Apply `
        -BackupRoot $upgradeBackups | Out-Null
    Assert-BothPatched $upgrade
    Assert-Equal (Get-FileHash $upgradeExe -Algorithm SHA256).Hash (
        $finalBinaryHash) 'V1 to V3 upgrade leaves audited EXE patch unchanged'
    foreach ($locale in 'en_us', 'zh_cn') {
        $xmlPath = Join-Path $upgrade (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $xml = [IO.File]::ReadAllText(
            $xmlPath, [Text.UTF8Encoding]::new($false, $true))
        Assert-Equal (Convert-PersonalInfoXml $xml $locale $true) $xml (
            "$locale direct V3 conversion idempotence")
    }

    $v2Upgrade = New-ClientFixture 'v2-upgrade'
    $v2UpgradeBackups = Join-Path $testRoot 'backups-v2-upgrade'
    Normalize-Original $v2Upgrade $v2UpgradeBackups
    & $questPatcher -ClientExe (Join-Path $v2Upgrade 'Origin.exe') `
        -Mode Apply -BackupRoot $v2UpgradeBackups | Out-Null
    & $speedPatcher -ClientRoot $v2Upgrade -Mode Apply `
        -BackupRoot $v2UpgradeBackups | Out-Null
    Set-SpeedV2Fixture $v2Upgrade
    $v2Exe = Join-Path $v2Upgrade 'Origin.exe'
    $beforeV3Exe = (Get-FileHash $v2Exe -Algorithm SHA256).Hash
    $beforeV3Lua = @{}
    foreach ($locale in 'en_us', 'zh_cn') {
        $beforeV3Lua[$locale] = (Get-FileHash (Join-Path $v2Upgrade (
            "Localization\$locale\UI\XML\PersonalInfoSpeedStats.lua")) `
            -Algorithm SHA256).Hash
    }
    & $speedPatcher -ClientRoot $v2Upgrade -Mode Apply `
        -BackupRoot $v2UpgradeBackups | Out-Null
    Assert-BothPatched $v2Upgrade
    Assert-Equal (Get-FileHash $v2Exe -Algorithm SHA256).Hash (
        $beforeV3Exe) 'V2 to V3 migration leaves EXE unchanged'
    foreach ($locale in 'en_us', 'zh_cn') {
        $luaPath = Join-Path $v2Upgrade (
            "Localization\$locale\UI\XML\PersonalInfoSpeedStats.lua")
        Assert-Equal (Get-FileHash $luaPath -Algorithm SHA256).Hash (
            $beforeV3Lua[$locale]) "$locale V2 to V3 keeps hover Lua"
    }

    $xmlCorrupt = New-ClientFixture 'xml-corrupt'
    $xmlCorruptBackups = Join-Path $testRoot 'backups-xml-corrupt'
    Normalize-Original $xmlCorrupt $xmlCorruptBackups
    & $speedPatcher -ClientRoot $xmlCorrupt -Mode Apply `
        -BackupRoot $xmlCorruptBackups | Out-Null
    $badXmlPath = Join-Path $xmlCorrupt (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $badXml = [IO.File]::ReadAllText(
        $badXmlPath, [Text.UTF8Encoding]::new($false, $true)).Replace(
        'RebornPersonalInfoMovementSpeedHovered()',
        'UnknownMovementSpeedHovered()')
    [IO.File]::WriteAllText(
        $badXmlPath, $badXml, [Text.UTF8Encoding]::new($false, $true))
    Assert-Throws {
        & $speedPatcher -ClientRoot $xmlCorrupt -Mode Status | Out-Null
    } 'unknown or partially applied' 'Unknown XML callback rejection'

    $luaCorrupt = New-ClientFixture 'lua-corrupt'
    $luaCorruptBackups = Join-Path $testRoot 'backups-lua-corrupt'
    Normalize-Original $luaCorrupt $luaCorruptBackups
    & $speedPatcher -ClientRoot $luaCorrupt -Mode Apply `
        -BackupRoot $luaCorruptBackups | Out-Null
    $badLuaPath = Join-Path $luaCorrupt (
        'Localization\zh_cn\UI\XML\PersonalInfoSpeedStats.lua')
    [IO.File]::AppendAllText(
        $badLuaPath, '-- unknown', [Text.UTF8Encoding]::new($false, $true))
    Assert-Throws {
        & $speedPatcher -ClientRoot $luaCorrupt -Mode Status | Out-Null
    } 'unknown content' 'Unknown hover-script rejection'

    $corrupt = New-ClientFixture 'corrupt'
    $corruptBackups = Join-Path $testRoot 'backups-corrupt'
    Normalize-Original $corrupt $corruptBackups
    & $speedPatcher -ClientRoot $corrupt -Mode Apply `
        -BackupRoot $corruptBackups | Out-Null
    $corruptExe = Join-Path $corrupt 'Origin.exe'
    [byte[]]$corruptBytes = [IO.File]::ReadAllBytes($corruptExe)
    $corruptBytes[0x5C3F9F] = 0xCC
    [IO.File]::WriteAllBytes($corruptExe, $corruptBytes)
    Assert-Throws {
        & $speedPatcher -ClientRoot $corrupt -Mode Status | Out-Null
    } 'unknown or partially applied' 'Unknown speed-cave byte rejection'

    Assert-Equal (Get-FileHash $fixtureExe -Algorithm SHA256).Hash `
        $fixtureHash 'Source fixture remains unchanged'
    Write-Host (
        "Character speed-stat patch checks passed: $assertions assertions.")
}
finally {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTest.StartsWith(
            $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTest -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
