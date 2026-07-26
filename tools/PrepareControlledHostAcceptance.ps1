[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$DatabaseBackupPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedDatabaseBackupSha256,

    [string]$ClientRoot = 'C:\RebornNetworkAcceptanceClient',

    [string]$EvidenceDirectory = (
        Join-Path $PSScriptRoot `
            '..\artifacts\controlled-host-acceptance\client-acl'),

    [switch]$AllowPreparation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AllowPreparation) {
    throw 'Preparation requires explicit -AllowPreparation.'
}
if (-not $PSCmdlet.ShouldProcess(
        'the pinned database dump and disposable acceptance client',
        'Protect the backup and harden the client ACL')) {
    return
}

$backupTool =
    Join-Path $PSScriptRoot 'ProtectControlledHostDatabaseBackup.ps1'
$clientTool =
    Join-Path $PSScriptRoot 'PrepareControlledHostClient.ps1'

$backup = & $backupTool `
    -Mode Apply `
    -SourcePath $DatabaseBackupPath `
    -ExpectedSha256 $ExpectedDatabaseBackupSha256 `
    -AllowBackupWrite `
    -Confirm:$false

$client = & $clientTool `
    -Mode Apply `
    -ClientRoot $ClientRoot `
    -EvidenceDirectory $EvidenceDirectory `
    -AllowAclWrite `
    -Confirm:$false

$clientReceipt = $null
if ($null -ne $client.PSObject.Properties['ReceiptPath']) {
    $clientReceipt = $client.ReceiptPath
}

[pscustomobject]@{
    Result = 'Prepared'
    BackupResult = $backup.Result
    ProtectedBackupPath = $backup.ProtectedPath
    ClientResult = $client.Result
    ClientRoot = $client.ClientRoot
    ClientAclReceipt = $clientReceipt
    ClientInventoryReceiptPath = $client.InventoryReceiptPath
    ClientInventoryReceiptSha256 = $client.InventoryReceiptSha256
    ClientInventorySetSha256 = $client.InventorySetSha256
    RebootRequiredBeforeActivation = $true
}
