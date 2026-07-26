Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
$orderedModules = @(
    'ControlledHostClientActivation.psm1'
    'ControlledHostPrivacyEvidence.psm1'
    'ControlledHostServerRuntime.psm1'
    'ControlledHostServerValidation.psm1'
    'DevelopmentNetworkHostsRuntimeGate.psm1'
    'ControlledHostClientRootLease.psm1'
    'ControlledHostManagedRelease.psm1'
    'ControlledHostProcessEnvironment.psm1'
    'ControlledHostRunnerIdentity.psm1'
    'ControlledHostRuntimeLock.psm1'
    'SecureNetworkPathSafety.psm1'
)
foreach ($moduleName in $orderedModules) {
    Import-Module (Join-Path $moduleRoot $moduleName) -Force
}

$requiredCommands = @(
    'Assert-RebornControlledHostClientActivation'
    'Assert-RebornControlledHostClientRootLease'
    'Assert-RebornControlledHostDirectoryLease'
    'Assert-RebornControlledHostNoUnreviewedGodswarEnvironment'
    'Assert-RebornControlledHostPrivacyEvidence'
    'Assert-RebornControlledHostRunnerIdentity'
    'Assert-RebornControlledHostRuntime'
    'Assert-RebornControlledHostSafeProcessEnvironment'
    'Assert-RebornControlledHostUnsetEnvironmentNames'
    'Assert-RebornDevelopmentHostsInstalledExact'
    'Assert-RebornDirectoryPath'
    'Assert-RebornSingleLinkRegularFilePath'
    'Enter-RebornControlledHostClientRootLease'
    'Enter-RebornControlledHostDirectoryLease'
    'Enter-RebornControlledHostRuntimeLock'
    'Enter-RebornDevelopmentHostsRuntimeLease'
    'Exit-RebornControlledHostClientRootLease'
    'Exit-RebornControlledHostDirectoryLease'
    'Exit-RebornControlledHostRuntimeLock'
    'Exit-RebornDevelopmentHostsRuntimeLock'
    'Get-RebornControlledHostDiagnosticsDisabledEnvironment'
    'Get-RebornControlledHostManagedReleaseSet'
    'Get-RebornControlledHostRuntimeRoot'
    'New-RebornControlledHostEvidencePath'
    'Protect-RebornControlledHostPrivacyEvidence'
    'Read-RebornAcceptanceDatabaseScope'
    'Test-RebornControlledHostCertificate'
    'Test-RebornControlledHostServerOptions'
)
foreach ($commandName in $requiredCommands) {
    if ($null -eq (Get-Command $commandName `
            -CommandType Function -ErrorAction SilentlyContinue)) {
        throw (
            'Controlled-host launcher dependency is not in module scope: ' +
            $commandName)
    }
}

Export-ModuleMember -Function $requiredCommands
