[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot `
    'PatchClientPetSealBindingPolicy.ps1'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) (
    'artifacts\pet-seal-binding-policy-test-' +
    [Guid]::NewGuid().ToString('N'))
$client = Join-Path $root 'client'
$backups = Join-Path $root 'backups'
$assertions = 0

function Assert-True([bool]$Value, [string]$Label) {
    if (-not $Value) {
        throw "Assertion failed: $Label"
    }
    $script:assertions++
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Expand-UnicodeLiteral([string]$Value) {
    [regex]::Replace(
        $Value,
        '\\u([0-9A-Fa-f]{4})',
        {
            param($match)
            [char][Convert]::ToUInt16($match.Groups[1].Value, 16)
        })
}

try {
    $paths = @()
    foreach ($locale in @('en_us', 'zh_cn')) {
        $source = Join-Path $FixtureRoot (
            "Localization\$locale\UI\Base\LuaText.lua")
        $target = Join-Path $client (
            "Localization\$locale\UI\Base\LuaText.lua")
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        $paths += $target
    }

    $initial = & $patcher -ClientRoot $client -Mode Status
    if ($initial.Status -eq 'Patched') {
        & $patcher -ClientRoot $client -Mode Revert `
            -BackupRoot $backups | Out-Null
    }
    $stockBytes = @{}
    foreach ($path in $paths) {
        $stockBytes[$path] = [IO.File]::ReadAllBytes($path)
    }

    $ready = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $ready.Status 'Ready' 'stock status'
    Assert-Equal $ready.BoundPetPackedJade `
        'FormerRuleRejected' 'stock bound-pet policy'
    Assert-Equal $ready.Resources 2 'guarded locale count'

    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    Assert-Equal $applied.BoundPetPackedJade `
        'BoundNonTradable' 'patched bound-pet policy'

    $english = Get-Content -Raw -LiteralPath $paths[0]
    Assert-True ($english.Contains(
        'A bound pet is sealed into a |cff39D8B8bound, non-tradable')) `
        'English bound packed-jade policy'
    Assert-True ($english.Contains(
        'only a tradable packed jade can transfer its pet') -and
        $english.Contains(
            'One empty jade transforms in place and needs no free bag slot') -and
        $english.Contains(
            'A stack needs at least |cff39D8B81 empty bag slot')) `
        'English transfer and conditional free-slot policy'
    Assert-True ($english.Contains(
        'Its tradability matches the sealed pet')) `
        'English success result'
    Assert-True ($english.Contains(
        'This request used the former sealing rule')) `
        'English old-replay result'

    $chinese = Get-Content -Raw -LiteralPath $paths[1]
    Assert-True ($chinese.Contains((Expand-UnicodeLiteral `
        '\u7ed1\u5b9a\u4e14\u4e0d\u53ef\u4ea4\u6613'))) `
        'Chinese bound packed-jade policy'
    Assert-True ($chinese.Contains(
        (Expand-UnicodeLiteral `
            '\u53ea\u6709\u53ef\u4ea4\u6613\u7075\u7389\u624d\u80fd\u5c06\u5ba0\u7269\u8f6c\u7ed9\u5176\u4ed6\u73a9\u5bb6')) -and
        $chinese.Contains((Expand-UnicodeLiteral `
            '\u4e00\u679a\u7a7a\u7075\u7389\u4f1a\u539f\u4f4d\u53d8\u4e3a\u5df2\u5c01\u5370\u7075\u7389\uff0c\u4e0d\u9700\u8981\u7a7a\u5305\u88f9\u4f4d')) -and
        $chinese.Contains((Expand-UnicodeLiteral `
            '\u7a7a\u7075\u7389\u6210\u53e0\u65f6\uff0c\u5305\u88f9\u81f3\u5c11\u9700\u8981\u4e00\u4e2a\u7a7a\u4f4d'))) `
        'Chinese transfer and conditional free-slot policy'
    Assert-True ($chinese.Contains(
        (Expand-UnicodeLiteral `
            '\u7075\u7389\u7684\u7ed1\u5b9a\u72b6\u6001\u4e0e\u5ba0\u7269\u4e00\u81f4'))) `
        'Chinese success result'
    Assert-True ($chinese.Contains(
        (Expand-UnicodeLiteral `
            '\u6b64\u8bf7\u6c42\u4f7f\u7528\u4e86\u65e7\u7248\u5c01\u5370\u89c4\u5219'))) `
        'Chinese old-replay result'

    $again = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'
    Assert-Equal (& $patcher -ClientRoot $client -Mode Status).Status `
        'Patched' 'patched status'

    $reverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    foreach ($path in $paths) {
        Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stockBytes[$path],
            [IO.File]::ReadAllBytes($path))) "byte-exact revert $path"
    }

    & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups | Out-Null
    [IO.File]::WriteAllBytes($paths[0], $stockBytes[$paths[0]])
    $mixedRefused = $false
    try {
        & $patcher -ClientRoot $client -Mode Status | Out-Null
    }
    catch {
        $mixedRefused = $_.Exception.Message.Contains('mixed state')
    }
    Assert-True $mixedRefused 'mixed locale state is refused'

    [IO.File]::WriteAllBytes($paths[1], $stockBytes[$paths[1]])
    [byte[]]$corrupt = [IO.File]::ReadAllBytes($paths[0])
    $corrupt[100] = $corrupt[100] -bxor 1
    [IO.File]::WriteAllBytes($paths[0], $corrupt)
    $unknownRefused = $false
    try {
        & $patcher -ClientRoot $client -Mode Status | Out-Null
    }
    catch {
        $unknownRefused = $_.Exception.Message.Contains('Unsupported')
    }
    Assert-True $unknownRefused 'unknown resource hash is refused'

    Write-Host (
        "Pet Seal binding policy patch checks passed: " +
        "$assertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
