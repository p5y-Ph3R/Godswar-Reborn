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
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$legacyHashes = @{
    en_us = '8202DBF6F83DE1B0916FC140AA93337414FF8DEC049AE5CBB7BAF2903806E91A'
    zh_cn = '99C72FB3818A3C3AB5A1B5CFB0278A43F2339B37CCE7F1A6390FB05BECA625A9'
}
$assertions = 0

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Assert-True([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "$Label expected true." }
    $script:assertions++
}

function Assert-Receipt($Result, [string]$BeforeState, [string]$AfterState) {
    Assert-True (Test-Path -LiteralPath $Result.Receipt -PathType Leaf) `
        'receipt exists'
    Assert-Equal (Get-FileHash -LiteralPath $Result.Receipt -Algorithm SHA256).Hash `
        $Result.ReceiptSha256 'receipt hash'
    $receipt = [IO.File]::ReadAllText($Result.Receipt, $utf8NoBom) |
        ConvertFrom-Json
    Assert-Equal $receipt.Schema `
        'reborn.client-pet-aptitude-itemcolor/v2' 'receipt schema'
    Assert-Equal $receipt.SourceState $BeforeState 'receipt source state'
    Assert-Equal $receipt.TargetState $AfterState 'receipt target state'
    Assert-Equal @($receipt.Locales).Count 2 'receipt locale count'
    foreach ($record in @($receipt.Locales)) {
        Assert-True (Test-Path -LiteralPath $record.BackupPath -PathType Leaf) `
            "$($record.Locale) receipt backup exists"
        Assert-Equal (Get-FileHash -LiteralPath $record.BackupPath `
                -Algorithm SHA256).Hash $record.BeforeSha256 `
            "$($record.Locale) receipt backup hash"
        $live = Join-Path $clientRoot $record.RelativePath
        Assert-Equal (Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash `
            $record.AfterSha256 "$($record.Locale) receipt installed hash"
    }
}

function Install-LegacyProjectState {
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        $text = [IO.File]::ReadAllText($path, $gb2312)
        [xml]$document = $text
        $names = @(7..10 | ForEach-Object {
                $document.SelectSingleNode(
                    "/ItemColor/Equip/Pet/Aptitude$_").BaseName
            })
        $desired = @($names[2], $names[3], $names[1], $names[0])
        for ($index = 0; $index -lt 4; $index++) {
            $level = 7 + $index
            $old = 'BaseLv="{0}" BaseName="{1}"' -f $level, $names[$index]
            $new = 'BaseLv="{0}" BaseName="{1}"' -f $level, $desired[$index]
            Assert-Equal ([regex]::Matches(
                    $text, [regex]::Escape($old)).Count) 1 `
                "$locale legacy aptitude $level guard"
            $text = $text.Replace($old, $new)
        }
        [IO.File]::WriteAllText($path, $text, $gb2312)
        Assert-Equal (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash `
            $legacyHashes[$locale] "$locale legacy hash"
    }
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
    if ($initial.State -ne 'StockOrder') {
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
    foreach ($unsafeBackup in @($clientRoot, (Join-Path $clientRoot 'backups'))) {
        $rejected = $false
        try {
            & $patcher -ClientRoot $clientRoot -Mode Apply `
                -BackupRoot $unsafeBackup | Out-Null
        }
        catch {
            $rejected = $_.Exception.Message -like
                '*BackupRoot must be outside the client directory*'
        }
        Assert-True $rejected "unsafe backup root $unsafeBackup rejected"
    }
    $apply = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $apply.Status 'Patched' 'apply status'
    Assert-Equal $apply.SourceState 'StockOrder' 'apply source state'
    Assert-Equal $apply.State 'ProjectOrder' 'apply target state'
    Assert-Equal $apply.SmartColor 'YELLOW_TEXTCOLOR' 'apply Smart color'
    Assert-Receipt $apply 'StockOrder' 'ProjectOrder'
    $expected = @{}
    foreach ($locale in @('en_us', 'zh_cn')) {
        $names = $sourceNames[$locale]
        $expected[$locale] = @($names[2], $names[3], $names[1], $names[0])
    }
    $colors = @('YELLOW_TEXTCOLOR', 'GREEN_TEXTCOLOR',
        'TEAM_COLOR', 'YELLOW_TEXTCOLOR')
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
    $revert = & $patcher -ClientRoot $clientRoot -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Equal $revert.Status 'Reverted' 'revert status'
    Assert-Receipt $revert 'ProjectOrder' 'StockOrder'
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) `
            ([Convert]::ToBase64String($source[$locale])) `
            "$locale byte-exact revert"
    }

    Install-LegacyProjectState
    $legacy = & $patcher -ClientRoot $clientRoot -Mode Status
    Assert-Equal $legacy.Status 'Migration required' 'legacy status'
    Assert-Equal $legacy.State 'LegacyProjectOrder' 'legacy state'
    Assert-Equal $legacy.SmartColor 'GREEN_TEXTCOLOR' 'legacy Smart color'
    $migrate = & $patcher -ClientRoot $clientRoot -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Equal $migrate.Status 'Patched' 'migration apply status'
    Assert-Equal $migrate.SourceState 'LegacyProjectOrder' `
        'migration source state'
    Assert-Receipt $migrate 'LegacyProjectOrder' 'ProjectOrder'
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        [xml]$document = [IO.File]::ReadAllText($path, $gb2312)
        $smart = $document.SelectSingleNode(
            '/ItemColor/Equip/Pet/Aptitude10')
        Assert-Equal $smart.BaseName $expected[$locale][3] `
            "$locale migrated Smart name"
        Assert-Equal $smart.BaseColor 'YELLOW_TEXTCOLOR' `
            "$locale migrated Smart color"
    }
    $legacyRevert = & $patcher -ClientRoot $clientRoot -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Receipt $legacyRevert 'ProjectOrder' 'StockOrder'
    foreach ($locale in @('en_us', 'zh_cn')) {
        $path = Join-Path $clientRoot (
            "Localization\$locale\Settings\Sys\ItemColor.xml")
        Assert-Equal ([Convert]::ToBase64String(
                [IO.File]::ReadAllBytes($path))) `
            ([Convert]::ToBase64String($source[$locale])) `
            "$locale post-migration byte-exact revert"
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
