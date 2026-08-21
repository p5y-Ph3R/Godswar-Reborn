[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientZodiacSkillTooltip.ps1'
$testRoot = Join-Path $env:TEMP (
    'reborn-zodiac-skill-tooltip-' + [guid]::NewGuid().ToString('N'))
$clientRoot = Join-Path $testRoot 'client'
$backupRoot = Join-Path $testRoot 'backups'
$seedBackupRoot = Join-Path $testRoot 'seed-backups'
$locales = @('en_us', 'zh_cn')
$sourceHash =
    '6A6F17DF922B1D32A298156105A198141506C44E40FB79464524653235A55B4F'
$patchedHash =
    '4D5E1B152FAC41BBE5527D8A0DBDFB4AFC8BC589BEDA418E9B784C755EFD4E69'
$assertions = 0

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) {
        throw "Assertion failed: $Label"
    }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Assert-Throws([scriptblock]$Operation, [string]$Fragment) {
    try {
        & $Operation | Out-Null
    }
    catch {
        Assert-True ($_.Exception.Message -like "*$Fragment*") (
            "expected error contains '$Fragment'")
        return
    }
    throw "Expected operation to fail with '$Fragment'."
}

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-BackupDirectoryCount([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return 0
    }
    return @(Get-ChildItem -LiteralPath $Root -Directory).Count
}

function Get-GridArray(
    [string]$Text,
    [int]$Grid,
    [string]$Field
) {
    $block = [regex]::Match(
        $Text,
        "(?s)SkillTrain_ConfigGird\[$Grid\]\s*=\s*\{(.*?)\r?\n\}")
    if (-not $block.Success) {
        throw "Grid $Grid is missing from SkillTrainConfig.lua."
    }
    $array = [regex]::Match(
        $block.Groups[1].Value,
        ([regex]::Escape($Field) + '=\{([^}]*)\}'))
    if (-not $array.Success) {
        throw "$Field is missing from grid $Grid."
    }
    return @($array.Groups[1].Value -split ',' | ForEach-Object {
            $_.Trim().Trim("'")
        })
}

function Assert-TooltipSourceContract([string]$Path, [string]$Locale) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    Assert-True ($bytes.Length -gt 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) "$Locale UTF-8 BOM preserved"
    for ($index = 3; $index -lt $bytes.Length; $index++) {
        if ($bytes[$index] -eq 0x0A) {
            Assert-True ($bytes[$index - 1] -eq 0x0D) (
                "$Locale newline at byte $index remains CRLF")
        }
    }
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    Assert-Equal ([regex]::Matches(
            $text, 'REBORN_ZODIAC_SKILL_TOOLTIP_BEGIN').Count) 1 (
        "$Locale patch marker")
    Assert-Equal ([regex]::Matches(
            $text, 'ST_X0_12\.\.lev\.\."/50"').Count) 1 (
        "$Locale /50 display")
    Assert-Equal ([regex]::Matches(
            $text, 'ST_X0_12\.\.lev\.\."/40"').Count) 0 (
        "$Locale old /40 display removed")
    Assert-Equal ([regex]::Matches(
            $text,
            'SkillTrain_GetDisplayedMP\(gird,index,lev\+1\)').Count) 1 (
        "$Locale next MP uses capped helper")
    Assert-Equal ([regex]::Matches(
            $text,
            'ST_X0_13\.\.SkillTrain_GetDisplayedMP\(gird,index,lev\)').Count) 1 (
        "$Locale current MP uses capped helper")
    Assert-Equal ([regex]::Matches(
            $text, 'gird\.SkillEff\[lev\+1\]').Count) 4 (
        "$Locale next SkillEff remains uncapped")
    Assert-Equal ([regex]::Matches(
            $text, 'gird\.SkillEff\[lev\]').Count) 4 (
        "$Locale current SkillEff remains uncapped")
}

try {
    $fixtureHashes = @{}
    foreach ($locale in $locales) {
        foreach ($name in @('SkillTrainProc.lua', 'SkillTrainConfig.lua')) {
            $relative = "Localization\$locale\UI\XML\$name"
            $source = Join-Path $FixtureRoot $relative
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Fixture asset is missing: $source"
            }
            $destination = Join-Path $clientRoot $relative
            [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
                Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
            $fixtureHashes[$source] = Get-Hash $source
        }
    }

    $initial = & $patcher -ClientRoot $clientRoot -Mode Status
    if ($initial.Status -eq 'Patched') {
        & $patcher -ClientRoot $clientRoot -Mode Revert `
            -BackupRoot $seedBackupRoot | Out-Null
    }

    $sourceBytes = @{}
    foreach ($locale in $locales) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\UI\XML\SkillTrainProc.lua")
        $sourceBytes[$locale] = [IO.File]::ReadAllBytes($path)
    }

    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Status).Status `
        'Ready to apply' 'original status'
    Assert-Equal (Get-BackupDirectoryCount $backupRoot) 0 (
        'status creates no backup')
    foreach ($locale in $locales) {
        $configPath = Join-Path $clientRoot (
            "Localization\$locale\UI\XML\SkillTrainConfig.lua")
        $config = [IO.File]::ReadAllText($configPath, [Text.Encoding]::UTF8)
        foreach ($grid in 5..8) {
            $mp = @(Get-GridArray $config $grid 'MP')
            $effect = @(Get-GridArray $config $grid 'SkillEff')
            Assert-Equal $mp.Count 45 "$locale grid $grid authored MP count"
            Assert-Equal $effect.Count 50 (
                "$locale grid $grid authored effect count")
            Assert-Equal $effect[48] '1.18' (
                "$locale grid $grid level 49 effect")
            Assert-Equal $effect[49] '1.20' (
                "$locale grid $grid level 50 effect")
            foreach ($level in 45..50) {
                $currentIndex = [Math]::Min($level, 45) - 1
                $nextIndex = [Math]::Min($level + 1, 45) - 1
                Assert-Equal $mp[$currentIndex] '300%' (
                    "$locale grid $grid level $level current MP cap")
                Assert-Equal $mp[$nextIndex] '300%' (
                    "$locale grid $grid level $level next MP cap")
                Assert-True (-not [string]::IsNullOrWhiteSpace(
                        $effect[$level - 1])) (
                    "$locale grid $grid level $level effect remains present")
            }
        }
    }

    $apply = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    Assert-True (Test-Path -LiteralPath $apply.Backup -PathType Container) (
        'apply backup exists')
    Assert-True (Test-Path -LiteralPath (
            (Join-Path $apply.Backup 'manifest.json')) -PathType Leaf) (
        'apply manifest exists')
    $manifest = Get-Content -LiteralPath (
        (Join-Path $apply.Backup 'manifest.json')) -Raw | ConvertFrom-Json
    Assert-Equal $manifest.schemaVersion 1 'manifest schema version'
    Assert-Equal $manifest.patch 'client-zodiac-skill-tooltip' (
        'manifest patch name')
    Assert-Equal $manifest.mode 'Apply' 'manifest mode'
    Assert-Equal @($manifest.files).Count 2 'manifest file count'
    foreach ($entry in @($manifest.files)) {
        Assert-True ($entry.relativePath -in @(
                'Localization/en_us/UI/XML/SkillTrainProc.lua',
                'Localization/zh_cn/UI/XML/SkillTrainProc.lua')) (
            'manifest locale path')
        Assert-Equal $entry.beforeSha256 $sourceHash (
            'manifest predecessor hash')
        Assert-Equal $entry.afterSha256 $patchedHash (
            'manifest target hash')
    }
    $patchedBytes = @{}
    foreach ($locale in $locales) {
        $relative = "Localization\$locale\UI\XML\SkillTrainProc.lua"
        $path = Join-Path $clientRoot $relative
        Assert-Equal (Get-Hash $path) $patchedHash "$locale patched hash"
        Assert-TooltipSourceContract $path $locale
        $patchedBytes[$locale] = [IO.File]::ReadAllBytes($path)
        $backup = Join-Path $apply.Backup $relative
        Assert-Equal ([Convert]::ToBase64String(
                [IO.File]::ReadAllBytes($backup))) (
            [Convert]::ToBase64String($sourceBytes[$locale])) (
            "$locale apply backup is byte-exact")
    }
    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Status).Status `
        'Patched' 'patched status'

    $backupCount = Get-BackupDirectoryCount $backupRoot
    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Apply `
            -BackupRoot $backupRoot).Status 'Already patched' (
        'idempotent apply')
    Assert-Equal (Get-BackupDirectoryCount $backupRoot) $backupCount (
        'idempotent apply creates no backup')

    $zhPath = Join-Path $clientRoot (
        'Localization\zh_cn\UI\XML\SkillTrainProc.lua')
    [IO.File]::WriteAllBytes($zhPath, $sourceBytes['zh_cn'])
    Assert-Throws {
        & $patcher -ClientRoot $clientRoot -Mode Status
    } 'different states'
    [IO.File]::WriteAllBytes($zhPath, $patchedBytes['zh_cn'])

    $enPath = Join-Path $clientRoot (
        'Localization\en_us\UI\XML\SkillTrainProc.lua')
    [byte[]]$foreign = [IO.File]::ReadAllBytes($enPath)
    $foreign[$foreign.Length - 1] = $foreign[$foreign.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($enPath, $foreign)
    Assert-Throws {
        & $patcher -ClientRoot $clientRoot -Mode Status
    } 'Unsupported en_us'
    [IO.File]::WriteAllBytes($enPath, $patchedBytes['en_us'])

    $revert = & $patcher -ClientRoot $clientRoot -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Equal $revert.Status 'Reverted' 'revert status'
    foreach ($locale in $locales) {
        $relative = "Localization\$locale\UI\XML\SkillTrainProc.lua"
        $path = Join-Path $clientRoot $relative
        Assert-Equal ([Convert]::ToBase64String(
                [IO.File]::ReadAllBytes($path))) (
            [Convert]::ToBase64String($sourceBytes[$locale])) (
            "$locale byte-exact revert")
        Assert-Equal (Get-Hash (Join-Path $revert.Backup $relative)) (
            $patchedHash) "$locale revert backup contains patched script"
    }
    $backupCount = Get-BackupDirectoryCount $backupRoot
    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Revert `
            -BackupRoot $backupRoot).Status 'Already reverted' (
        'idempotent revert')
    Assert-Equal (Get-BackupDirectoryCount $backupRoot) $backupCount (
        'idempotent revert creates no backup')
    Assert-Equal @(Get-ChildItem -LiteralPath $clientRoot -Recurse -File |
            Where-Object Extension -eq '.stage').Count 0 (
        'no staging files remain')

    foreach ($source in $fixtureHashes.Keys) {
        Assert-Equal (Get-Hash $source) $fixtureHashes[$source] (
            "fixture remained read-only: $source")
    }
    Write-Host (
        "Zodiac skill tooltip patch checks passed: $assertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $temp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
        $leaf = [IO.Path]::GetFileName($resolved)
        if (-not $resolved.StartsWith(
                $temp, [StringComparison]::OrdinalIgnoreCase) -or
            -not $leaf.StartsWith(
                'reborn-zodiac-skill-tooltip-',
                [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected test directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
