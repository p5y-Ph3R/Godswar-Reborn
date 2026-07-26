[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSha256,

    [string]$ProtectedRoot = (
        Join-Path (
            [Environment]::GetFolderPath('CommonApplicationData')
        ) `
            'RebornSecureNetworkBackups\controlled-host-database'),

    [switch]$AllowBackupWrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleFiles.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostDatabaseBackup.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostReadOnlyArtifactAcl.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Source {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedHash
    )

    $allowedRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot `
            '..\artifacts\controlled-host-acceptance')).TrimEnd('\')
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith(
            $allowedRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotmatch
            '^godswar-\d{8}-\d{6}\.dump$') {
        throw (
            'Database backup source must be an issued controlled-host dump ' +
            "under $allowedRoot.")
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Database backup source not found: $resolved"
    }
    Assert-RebornSingleLinkRegularFilePath `
        $resolved 'controlled-host database backup source' | Out-Null
    if ((Get-Sha256 $resolved) -cne $ExpectedHash) {
        throw 'Database backup source SHA-256 does not match.'
    }
    return $resolved
}

function Initialize-ReadOnlyDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $parent = Split-Path -Parent $resolved
    Assert-RebornDirectoryPath `
        $parent 'database backup protected parent' | Out-Null
    if (-not (Test-Path -LiteralPath $resolved)) {
        [IO.Directory]::CreateDirectory(
            $resolved,
            (New-RebornControlledHostReadOnlyArtifactSecurity)) |
            Out-Null
    }
    Assert-RebornDirectoryPath `
        $resolved 'database backup protected directory' | Out-Null
    Protect-RebornControlledHostReadOnlyArtifact $resolved |
        Out-Null
    Assert-RebornControlledHostReadOnlyArtifactAcl $resolved |
        Out-Null
}

$expected = $ExpectedSha256.ToUpperInvariant()
$source = Assert-Source $SourcePath $expected
$protected = [IO.Path]::GetFullPath($ProtectedRoot).TrimEnd('\')
$expectedProtected = [IO.Path]::GetFullPath(
    (Join-Path (
        [Environment]::GetFolderPath('CommonApplicationData')
    ) `
        'RebornSecureNetworkBackups\controlled-host-database')).TrimEnd('\')
if (-not $protected.Equals(
        $expectedProtected,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "ProtectedRoot is restricted to $expectedProtected."
}
$target = Join-Path $protected ([IO.Path]::GetFileName($source))
$receipt = Join-Path $protected (
    "database-backup-$([IO.Path]::GetFileNameWithoutExtension($source))-" +
    "$($expected.Substring(0, 16)).json")
$protectedState =
    Get-RebornControlledHostDatabaseBackupState `
        $target $receipt $expected
$state = [string]$protectedState.State

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        State = $state
        SourcePath = $source
        SourceSha256 = $expected
        ProtectedPath = $target
        ReceiptPath = $receipt
        Elevated = Test-IsAdministrator
    }
    return
}

if (-not $AllowBackupWrite) {
    throw 'Apply requires explicit -AllowBackupWrite.'
}
if (-not (Test-IsAdministrator)) {
    throw 'Database backup protection requires an elevated PowerShell process.'
}
if ($state -eq 'Conflict') {
    throw 'Protected database backup state conflicts with the pinned source.'
}
if ($state -eq 'Protected') {
    [pscustomobject]@{
        Result = 'AlreadyProtected'
        ProtectedPath = $target
        ReceiptPath = $receipt
    }
    return
}
if (-not $PSCmdlet.ShouldProcess(
        $target,
        'Copy the pinned database dump into protected local recovery storage')) {
    return
}

$commonRoot = Split-Path -Parent $protected
Initialize-ReadOnlyDirectory $commonRoot
Initialize-ReadOnlyDirectory $protected
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    Copy-RebornFileAtomic $source $target $expected
}
Protect-RebornControlledHostReadOnlyArtifact $target -File |
    Out-Null
if (-not (Test-Path -LiteralPath $receipt -PathType Leaf)) {
    $record = New-RebornControlledHostDatabaseBackupReceipt `
        $target $expected $source
    Write-RebornJsonAtomic $record $receipt
}
Protect-RebornControlledHostReadOnlyArtifact $receipt -File |
    Out-Null

$verified =
    Get-RebornControlledHostDatabaseBackupState `
        $target $receipt $expected
if ($verified.State -cne 'Protected') {
    throw (
        'Protected database backup verification failed: ' +
        [string]$verified.State)
}
[pscustomobject]@{
    Result = 'Protected'
    ProtectedPath = $target
    ReceiptPath = $receipt
    Sha256 = $expected
}
