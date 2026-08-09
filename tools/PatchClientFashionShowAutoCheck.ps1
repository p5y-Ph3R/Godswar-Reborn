[CmdletBinding()]
param(
    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',

    [ValidateSet('Apply', 'Revert', 'Status')]
    [string]$Mode = 'Status',

    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'FashionShowAutoCheck.Patch.ps1')

$arguments = @{
    ClientExe = $ClientExe
    Mode = $Mode
    BackupRoot = $BackupRoot
    RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
Invoke-FashionShowAutoCheckPatch @arguments
