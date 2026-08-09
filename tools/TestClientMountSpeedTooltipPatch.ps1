[CmdletBinding()]
param(
    [string] $FixtureExe = 'C:\Godswar Origin\Origin.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$patcher = Join-Path $PSScriptRoot 'PatchClientMountSpeedTooltip.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot (
    'artifacts\mount-speed-parser-test-' +
    [Guid]::NewGuid().ToString('N'))
$clientRoot = Join-Path $testRoot 'client'
$backupRoot = Join-Path $testRoot 'backups'
$clientExe = Join-Path $clientRoot 'Origin.exe'
$assertions = 0

function Convert-HexBytes([string] $Hex) {
    $normalized = $Hex -replace '\s', ''
    [byte[]] $bytes = for ($index = 0;
        $index -lt $normalized.Length;
        $index += 2) {
        [Convert]::ToByte($normalized.Substring($index, 2), 16)
    }
    return $bytes
}

function Assert-True([bool] $Condition, [string] $Label) {
    if (-not $Condition) {
        throw "Assertion failed: $Label"
    }
    $script:assertions++
}

function Assert-Throws([scriptblock] $Operation, [string] $Fragment) {
    try {
        & $Operation
    }
    catch {
        Assert-True ($_.Exception.Message -like "*$Fragment*") (
            "expected error contains '$Fragment'")
        return
    }
    throw "Expected operation to fail with '$Fragment'."
}

function Set-Bytes(
    [byte[]] $Data,
    [int] $Offset,
    [byte[]] $Value
) {
    [Array]::Copy($Value, 0, $Data, $Offset, $Value.Length)
}

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Fixture Origin.exe is missing: $FixtureExe"
    }
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    Copy-Item -LiteralPath $FixtureExe -Destination $clientExe

    $callOffset = 0x03D017
    $materializationOffset = 0x03D01C
    $originalCall = Convert-HexBytes 'E8 B8 D7 37 00'
    $patchedCall = Convert-HexBytes 'E8 78 DB 37 00'
    $originalMaterialization = Convert-HexBytes (
        '89 44 24 1C DB 44 24 1C')
    $patchedMaterialization = Convert-HexBytes (
        '90 90 90 90 90 90 90 90')

    [byte[]] $fixture = [IO.File]::ReadAllBytes($clientExe)
    $callIsKnown =
        [Convert]::ToBase64String(
            $fixture[$callOffset..($callOffset + 4)]) -in @(
                [Convert]::ToBase64String($originalCall),
                [Convert]::ToBase64String($patchedCall))
    Assert-True $callIsKnown 'fixture call site has a known state'
    Set-Bytes $fixture $callOffset $originalCall
    Set-Bytes $fixture $materializationOffset $originalMaterialization
    [IO.File]::WriteAllBytes($clientExe, $fixture)

    Assert-Throws {
        & $patcher -ClientRoot $clientRoot `
            -BackupRoot $backupRoot -Check
    } 'not installed'

    [byte[]] $before = [IO.File]::ReadAllBytes($clientExe)
    [byte[]] $expected = [byte[]]::new($before.Length)
    [Array]::Copy($before, $expected, $before.Length)
    Set-Bytes $expected $callOffset $patchedCall
    Set-Bytes $expected $materializationOffset $patchedMaterialization

    & $patcher -ClientRoot $clientRoot -BackupRoot $backupRoot
    & $patcher -ClientRoot $clientRoot -BackupRoot $backupRoot -Check

    [byte[]] $after = [IO.File]::ReadAllBytes($clientExe)
    Assert-True (
        [Convert]::ToBase64String($after) -ceq
        [Convert]::ToBase64String($expected)) (
        'binary delta is limited to the final Speed conversion')
    Assert-True (
        @(Get-ChildItem -LiteralPath $backupRoot -Recurse -File).Count -eq 1) (
        'apply creates exactly one Origin.exe backup')

    & $patcher -ClientRoot $clientRoot -BackupRoot $backupRoot
    Assert-True (
        @(Get-ChildItem -LiteralPath $backupRoot -Recurse -File).Count -eq 1) (
        'idempotent apply creates no second backup')

    [byte[]] $partial = [IO.File]::ReadAllBytes($clientExe)
    Set-Bytes $partial $callOffset $originalCall
    [IO.File]::WriteAllBytes($clientExe, $partial)
    Assert-Throws {
        & $patcher -ClientRoot $clientRoot `
            -BackupRoot $backupRoot -Check
    } 'partially applied'

    Write-Host (
        "Mount Speed parser patch checks passed: $assertions assertions.")
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
