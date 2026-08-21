[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$patcher = Join-Path $PSScriptRoot 'PatchClientLoginLogo.ps1'
$canonical = Join-Path (Split-Path $PSScriptRoot -Parent) `
    'assets\client-branding\gamelogo-reborn.gwo'
if ((Get-Item -LiteralPath $patcher).Length -ge 20KB) {
    throw 'PatchClientLoginLogo.ps1 exceeds the repository 20KB limit.'
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function New-TgaFixture {
    param(
        [string]$Path,
        [byte]$Blue,
        [byte]$Green,
        [byte]$Red
    )
    $bytes = [byte[]]::new(18 + 512 * 512 * 4)
    $bytes[2] = 2
    $bytes[12] = 0
    $bytes[13] = 2
    $bytes[14] = 0
    $bytes[15] = 2
    $bytes[16] = 32
    $bytes[17] = 0x28
    for ($index = 18; $index -lt $bytes.Length; $index += 4) {
        $bytes[$index] = $Blue
        $bytes[$index + 1] = $Green
        $bytes[$index + 2] = $Red
        $bytes[$index + 3] = 255
    }
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Get-Hash {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$root = Join-Path ([IO.Path]::GetTempPath()) `
    ('reborn-login-logo-' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $root 'client'
$backups = Join-Path $root 'backups'
$source = Join-Path $root 'source.gwo'
$target = Join-Path $root 'target.gwo'
$relativePaths = @(
    'Localization\en_us\UI\Texture\gamelogo.gwo',
    'Localization\zh_cn\UI\Texture\gamelogo.gwo')

try {
    New-TgaFixture $source 10 20 30
    New-TgaFixture $target 70 80 90
    $pristineHash = Get-Hash $source
    $targetHash = Get-Hash $target
    foreach ($relative in $relativePaths) {
        $path = Join-Path $client $relative
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::Copy($source, $path)
    }

    $arguments = @{
        ClientRoot = $client
        AssetPath = $target
        BackupRoot = $backups
        ExpectedPristineSha256 = $pristineHash
        ExpectedTargetSha256 = $targetHash
    }
    $status = & $patcher -Mode Status @arguments
    Assert-True ($status.State -ceq 'Pristine') `
        'Pristine locale pair was not recognized.'

    $applied = & $patcher -Mode Apply @arguments -AllowMutation
    Assert-True ($applied.State -ceq 'Patched') `
        'Apply did not install the target logo.'
    Assert-True (
        -not [string]::IsNullOrWhiteSpace($applied.BackupDirectory)) `
        'Apply did not create a verified backup.'
    foreach ($relative in $relativePaths) {
        Assert-True ((Get-Hash (Join-Path $client $relative)) -ceq
                $targetHash) `
            "Apply produced the wrong bytes for $relative."
    }

    $idempotent = & $patcher -Mode Apply @arguments -AllowMutation
    Assert-True ($idempotent.State -ceq 'Patched' -and
        [string]::IsNullOrWhiteSpace($idempotent.BackupDirectory)) `
        'Idempotent Apply unexpectedly created another backup.'

    $rolledBack = & $patcher -Mode Rollback @arguments `
        -RollbackFrom $applied.BackupDirectory -AllowMutation
    Assert-True ($rolledBack.State -ceq 'Pristine') `
        'Rollback did not restore the pristine state.'
    foreach ($relative in $relativePaths) {
        Assert-True ((Get-Hash (Join-Path $client $relative)) -ceq
                $pristineHash) `
            "Rollback did not restore exact bytes for $relative."
    }

    $unknown = Join-Path $client $relativePaths[0]
    $bytes = [IO.File]::ReadAllBytes($unknown)
    $bytes[100] = $bytes[100] -bxor 0xFF
    [IO.File]::WriteAllBytes($unknown, $bytes)
    $failedClosed = $false
    try {
        $null = & $patcher -Mode Apply @arguments -AllowMutation
    }
    catch {
        $failedClosed = $_.Exception.Message -like '*state is Unknown*'
    }
    Assert-True $failedClosed 'Unknown client asset did not fail closed.'

    $canonicalBytes = [IO.File]::ReadAllBytes($canonical)
    Assert-True ($canonicalBytes.Length -eq 18 + 512 * 512 * 4) `
        'Canonical logo is not a 512x512 32-bit raw TGA.'
    Assert-True ($canonicalBytes[2] -eq 2 -and
        $canonicalBytes[16] -eq 32 -and
        $canonicalBytes[17] -eq 0x28) `
        'Canonical logo lost its reviewed TGA layout.'

    'Client login-logo patch checks passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
