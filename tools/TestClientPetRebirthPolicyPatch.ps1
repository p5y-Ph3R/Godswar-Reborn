[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetRebirthPolicy.ps1'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) (
    'artifacts\pet-rebirth-policy-test-' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $root 'client'
$backups = Join-Path $root 'backups'
$assertions = 0

function Assert-True([bool]$Value, [string]$Label) {
    if (-not $Value) { throw "Assertion failed: $Label" }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Restore-HelpLevel50([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $text = $encoding.GetString(
        $bytes, $offset, $bytes.Length - $offset)
    Assert-Equal ([regex]::Matches(
        $text,
        [regex]::Escape(
            'The first rebirth requires lvl 30.')).Count) 1 `
        'partial fixture source occurrence'
    $body = $encoding.GetBytes($text.Replace(
        'The first rebirth requires lvl 30.',
        'The first rebirth requires lvl 50.'))
    [byte[]]$output = [byte[]]::new($body.Length + $offset)
    if ($hasBom) {
        $output[0] = 0xEF; $output[1] = 0xBB; $output[2] = 0xBF
    }
    [Array]::Copy($body, 0, $output, $offset, $body.Length)
    [IO.File]::WriteAllBytes($Path, $output)
}

try {
    foreach ($locale in @('en_us', 'zh_cn')) {
        foreach ($relative in @(
                'Settings\Sys\Pet_Alter.xml',
                'UI\Base\LuaText.lua',
                'UI\XML\HelpSystemSkillConfig.lua')) {
            $source = Join-Path $FixtureRoot (
                "Localization\$locale\$relative")
            $target = Join-Path $client (
                "Localization\$locale\$relative")
            [IO.Directory]::CreateDirectory(
                [IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
    }

    $initial = & $patcher -ClientRoot $client -Mode Status
    if ($initial.Status -ne 'Ready') {
        & $patcher -ClientRoot $client -Mode Revert `
            -BackupRoot $backups | Out-Null
    }
    $stockBytes = @{}
    Get-ChildItem $client -Recurse -File | ForEach-Object {
        $stockBytes[$_.FullName] = [IO.File]::ReadAllBytes($_.FullName)
    }
    $ready = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $ready.Status 'Ready' 'stock status'
    Assert-Equal $ready.FirstRebirthLevel 50 'stock gate'

    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.FirstRebirthLevel 30 'patched gate'

    foreach ($locale in @('en_us', 'zh_cn')) {
        [xml]$xml = Get-Content -Raw -LiteralPath (Join-Path $client (
            "Localization\$locale\Settings\Sys\Pet_Alter.xml"))
        Assert-True (
            $xml.Alter.Resurge.Resurge.PetLv.StartsWith('30,80,100')) `
            "$locale XML level ladder"
    }
    $english = Get-Content -Raw -LiteralPath (Join-Path $client (
        'Localization\en_us\UI\Base\LuaText.lua'))
    Assert-True ($english.Contains(
        'The first rebirth requires lvl 30.')) 'English instructions'
    Assert-True ($english.Contains(
        'The first rebirth requires Level 30.')) 'English rejection'
    Assert-True ($english.Contains(
        'requires Level 30!')) 'English level alert'
    foreach ($locale in @('en_us', 'zh_cn')) {
        $help = Get-Content -Raw -LiteralPath (Join-Path $client (
            "Localization\$locale\UI\XML\HelpSystemSkillConfig.lua"))
        Assert-Equal ([regex]::Matches(
            $help,
            [regex]::Escape(
                'The first rebirth requires lvl 30.')).Count) 1 `
            "$locale help reference"
    }
    $chinese = Get-Content -Raw -LiteralPath (Join-Path $client (
        'Localization\zh_cn\UI\Base\LuaText.lua'))
    $chineseLevel30 = [regex]::Unescape(
        '\u5ba0\u7269\u7b2c\u4e00\u6b21\u8f6c\u751f' +
        '\uff0c\u9700\u8981\u8fbe\u523030\u7ea7')
    Assert-Equal ([regex]::Matches(
        $chinese,
        [regex]::Escape($chineseLevel30)).Count) 3 `
        'Chinese level-30 references'

    $again = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    $reverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    foreach ($path in $stockBytes.Keys) {
        Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stockBytes[$path],
            [IO.File]::ReadAllBytes($path))) "byte-exact revert $path"
    }

    & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    $coreHashes = @{}
    Get-ChildItem $client -Recurse -File |
        Where-Object Name -ne 'HelpSystemSkillConfig.lua' |
        ForEach-Object {
            $coreHashes[$_.FullName] =
                (Get-FileHash -Algorithm SHA256 $_.FullName).Hash
        }
    foreach ($locale in @('en_us', 'zh_cn')) {
        Restore-HelpLevel50 (Join-Path $client (
            "Localization\$locale\UI\XML\HelpSystemSkillConfig.lua"))
    }
    $partial = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $partial.Status 'PreviousPartial' `
        'previous four-resource partial status'
    Assert-Equal $partial.FirstRebirthLevel 30 `
        'partial server gate'
    & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    foreach ($path in $coreHashes.Keys) {
        Assert-Equal (Get-FileHash -Algorithm SHA256 $path).Hash `
            $coreHashes[$path] 'partial upgrade preserves patched core'
    }
    Assert-Equal (& $patcher -ClientRoot $client -Mode Status).Status `
        'Patched' 'partial upgrade completion'
    & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups | Out-Null

    $xmlPath = Join-Path $client (
        'Localization\en_us\Settings\Sys\Pet_Alter.xml')
    [byte[]]$corrupt = [IO.File]::ReadAllBytes($xmlPath)
    $corrupt[100] = $corrupt[100] -bxor 1
    [IO.File]::WriteAllBytes($xmlPath, $corrupt)
    $refused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $refused = $_.Exception.Message.Contains('Unsupported') }
    Assert-True $refused 'unknown client resource is refused'

    Write-Host "Pet rebirth policy patch checks passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
