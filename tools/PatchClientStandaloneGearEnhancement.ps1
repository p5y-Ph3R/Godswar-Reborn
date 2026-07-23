param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [ValidateSet('Verify', 'Apply', 'Revert')]
    [string]$Mode = 'Verify',
    [string]$BackupRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$helperRoot = Join-Path $PSScriptRoot 'client_patch_helpers'
. (Join-Path $helperRoot 'StandaloneGearEnhancement.Core.ps1')
. (Join-Path $helperRoot 'StandaloneGearEnhancement.Enhancer.ps1')
. (Join-Path $helperRoot 'StandaloneGearEnhancement.Patch.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Invoke-StandaloneGearEnhancementPatch -ClientRoot $ClientRoot -Mode $Mode `
    -BackupRoot $BackupRoot -RepositoryRoot $repositoryRoot
