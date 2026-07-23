param(
    [string]$ClientExe = 'C:\Godswar Origin\Origin.exe',
    [ValidateSet('Apply', 'Revert')]
    [string]$Mode = 'Apply',
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'AvatarPreviewGuard.Binary.ps1')
. (Join-Path $helperRoot 'AvatarPreviewGuard.Patch.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Invoke-AvatarPreviewGuardPatch -ClientExe $ClientExe -Mode $Mode `
    -BackupRoot $BackupRoot -RepositoryRoot $repositoryRoot
