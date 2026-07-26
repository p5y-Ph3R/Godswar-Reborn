[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineProfileResultPath,
    [Parameter(Mandatory)][string]$FallbackProfileResultPath,
    [Parameter(Mandatory)][string]$SoakProfileResultPath,

    [Parameter(Mandatory)][switch]$AttestAlternatingAccounts,
    [Parameter(Mandatory)][switch]$AttestPreviewReadiness,
    [Parameter(Mandatory)][switch]$AttestUnmountedMovement,
    [Parameter(Mandatory)][switch]$AttestMountedMovement,
    [Parameter(Mandatory)][switch]$AttestWorldGenerationChanges,
    [Parameter(Mandatory)][switch]$AttestDeathAndRevive,
    [Parameter(Mandatory)][switch]$AttestSessionLifecycle,
    [Parameter(Mandatory)][switch]$AttestFallbackCorrection,
    [Parameter(Mandatory)][switch]$AttestSoakStability,
    [Parameter(Mandatory)][switch]$AttestDatabaseMutationReviewed,

    [Parameter(Mandatory)]
    [ValidateSet('Passed', 'Unavailable')]
    [string]$ViewerParity,

    [switch]$AllowCompletion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'Phase4SecureDockerClientCampaign.psm1',
    'Phase4SecureDockerClientRuntime.psm1',
    'Phase4CompletionValidation.psm1',
    'Phase4CompletionReceipt.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}
# Composite modules reload this dependency privately. Keep the pin commands
# explicitly available to this entry point after all composite imports.
Import-Module (
    Join-Path $PSScriptRoot 'Phase4SecureDockerClientCampaign.psm1'
) -Force

function Assert-RebornPhase4CompletionAuthority {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($null -eq $identity.User -or
        $identity.User.Value -ceq 'S-1-5-18' -or
        -not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Phase 4 completion requires an elevated issued-user token.'
    }
}

if (-not $AllowCompletion) {
    throw 'Explicit -AllowCompletion is required.'
}
Assert-RebornPhase4CompletionAuthority

$pins = Get-RebornPhase4SecureDockerPins
Assert-RebornPhase4PinnedInputs $pins | Out-Null
$statusOutput = @(
    & (Join-Path $PSScriptRoot 'ManagePhase4SecureDockerClient.ps1') `
        -Mode Status)
$status = @(
    $statusOutput |
        Where-Object {
            $null -ne $_.PSObject.Properties['State']
        })
if ($status.Count -ne 1) {
    throw 'Phase 4 final client status was not unique.'
}
$docker = Assert-RebornPhase4SecureDockerRuntime $pins
$manual = New-RebornPhase4ManualAttestation `
    -AlternatingAccounts:$AttestAlternatingAccounts `
    -PreviewReadiness:$AttestPreviewReadiness `
    -UnmountedMovement:$AttestUnmountedMovement `
    -MountedMovement:$AttestMountedMovement `
    -WorldGenerationChanges:$AttestWorldGenerationChanges `
    -DeathAndRevive:$AttestDeathAndRevive `
    -SessionLifecycle:$AttestSessionLifecycle `
    -FallbackCorrection:$AttestFallbackCorrection `
    -SoakStability:$AttestSoakStability `
    -DatabaseMutationReviewed:$AttestDatabaseMutationReviewed `
    -ViewerParity $ViewerParity

$completion = Write-RebornPhase4CompletionReceipt `
    -ProfileResultPaths @(
        $BaselineProfileResultPath,
        $FallbackProfileResultPath,
        $SoakProfileResultPath) `
    -ManualAttestation $manual `
    -FinalStatus $status[0] `
    -DockerStatus $docker `
    -Pins $pins

[pscustomobject][ordered]@{
    Result = 'Phase4Accepted'
    CampaignId = [string]$completion.Record.campaign.id
    IssuedUserSid = [string]$completion.Record.campaign.issuedUserSid
    CompletionPath = $completion.Path
    CompletionChecksumPath = $completion.ChecksumPath
    CompletionSha256 = $completion.Sha256
    Profiles = @($completion.Record.profiles.profile) -join ','
    ViewerParity = [string]$completion.Record.manualAttestation.viewerParity
    FinalRestore = [string]$completion.Record.finalState.restore
    DockerState = [string]$completion.Record.finalState.dockerState
}
