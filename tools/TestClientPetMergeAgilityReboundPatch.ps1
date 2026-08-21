[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot `
    'PatchClientPetMergeAgilityRebound.ps1'
$factorPatcher = Join-Path $PSScriptRoot `
    'PatchClientPetMergeDecimalFactors.ps1'
$rebirthPatcher = Join-Path $PSScriptRoot `
    'PatchClientPetRebirthPolicy.ps1'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) (
    'artifacts\pet-merge-agility-rebound-test-' +
    [Guid]::NewGuid().ToString('N'))
$client = Join-Path $root 'client'
$backups = Join-Path $root 'backups'
$locales = @('en_us', 'zh_cn')
$assertions = 0

function Assert-True([bool]$Value, [string]$Label) {
    if (-not $Value) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Get-Hash([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-PetAlterPaths {
    foreach ($locale in $locales) {
        Join-Path $client (
            "Localization\$locale\Settings\Sys\Pet_Alter.xml")
    }
}

function Get-ClientSnapshot {
    $snapshot = @{}
    Get-ChildItem -LiteralPath $client -Recurse -File | ForEach-Object {
        $snapshot[$_.FullName] = Get-Hash $_.FullName
    }
    $snapshot
}

function Assert-ClientSnapshot($Expected, [string]$Label) {
    foreach ($path in $Expected.Keys) {
        Assert-Equal (Get-Hash $path) $Expected[$path] "$Label $path"
    }
}

function Assert-ReboundState(
    [string]$ExpectedStatus,
    [string]$ExpectedRebound,
    [string]$ExpectedFactors,
    [string]$ExpectedRebirth,
    [string]$Label
) {
    $status = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $status.Status $ExpectedStatus "$Label status"
    Assert-Equal $status.AgilityDamageRebound $ExpectedRebound `
        "$Label Agility state"
    Assert-Equal $status.LuckDamageRebound 'Enabled' "$Label Luck state"
    Assert-Equal $status.Factors $ExpectedFactors "$Label factor state"
    Assert-Equal $status.Rebirth $ExpectedRebirth "$Label rebirth state"
    foreach ($path in Get-PetAlterPaths) {
        [xml]$xml = Get-Content -LiteralPath $path -Raw
        $agility = @($xml.SelectNodes(
            '/Alter/Unite/Trait[@Type="1"]/*[@Effect="38"]'))
        $luck = @($xml.SelectNodes(
            '/Alter/Unite/Trait[@Type="6"]/*[@Effect="38"]'))
        $expectedValues = if ($ExpectedRebound -eq 'Disabled') {
            '0,0,0,0,0'
        }
        else { '1.5,1.2,1,0.8,0.7' }
        Assert-Equal $agility.Count 1 "$Label Agility curve count"
        Assert-Equal $agility[0].Values $expectedValues `
            "$Label Agility curve"
        Assert-Equal $luck.Count 1 "$Label Luck curve count"
        Assert-Equal $luck[0].Values '6,4.8,3.9,3.3,2.7' `
            "$Label Luck curve preserved"
        Assert-Equal @($xml.SelectNodes(
            '/Alter/Unite/Trait[@Type="3"]/*[@Effect="7" and ' +
            '@Values="1.5,1.2,1,0.8,0.7"]')).Count 1 `
            "$Label unrelated effect-7 curve"
    }
}

function Test-ReboundRoundTrip(
    [string]$Factors,
    [string]$Rebirth,
    [string]$Label
) {
    $before = @{}
    foreach ($path in Get-PetAlterPaths) {
        $before[$path] = Get-Hash $path
    }
    Assert-ReboundState 'Ready' 'Enabled' $Factors $Rebirth `
        "$Label enabled"
    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' "$Label apply"
    Assert-True (Test-Path -LiteralPath $applied.Backup -PathType Container) `
        "$Label backup exists"
    Assert-Equal @(Get-ChildItem -LiteralPath $applied.Backup -File).Count 2 `
        "$Label backup count"
    Assert-ReboundState 'Patched' 'Disabled' $Factors $Rebirth `
        "$Label disabled"
    $again = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' "$Label idempotent apply"
    $reverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' "$Label revert"
    foreach ($path in Get-PetAlterPaths) {
        Assert-Equal (Get-Hash $path) $before[$path] `
            "$Label byte-exact round trip"
    }
}

function Assert-ToolStates(
    [string]$Factors,
    [string]$Rebirth,
    [string]$Rebound,
    [string]$Label
) {
    $factor = & $factorPatcher -ClientRoot $client -Mode Status
    $rebirthStatus = & $rebirthPatcher -ClientRoot $client -Mode Status
    Assert-Equal $factor.AgilityDamageRebound $Rebound `
        "$Label factor tool preserves rebound"
    Assert-Equal $factor.Rebirth $Rebirth "$Label factor tool rebirth"
    Assert-Equal $rebirthStatus.AgilityDamageRebound $Rebound `
        "$Label rebirth tool preserves rebound"
    Assert-Equal $rebirthStatus.Factors $Factors `
        "$Label rebirth tool factors"
}

$fixturePaths = @(
    (Join-Path $FixtureRoot 'Origin.exe')
)
foreach ($locale in $locales) {
    foreach ($relative in @(
            'Settings\Sys\Pet_Alter.xml',
            'UI\Base\LuaText.lua',
            'UI\XML\HelpSystemSkillConfig.lua')) {
        $fixturePaths += Join-Path $FixtureRoot (
            "Localization\$locale\$relative")
    }
}
$fixtureHashes = @{}
foreach ($path in $fixturePaths) { $fixtureHashes[$path] = Get-Hash $path }

try {
    foreach ($source in $fixturePaths) {
        $relative = $source.Substring(
            [IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\').Length).
            TrimStart('\')
        $target = Join-Path $client $relative
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }

    if ((& $patcher -ClientRoot $client -Mode Status).Status -eq 'Patched') {
        & $patcher -ClientRoot $client -Mode Revert `
            -BackupRoot $backups | Out-Null
    }
    if ((& $factorPatcher -ClientRoot $client -Mode Status).Status -ne
            'Patched') {
        & $factorPatcher -ClientRoot $client -Mode Apply `
            -BackupRoot $backups | Out-Null
    }
    if ((& $rebirthPatcher -ClientRoot $client -Mode Status).Status -ne
            'Patched') {
        & $rebirthPatcher -ClientRoot $client -Mode Apply `
            -BackupRoot $backups | Out-Null
    }
    $normalized = Get-ClientSnapshot

    Test-ReboundRoundTrip 'Patched' 'Level30' 'decimal level-30'
    & $rebirthPatcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups | Out-Null
    Test-ReboundRoundTrip 'Patched' 'Level50' 'decimal level-50'
    & $factorPatcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups | Out-Null
    Test-ReboundRoundTrip 'Stock' 'Level50' 'stock level-50'
    & $rebirthPatcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    Test-ReboundRoundTrip 'Stock' 'Level30' 'stock level-30'
    & $factorPatcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    Assert-ClientSnapshot $normalized 'enabled matrix restoration'

    & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    & $rebirthPatcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups | Out-Null
    Assert-ReboundState 'Patched' 'Disabled' 'Patched' 'Level50' `
        'disabled decimal level-50'
    Assert-ToolStates 'Patched' 'Level50' 'Disabled' `
        'disabled decimal level-50'
    & $factorPatcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups | Out-Null
    Assert-ReboundState 'Patched' 'Disabled' 'Stock' 'Level50' `
        'disabled stock level-50'
    & $rebirthPatcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    Assert-ReboundState 'Patched' 'Disabled' 'Stock' 'Level30' `
        'disabled stock level-30'
    & $factorPatcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    Assert-ReboundState 'Patched' 'Disabled' 'Patched' 'Level30' `
        'disabled decimal level-30'
    Assert-ToolStates 'Patched' 'Level30' 'Disabled' `
        'disabled decimal level-30'
    & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups | Out-Null
    Assert-ClientSnapshot $normalized 'disabled composition restoration'

    $petPaths = @(Get-PetAlterPaths)
    [byte[]]$enabled = [IO.File]::ReadAllBytes($petPaths[0])
    & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    [IO.File]::WriteAllBytes($petPaths[1], $enabled)
    $mixedRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $mixedRefused = $_.Exception.Message.Contains('mixed state') }
    Assert-True $mixedRefused 'mixed locale state is refused'
    [IO.File]::WriteAllBytes($petPaths[0], $enabled)
    [IO.File]::WriteAllBytes($petPaths[1], $enabled)

    [byte[]]$corrupt = [IO.File]::ReadAllBytes($petPaths[0])
    $corrupt[100] = $corrupt[100] -bxor 1
    [IO.File]::WriteAllBytes($petPaths[0], $corrupt)
    $unknownRefused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $unknownRefused = $_.Exception.Message.Contains('Unsupported') }
    Assert-True $unknownRefused 'unknown resource is refused'
    [IO.File]::WriteAllBytes($petPaths[0], $enabled)
    Assert-ClientSnapshot $normalized 'negative-test restoration'

    foreach ($path in $fixturePaths) {
        Assert-Equal (Get-Hash $path) $fixtureHashes[$path] `
            "live fixture remains read-only $path"
    }
    Write-Host (
        "Pet Merge Agility-rebound checks passed: $assertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
