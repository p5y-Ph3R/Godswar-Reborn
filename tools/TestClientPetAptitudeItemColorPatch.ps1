[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetAptitudeItemColor.ps1'
$testRoot = Join-Path $env:TEMP (
    'reborn-pet-aptitude-itemcolor-' + [guid]::NewGuid().ToString('N'))
$clientRoot = Join-Path $testRoot 'client'
$backupRoot = Join-Path $testRoot 'backups'
$gb2312 = [Text.Encoding]::GetEncoding(936)
$assertions = 0

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

try {
    foreach ($locale in @('en_us', 'zh_cn')) {
        $relative = "Localization\$locale\Settings\Sys\ItemColor.xml"
        $destination = Join-Path $clientRoot $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) |
            Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureRoot $relative) `
            -Destination $destination
    }
    $initial = & $patcher -ClientRoot $clientRoot -Mode Status
    if ($initial.Status -eq 'Patched') {
        & $patcher -ClientRoot $clientRoot -Mode Revert `
            -BackupRoot $backupRoot | Out-Null
    }
    $source = @{}
    $sourceNames = @{}
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        $source[$locale] = [IO.File]::ReadAllBytes($path)
        [xml]$sourceDocument = [IO.File]::ReadAllText($path, $gb2312)
        $sourceNames[$locale] = @(7..10 | ForEach-Object {
                $sourceDocument.SelectSingleNode(
                    "/ItemColor/Equip/Pet/Aptitude$_").BaseName
            })
    }
    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Status).Status `
        'Ready to apply' 'source status'
    $apply = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    $expected = @{}
    foreach ($locale in @('en_us', 'zh_cn')) {
        $names = $sourceNames[$locale]
        $expected[$locale] = @($names[2], $names[3], $names[1], $names[0])
    }
    $colors = @('YELLOW_TEXTCOLOR', 'GREEN_TEXTCOLOR',
        'TEAM_COLOR', 'GREEN_TEXTCOLOR')
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        [xml]$document = [IO.File]::ReadAllText($path, $gb2312)
        for ($index = 0; $index -lt 4; $index++) {
            $level = 7 + $index
            $node = $document.SelectSingleNode(
                "/ItemColor/Equip/Pet/Aptitude$level")
            Assert-Equal $node.BaseName $expected[$locale][$index] `
                "$locale aptitude $level name"
            Assert-Equal $node.BaseColor $colors[$index] `
                "$locale aptitude $level color"
        }
    }
    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Apply `
            -BackupRoot $backupRoot).Status 'Already patched' `
        'idempotent apply'
    Assert-Equal (& $patcher -ClientRoot $clientRoot -Mode Revert `
            -BackupRoot $backupRoot).Status 'Reverted' 'revert status'
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) `
            ([Convert]::ToBase64String($source[$locale])) `
            "$locale byte-exact revert"
    }
    Write-Host "Pet aptitude ItemColor patch passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $temp = [IO.Path]::GetFullPath($env:TEMP)
        if (-not $resolved.StartsWith(
                $temp, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolved)).StartsWith(
                'reborn-pet-aptitude-itemcolor-',
                [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected test directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
