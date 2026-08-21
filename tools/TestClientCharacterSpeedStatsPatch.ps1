[CmdletBinding()]
param(
    [string]$FixtureRoot = 'C:\Godswar Origin'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$speedPatcher = Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.ps1'
$questPatcher = Join-Path $PSScriptRoot 'PatchClientQuestViewFrameGuard.ps1'
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Binary.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Text.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.XmlValidation.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Core.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Layout.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.XmlState.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Lua.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Transaction.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'character-speed-stats-test-' + [guid]::NewGuid().ToString('N'))
$fixtureRoot = [IO.Path]::GetFullPath($FixtureRoot)
$assertions = 0
. (Join-Path $PSScriptRoot 'TestClientCharacterSpeedStatsPatch.Helpers.ps1')
. (Join-Path $PSScriptRoot 'TestClientCharacterSpeedStatsPatch.Advanced.ps1')
. (Join-Path $PSScriptRoot 'TestClientCharacterSpeedStatsPatch.XmlSecurity.ps1')
. (Join-Path $PSScriptRoot 'TestClientCharacterSpeedStatsPatch.Layout.ps1')
. (Join-Path $PSScriptRoot 'TestClientCharacterSpeedStatsPatch.Frame.ps1')
. (Join-Path $PSScriptRoot 'TestClientCharacterSpeedStatsPatch.Compatibility.ps1')

if (-not (Test-Path -LiteralPath (Join-Path $fixtureRoot 'Origin.exe') `
        -PathType Leaf)) {
    throw "Origin.exe fixture is missing under $fixtureRoot."
}
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$fixtureExe = Join-Path $fixtureRoot 'Origin.exe'
$fixtureHash = (Get-FileHash $fixtureExe -Algorithm SHA256).Hash
$fixtureInitialState = (& $speedPatcher -ClientRoot $fixtureRoot `
    -Mode Status).State

try {
    $partial = New-ClientFixture 'legacy-partial'
    $partialBackups = Join-Path $testRoot 'backups-legacy-partial'
    Normalize-Original $partial $partialBackups
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $partialOriginalHashes = @{}
    $partialOriginalBoms = @{}
    foreach ($locale in 'en_us', 'zh_cn') {
        $xmlPath = Join-Path $partial (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $stockXml = [IO.File]::ReadAllText($xmlPath, $encoding)
        $roundTripXml = Convert-PersonalInfoXml (
            Convert-PersonalInfoXml $stockXml $locale $true) $locale $false
        Assert-Equal $roundTripXml $stockXml (
            "$locale XML conversion is text-exact")
        Assert-Equal ([regex]::Matches($roundTripXml, "`r`n").Count) (
            [regex]::Matches($stockXml, "`r`n").Count) (
            "$locale XML conversion preserves CRLF count")
        $constellationPath = Join-Path $partial (
            "Localization\$locale\UI\XML\Constellation.lua")
        $partialOriginalHashes[$xmlPath] = Get-FileSha256 $xmlPath
        $partialOriginalHashes[$constellationPath] = Get-FileSha256 (
            $constellationPath)
        $partialOriginalBoms[$xmlPath] = Test-Utf8Bom $xmlPath
        $partialOriginalBoms[$constellationPath] = Test-Utf8Bom (
            $constellationPath)
        $stockConstellation = [IO.File]::ReadAllText(
            $constellationPath, $encoding)
        Assert-Equal (Convert-ConstellationStatsLua (
                Convert-ConstellationStatsLua $stockConstellation $true) (
                $false)) $stockConstellation (
            "$locale Constellation conversion is text-exact")
    }
    Set-LegacyPartialFixture $partial
    $partialStatus = & $speedPatcher -ClientRoot $partial -Mode Status
    Assert-Equal $partialStatus.State 'LegacyPartial' (
        'Installed-client predecessor is recognized')
    [byte[]]$partialBefore = [IO.File]::ReadAllBytes(
        (Join-Path $partial 'Origin.exe'))
    $questBefore = (& $questPatcher -ClientExe (Join-Path $partial (
                'Origin.exe')) -Mode Status).State
    & $speedPatcher -ClientRoot $partial -Mode Apply `
        -BackupRoot $partialBackups | Out-Null
    Assert-Sid200Patched $partial
    Assert-Equal (& $questPatcher -ClientExe (Join-Path $partial (
                'Origin.exe')) -Mode Status).State $questBefore (
        'Legacy-partial migration preserves QuestView owner')
    [byte[]]$partialAfter = [IO.File]::ReadAllBytes(
        (Join-Path $partial 'Origin.exe'))
    $profile = Get-CharacterStatsBinaryProfile
    $changed = 0
    $allowed = @(
        [pscustomobject]@{ Offset = $profile.HookOffset; Length = 5 },
        [pscustomobject]@{
            Offset = $profile.CaveOffset
            Length = $profile.CaveReserveLength
        }
    )
    for ($offset = 0; $offset -lt $partialAfter.Length; $offset++) {
        if ($partialBefore[$offset] -eq $partialAfter[$offset]) { continue }
        $changed++
        Assert-True (Test-OffsetAllowed $offset $allowed) (
            "legacy migration binary offset 0x$('{0:X}' -f $offset)")
    }
    Assert-True ($changed -gt 100) 'Legacy native hook and cave were removed'
    Assert-True (Test-RebornBytes $partialAfter $profile.HookOffset (
            $profile.OriginalHook)) 'Original PersonalInfo hook restored'
    Assert-True (Test-RebornBytes $partialAfter $profile.CaveOffset (
            $profile.EmptyCave)) 'Only owned 128-byte cave is zeroed'
    $backupCount = Get-BackupFileCount $partialBackups
    & $speedPatcher -ClientRoot $partial -Mode Apply `
        -BackupRoot $partialBackups | Out-Null
    Assert-Equal (Get-BackupFileCount $partialBackups) $backupCount (
        'SID200 Apply is idempotent')
    & $speedPatcher -ClientRoot $partial -Mode Revert `
        -BackupRoot $partialBackups | Out-Null
    Assert-Equal (& $speedPatcher -ClientRoot $partial -Mode Status).State (
        'Original') 'SID200 Revert reaches stock state'
    foreach ($path in $partialOriginalHashes.Keys) {
        Assert-Equal (Get-FileSha256 $path) $partialOriginalHashes[$path] (
            "$(Split-Path $path -Leaf) byte-exact Apply/Revert round trip")
        Assert-Equal (Test-Utf8Bom $path) $partialOriginalBoms[$path] (
            "$(Split-Path $path -Leaf) BOM policy survives round trip")
    }

    $questFirst = New-ClientFixture 'quest-first'
    $questFirstBackups = Join-Path $testRoot 'backups-quest-first'
    Normalize-Original $questFirst $questFirstBackups
    [byte[]]$baseline = [IO.File]::ReadAllBytes(
        (Join-Path $questFirst 'Origin.exe'))
    & $questPatcher -ClientExe (Join-Path $questFirst 'Origin.exe') `
        -Mode Apply -BackupRoot $questFirstBackups | Out-Null
    & $speedPatcher -ClientRoot $questFirst -Mode Apply `
        -BackupRoot $questFirstBackups | Out-Null
    Assert-Sid200Patched $questFirst
    Assert-Equal (& $questPatcher -ClientExe (Join-Path $questFirst (
                'Origin.exe')) -Mode Status).State 'Patched' (
        'QuestView remains patched')
    [byte[]]$both = [IO.File]::ReadAllBytes(
        (Join-Path $questFirst 'Origin.exe'))
    $questRanges = @(
        [pscustomobject]@{ Offset = 0x1DA4C0; Length = 5 },
        [pscustomobject]@{ Offset = 0x5C3F00; Length = 0x20 }
    )
    $questChanges = 0
    for ($offset = 0; $offset -lt $both.Length; $offset++) {
        if ($baseline[$offset] -eq $both[$offset]) { continue }
        $questChanges++
        Assert-True (Test-OffsetAllowed $offset $questRanges) (
            "Quest-only binary offset 0x$('{0:X}' -f $offset)")
    }
    Assert-True ($questChanges -gt 20) 'Quest patch remains the only EXE change'
    & $speedPatcher -ClientRoot $questFirst -Mode Revert `
        -BackupRoot $questFirstBackups | Out-Null
    Assert-Equal (& $questPatcher -ClientExe (Join-Path $questFirst (
                'Origin.exe')) -Mode Status).State 'Patched' (
        'Character-stat Revert preserves QuestView')

    $speedFirst = New-ClientFixture 'speed-first'
    $speedFirstBackups = Join-Path $testRoot 'backups-speed-first'
    Normalize-Original $speedFirst $speedFirstBackups
    & $speedPatcher -ClientRoot $speedFirst -Mode Apply `
        -BackupRoot $speedFirstBackups | Out-Null
    & $questPatcher -ClientExe (Join-Path $speedFirst 'Origin.exe') `
        -Mode Apply -BackupRoot $speedFirstBackups | Out-Null
    Assert-Sid200Patched $speedFirst
    & $questPatcher -ClientExe (Join-Path $speedFirst 'Origin.exe') `
        -Mode Revert -BackupRoot $speedFirstBackups | Out-Null
    Assert-Equal (& $speedPatcher -ClientRoot $speedFirst -Mode Status).State (
        'PatchedSid200') 'QuestView Revert preserves SID200 UI'

    foreach ($legacyState in 'PatchedV1', 'PatchedV2', 'PatchedV3') {
        $legacy = New-ClientFixture $legacyState.ToLowerInvariant()
        $legacyBackups = Join-Path $testRoot (
            'backups-' + $legacyState.ToLowerInvariant())
        Normalize-Original $legacy $legacyBackups
        [byte[]]$legacyBaseline = [IO.File]::ReadAllBytes(
            (Join-Path $legacy 'Origin.exe'))
        Set-LegacyFixture $legacy $legacyState
        & $speedPatcher -ClientRoot $legacy -Mode Apply `
            -BackupRoot $legacyBackups | Out-Null
        Assert-Sid200Patched $legacy
        [byte[]]$legacyFinal = [IO.File]::ReadAllBytes(
            (Join-Path $legacy 'Origin.exe'))
        Assert-Equal ([BitConverter]::ToString($legacyFinal)) (
            [BitConverter]::ToString($legacyBaseline)) (
            "$legacyState migration restores exact stock EXE")
    }

    $preserve = New-ClientFixture 'preserve-constellation'
    $preserveBackups = Join-Path $testRoot 'backups-preserve'
    Normalize-Original $preserve $preserveBackups
    $sentinel = '-- unrelated constellation sentinel'
    $constellationBefore = @{}
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $preserve (
            "Localization\$locale\UI\XML\Constellation.lua")
        $text = [IO.File]::ReadAllText($path, $encoding) +
            "`r`n$sentinel`r`n"
        [IO.File]::WriteAllText($path, $text, $encoding)
        $constellationBefore[$locale] = $text
    }
    & $speedPatcher -ClientRoot $preserve -Mode Apply `
        -BackupRoot $preserveBackups | Out-Null
    Assert-Sid200Patched $preserve
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $preserve (
            "Localization\$locale\UI\XML\Constellation.lua")
        Assert-True ([IO.File]::ReadAllText($path, $encoding).Contains(
                $sentinel)) "$locale unrelated Lua survives Apply"
    }
    & $speedPatcher -ClientRoot $preserve -Mode Revert `
        -BackupRoot $preserveBackups | Out-Null
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $preserve (
            "Localization\$locale\UI\XML\Constellation.lua")
        Assert-Equal ([IO.File]::ReadAllText($path, $encoding)) (
            $constellationBefore[$locale]) (
            "$locale unrelated Lua survives exact round trip")
    }

    $callbackOwner = New-ClientFixture 'callback-owner'
    $callbackBackups = Join-Path $testRoot 'backups-callback-owner'
    Normalize-Original $callbackOwner $callbackBackups
    & $speedPatcher -ClientRoot $callbackOwner -Mode Apply `
        -BackupRoot $callbackBackups | Out-Null
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $callbackOwner (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $text = [IO.File]::ReadAllText($path, $encoding).Replace(
            'OnLoad="RebornPersonalInfoStatsLoad()" OnClose=',
            'OnLoad="RebornPersonalInfoStatsLoad()" CustomOwner="keep" OnClose=')
        [IO.File]::WriteAllText($path, $text, $encoding)
    }
    Assert-Equal (& $speedPatcher -ClientRoot $callbackOwner `
        -Mode Status).State 'PatchedSid200' (
        'Unrelated root attribute is accepted in patched layout')
    & $speedPatcher -ClientRoot $callbackOwner -Mode Revert `
        -BackupRoot $callbackBackups | Out-Null
    foreach ($locale in 'en_us', 'zh_cn') {
        $path = Join-Path $callbackOwner (
            "Localization\$locale\UI\XML\PersonalInfoUI.xml")
        $text = [IO.File]::ReadAllText($path, $encoding)
        Assert-True $text.Contains('CustomOwner="keep"') (
            "$locale unrelated root attribute survives Revert")
        Assert-True (-not $text.Contains('RebornPersonalInfoStatsLoad')) (
            "$locale owned OnLoad removed independently")
        Assert-True (-not $text.Contains('RebornPersonalInfoStatsClose')) (
            "$locale owned OnClose removed independently")
    }
    Assert-Equal (& $speedPatcher -ClientRoot $callbackOwner `
        -Mode Status).State 'Original' (
        'Callback-owner fixture reaches clean Original state')

    $xmlCorrupt = New-ClientFixture 'xml-corrupt'
    $xmlCorruptBackups = Join-Path $testRoot 'backups-xml-corrupt'
    Normalize-Original $xmlCorrupt $xmlCorruptBackups
    & $speedPatcher -ClientRoot $xmlCorrupt -Mode Apply `
        -BackupRoot $xmlCorruptBackups | Out-Null
    $badXmlPath = Join-Path $xmlCorrupt (
        'Localization\en_us\UI\XML\PersonalInfoUI.xml')
    $badXml = [IO.File]::ReadAllText($badXmlPath, $encoding).Replace(
        'RebornPersonalInfoStatsUpdate()', 'UnknownStatsUpdate()')
    [IO.File]::WriteAllText($badXmlPath, $badXml, $encoding)
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
    [IO.File]::AppendAllText($badLuaPath, '-- unknown', $encoding)
    Assert-Throws {
        & $speedPatcher -ClientRoot $luaCorrupt -Mode Status | Out-Null
    } 'unknown content' 'Unknown PersonalInfo Lua rejection'

    $constellationCorrupt = New-ClientFixture 'constellation-corrupt'
    $constellationCorruptBackups = Join-Path $testRoot (
        'backups-constellation-corrupt')
    Normalize-Original $constellationCorrupt $constellationCorruptBackups
    & $speedPatcher -ClientRoot $constellationCorrupt -Mode Apply `
        -BackupRoot $constellationCorruptBackups | Out-Null
    $badConstellationPath = Join-Path $constellationCorrupt (
        'Localization\en_us\UI\XML\Constellation.lua')
    $badConstellation = [IO.File]::ReadAllText(
        $badConstellationPath, $encoding).Replace(
        'REBORN_PERSONAL_INFO_STATS_BRANCH_END',
        'REBORN_PERSONAL_INFO_STATS_BRANCH_BROKEN')
    [IO.File]::WriteAllText(
        $badConstellationPath, $badConstellation, $encoding)
    Assert-Throws {
        & $speedPatcher -ClientRoot $constellationCorrupt -Mode Status |
            Out-Null
    } 'unknown or partially applied SID200' (
        'Unknown Constellation branch rejection')

    $binaryCorrupt = New-ClientFixture 'binary-corrupt'
    $binaryCorruptBackups = Join-Path $testRoot 'backups-binary-corrupt'
    Normalize-Original $binaryCorrupt $binaryCorruptBackups
    $badExe = Join-Path $binaryCorrupt 'Origin.exe'
    [byte[]]$badBytes = [IO.File]::ReadAllBytes($badExe)
    $badBytes[$profile.CaveOffset + $profile.CaveReserveLength - 1] = 0xCC
    [IO.File]::WriteAllBytes($badExe, $badBytes)
    Assert-Throws {
        & $speedPatcher -ClientRoot $binaryCorrupt -Mode Status | Out-Null
    } 'unknown or partially applied' 'Unknown owned-cave byte rejection'

    Invoke-CharacterStatsAdvancedTests
    Invoke-CharacterStatsXmlSecurityTests
    Invoke-CharacterStatsLayoutTests
    Invoke-CharacterStatsFrameTests
    Invoke-CharacterStatsCompatibilityTests

    foreach ($path in @(
        $speedPatcher,
        (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Binary.ps1'),
        (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Core.ps1'),
        (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Layout.ps1'),
        (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Lua.ps1'),
        (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Text.ps1'),
        (Join-Path $PSScriptRoot (
            'PatchClientCharacterSpeedStats.Transaction.ps1')),
        $PSCommandPath,
        (Join-Path $PSScriptRoot (
            'TestClientCharacterSpeedStatsPatch.Helpers.ps1')),
        (Join-Path $PSScriptRoot (
            'TestClientCharacterSpeedStatsPatch.Advanced.ps1')),
        (Join-Path $PSScriptRoot (
            'TestClientCharacterSpeedStatsPatch.XmlSecurity.ps1')),
        (Join-Path $PSScriptRoot (
            'TestClientCharacterSpeedStatsPatch.Layout.ps1')),
        (Join-Path $PSScriptRoot (
            'TestClientCharacterSpeedStatsPatch.Frame.ps1')),
        (Join-Path $PSScriptRoot (
            'TestClientCharacterSpeedStatsPatch.Compatibility.ps1')),
        (Join-Path $PSScriptRoot (
            'PatchClientCharacterSpeedStats.XmlValidation.ps1')),
        (Join-Path $PSScriptRoot (
            'PatchClientCharacterSpeedStats.XmlState.ps1')))) {
        Assert-True ((Get-Item -LiteralPath $path).Length -lt 20000) (
            "$(Split-Path $path -Leaf) is below 20KB")
        Assert-True ((Get-Content -LiteralPath $path).Count -lt 600) (
            "$(Split-Path $path -Leaf) is below 600 lines")
    }
    Assert-Equal (Get-FileHash $fixtureExe -Algorithm SHA256).Hash (
        $fixtureHash) 'Source fixture remains unchanged'
    Assert-True ($fixtureInitialState -in @(
            'Original', 'LegacyPartial', 'PatchedV1', 'PatchedV2',
            'PatchedV3', 'PatchedSid200V1', 'PatchedSid200FrameV1',
            'PatchedSid200')) (
        'Read-only source fixture status is recognized')
    Write-Host (
        "Character SID200 stat patch checks passed: $assertions assertions.")
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
