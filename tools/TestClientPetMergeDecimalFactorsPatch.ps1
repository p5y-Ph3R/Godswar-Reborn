[CmdletBinding()]
param([string]$FixtureRoot = 'C:\Godswar Origin')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$patcher = Join-Path $PSScriptRoot 'PatchClientPetMergeDecimalFactors.ps1'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) (
    'artifacts\pet-merge-factor-test-' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $root 'client'
$backups = Join-Path $root 'backups'
$locales = @('en_us', 'zh_cn')
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

try {
    [IO.Directory]::CreateDirectory($client) | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'Origin.exe') `
        -Destination (Join-Path $client 'Origin.exe')
    $paths = @()
    foreach ($locale in $locales) {
        $directory = Join-Path $client (
            "Localization\$locale\Settings\Sys")
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $path = Join-Path $directory 'Pet_Alter.xml'
        Copy-Item -LiteralPath (Join-Path $FixtureRoot (
            "Localization\$locale\Settings\Sys\Pet_Alter.xml")) `
            -Destination $path
        $paths += $path
    }

    $initial = & $patcher -ClientRoot $client -Mode Status
    if ($initial.Status -eq 'Patched') {
        & $patcher -ClientRoot $client -Mode Revert `
            -BackupRoot $backups | Out-Null
    }
    $stock = [Collections.Generic.List[byte[]]]::new()
    foreach ($path in $paths) {
        $stock.Add([IO.File]::ReadAllBytes($path))
    }
    $ready = & $patcher -ClientRoot $client -Mode Status
    Assert-Equal $ready.Status 'Ready' 'stock status'
    Assert-Equal $ready.Factors 'stock-binary32' 'stock factor semantics'

    $applied = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $applied.Status 'Patched' 'apply status'
    foreach ($path in $paths) {
        [xml]$xml = Get-Content -LiteralPath $path -Raw
        $factors = @($xml.SelectNodes('/Alter/Inosculate/typePoint/*'))
        Assert-Equal @($factors | Where-Object {
            $_.Values -eq '1.4001' }).Count 2 '1.4001 factor count'
        Assert-Equal @($factors | Where-Object {
            $_.Values -eq '2.6001' }).Count 39 '2.6001 factor count'
        Assert-Equal @($factors | Where-Object {
            $_.Values -eq '0.8' }).Count 4 '0.8 factors stay exact'
    }

    foreach ($factor in @(
            [pscustomobject]@{ Stock = 1.4; Patched = [single]1.4001 },
            [pscustomobject]@{ Stock = 2.6; Patched = [single]2.6001 })) {
        foreach ($value in 1..300) {
            Assert-Equal (
                [int][Math]::Truncate($value * [double]$factor.Patched)) (
                [int][Math]::Truncate($value * [decimal]$factor.Stock)) `
                "factor $($factor.Stock) lookup $value"
        }
    }
    Assert-Equal ([int][Math]::Truncate(300 * [single]2.6001)) 780 `
        'screenshot capped increase'
    Assert-Equal ([int][Math]::Truncate(162 * [single]2.6001)) 421 `
        'screenshot Luck increase'

    $again = & $patcher -ClientRoot $client -Mode Apply `
        -BackupRoot $backups
    Assert-Equal $again.Status 'Already patched' 'idempotent apply'

    $reverted = & $patcher -ClientRoot $client -Mode Revert `
        -BackupRoot $backups
    Assert-Equal $reverted.Status 'Reverted' 'revert status'
    for ($index = 0; $index -lt $paths.Count; $index++) {
        Assert-True ([Linq.Enumerable]::SequenceEqual(
            $stock[$index], [IO.File]::ReadAllBytes($paths[$index]))) `
            "$($locales[$index]) byte-exact revert"
    }

    $bytes = [IO.File]::ReadAllBytes($paths[0])
    $bytes[100] = $bytes[100] -bxor 1
    [IO.File]::WriteAllBytes($paths[0], $bytes)
    $refused = $false
    try { & $patcher -ClientRoot $client -Mode Status | Out-Null }
    catch { $refused = $_.Exception.Message.Contains('Unsupported Pet_Alter.xml') }
    Assert-True $refused 'unknown resource is refused'

    Write-Host "Pet Merge decimal-factor checks passed: $assertions assertions."
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
