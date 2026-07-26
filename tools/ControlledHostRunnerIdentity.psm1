Set-StrictMode -Version Latest

function Assert-RebornControlledHostRunnerIdentityState {
    param(
        [Parameter(Mandatory)][bool]$IsElevated,
        [Parameter(Mandatory)][bool]$IsSystem,
        [Parameter(Mandatory)]
        [ValidatePattern('^S-\d(-\d+)+$')]
        [string]$UserSid
    )

    if ($IsElevated -or $IsSystem) {
        throw (
            'The controlled-host secure server must run as the issued ' +
            'non-elevated user, never as Administrator or SYSTEM.')
    }
    return $UserSid
}

function Assert-RebornControlledHostRunnerIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return Assert-RebornControlledHostRunnerIdentityState `
        ($principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) `
        $identity.IsSystem `
        $identity.User.Value
}

Export-ModuleMember -Function @(
    'Assert-RebornControlledHostRunnerIdentityState',
    'Assert-RebornControlledHostRunnerIdentity'
)
