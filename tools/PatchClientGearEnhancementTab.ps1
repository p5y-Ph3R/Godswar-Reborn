param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [ValidateSet('Verify', 'Apply', 'Revert')]
    [string]$Mode = 'Verify',
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'GearEnhancementTab.Binary.ps1')
. (Join-Path $helperRoot 'GearEnhancementTab.Localization.ps1')
. (Join-Path $helperRoot 'GearEnhancementTab.Patch.ps1')

Invoke-GearEnhancementTabPatch -ClientRoot $ClientRoot -Mode $Mode `
    -BackupRoot $BackupRoot
