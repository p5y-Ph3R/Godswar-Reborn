[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Rollback')]
    [string]$Mode = 'Status',
    [string]$ClientRoot = 'C:\Godswar Origin',
    [string]$AssetPath = '',
    [string]$BackupRoot = '',
    [string]$RollbackFrom = '',
    [string]$ExpectedPristineSha256 =
        'A5167EA2E100EE7FA09045AB51E040B5CEB38A1026C6BF7633E5F8A452C1C888',
    [string]$ExpectedTargetSha256 =
        '4E4D5F754AF241E89B12E1051C3FDD572A1C0E42EDAEA3745AA5823CBF4336A6',
    [switch]$AllowMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$relativePaths = @(
    'Localization\en_us\UI\Texture\gamelogo.gwo',
    'Localization\zh_cn\UI\Texture\gamelogo.gwo')
if ([string]::IsNullOrWhiteSpace($AssetPath)) {
    $AssetPath = Join-Path (Split-Path $PSScriptRoot -Parent) `
        'assets\client-branding\gamelogo-reborn.gwo'
}
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path $PSScriptRoot -Parent) `
        'artifacts\client-login-logo-backups'
}

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-ContainedPath {
    param(
        [string]$Root,
        [string]$RelativePath
    )
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath((Join-Path $fullRoot $RelativePath))
    if (-not $fullPath.StartsWith(
            $fullRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset escapes its expected root: $RelativePath"
    }
    return $fullPath
}

function Assert-LoginLogoTga {
    param(
        [string]$Path,
        [string]$Label
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $width = if ($bytes.Length -ge 18) {
        [BitConverter]::ToUInt16($bytes, 12)
    } else { 0 }
    $height = if ($bytes.Length -ge 18) {
        [BitConverter]::ToUInt16($bytes, 14)
    } else { 0 }
    if ($bytes.Length -ne (18 + 512 * 512 * 4) -or
        $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or
        $bytes[2] -ne 2 -or $width -ne 512 -or $height -ne 512 -or
        $bytes[16] -ne 32 -or $bytes[17] -ne 0x28) {
        throw "$Label is not the reviewed 512x512 top-origin BGRA TGA layout."
    }
}

function Get-AssetState {
    param([string]$Path)
    Assert-LoginLogoTga $Path 'Client login logo'
    $hash = Get-Sha256Hex $Path
    $state = if ($hash -ceq $ExpectedTargetSha256) {
        'Patched'
    }
    elseif ($hash -ceq $ExpectedPristineSha256) {
        'Pristine'
    }
    else {
        'Unknown'
    }
    return [pscustomobject]@{
        Path = $Path
        State = $state
        Sha256 = $hash
    }
}

function Get-ClientAssets {
    return @($relativePaths | ForEach-Object {
        Get-AssetState (Get-ContainedPath $ClientRoot $_)
    })
}

function Get-OverallState {
    param([object[]]$Assets)
    if ($Assets.State -contains 'Unknown') {
        return 'Unknown'
    }
    if (@($Assets | Where-Object State -ceq 'Patched').Count -eq
        $Assets.Count) {
        return 'Patched'
    }
    if (@($Assets | Where-Object State -ceq 'Pristine').Count -eq
        $Assets.Count) {
        return 'Pristine'
    }
    return 'Mixed'
}

function Assert-ClientClosed {
    $running = @(Get-Process -Name Origin -ErrorAction SilentlyContinue)
    if ($running.Count -ne 0) {
        throw 'Origin.exe is running. Close the client before changing its logo.'
    }
}

function Set-FileAtomically {
    param(
        [string]$Source,
        [string]$Destination
    )
    $temporary = Join-Path ([IO.Path]::GetDirectoryName($Destination)) `
        ('.reborn-logo-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $replaced = $temporary + '.previous'
    try {
        [IO.File]::Copy($Source, $temporary, $false)
        if ((Get-Sha256Hex $temporary) -cne (Get-Sha256Hex $Source)) {
            throw "Temporary logo verification failed: $Destination"
        }
        [IO.File]::Replace($temporary, $Destination, $replaced)
    }
    finally {
        foreach ($path in @($temporary, $replaced)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force
            }
        }
    }
}

function New-VerifiedBackup {
    param([object[]]$Assets)
    [IO.Directory]::CreateDirectory($BackupRoot) | Out-Null
    $name = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $directory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) $name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $records = @()
    foreach ($index in 0..($Assets.Count - 1)) {
        $relative = $relativePaths[$index]
        $destination = Get-ContainedPath $directory $relative
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($Assets[$index].Path, $destination, $false)
        $backupHash = Get-Sha256Hex $destination
        if ($backupHash -cne $Assets[$index].Sha256) {
            throw "Backup verification failed: $relative"
        }
        $records += [ordered]@{
            relativePath = $relative
            sha256 = $backupHash
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        createdAtUtc = [DateTime]::UtcNow.ToString('O')
        clientRoot = [IO.Path]::GetFullPath($ClientRoot)
        pristineSha256 = $ExpectedPristineSha256
        targetSha256 = $ExpectedTargetSha256
        assets = $records
    }
    $manifestPath = Join-Path $directory 'manifest.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
    return $directory
}

function Restore-Backup {
    param([string]$Directory)
    $manifestPath = Join-Path $Directory 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Backup manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.assets.Count -ne 2) {
        throw 'Backup manifest is not a supported login-logo backup.'
    }
    $backupByPath = @{}
    foreach ($record in $manifest.assets) {
        $backupByPath[[string]$record.relativePath] = $record
    }
    foreach ($relative in $relativePaths) {
        if (-not $backupByPath.ContainsKey($relative)) {
            throw "Backup manifest does not contain $relative."
        }
        $record = $backupByPath[$relative]
        $source = Get-ContainedPath $Directory $relative
        Assert-LoginLogoTga $source 'Backed-up login logo'
        if ((Get-Sha256Hex $source) -cne [string]$record.sha256) {
            throw "Backed-up login logo failed its hash: $relative"
        }
    }
    foreach ($relative in $relativePaths) {
        $source = Get-ContainedPath $Directory $relative
        $destination = Get-ContainedPath $ClientRoot $relative
        Set-FileAtomically $source $destination
    }
}

$targetPath = [IO.Path]::GetFullPath($AssetPath)
Assert-LoginLogoTga $targetPath 'Canonical Reborn login logo'
if ((Get-Sha256Hex $targetPath) -cne $ExpectedTargetSha256) {
    throw 'Canonical Reborn login logo failed its reviewed SHA-256 guard.'
}

$assets = Get-ClientAssets
$state = Get-OverallState $assets
if ($Mode -ceq 'Status') {
    [pscustomobject]@{
        State = $state
        Assets = $assets
        TargetAsset = $targetPath
        BackupDirectory = ''
    }
    exit 0
}

if (-not $AllowMutation) {
    throw "$Mode requires -AllowMutation."
}
Assert-ClientClosed

if ($Mode -ceq 'Apply') {
    if ($state -ceq 'Patched') {
        [pscustomobject]@{
            State = $state
            Assets = $assets
            TargetAsset = $targetPath
            BackupDirectory = ''
        }
        exit 0
    }
    if ($state -cne 'Pristine') {
        throw "Apply requires two pristine locale assets; state is $state."
    }
    $backup = New-VerifiedBackup $assets
    try {
        foreach ($asset in $assets) {
            Set-FileAtomically $targetPath $asset.Path
        }
        $resultAssets = Get-ClientAssets
        $resultState = Get-OverallState $resultAssets
        if ($resultState -cne 'Patched') {
            throw 'Installed login-logo verification did not reach Patched.'
        }
    }
    catch {
        Restore-Backup $backup
        throw
    }
    [pscustomobject]@{
        State = $resultState
        Assets = $resultAssets
        TargetAsset = $targetPath
        BackupDirectory = $backup
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($RollbackFrom)) {
    throw 'Rollback requires -RollbackFrom.'
}
Restore-Backup ([IO.Path]::GetFullPath($RollbackFrom))
$resultAssets = Get-ClientAssets
[pscustomobject]@{
    State = Get-OverallState $resultAssets
    Assets = $resultAssets
    TargetAsset = $targetPath
    BackupDirectory = [IO.Path]::GetFullPath($RollbackFrom)
}
