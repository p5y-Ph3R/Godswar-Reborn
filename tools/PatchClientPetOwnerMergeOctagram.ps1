[CmdletBinding()]
param(
    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$BackupRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Binary.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Xml.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Asset.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Transaction.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Upgrade.ps1')
. (Join-Path $helperRoot 'PetOwnerMergeOctagram.Patch.ps1')

$arguments = @{
    ClientRoot = $ClientRoot
    Mode = $Mode
    BackupRoot = $BackupRoot
    RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
Invoke-PetOwnerMergeOctagramPatch @arguments
