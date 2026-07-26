Set-StrictMode -Version Latest

$script:ActivationRegistrySubKey = 'SOFTWARE\Reborn\NetworkManifest'
$script:PrivilegedRegistrySids = @(
    'S-1-5-18',
    'S-1-5-32-544',
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
)

function Get-RebornRegistryRuleSid {
    param([Parameter(Mandatory)][object]$Rule)

    if ($null -ne $Rule.PSObject.Properties['IdentitySid']) {
        return [string]$Rule.IdentitySid
    }
    return (
        $Rule.IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier])
    ).Value
}

function Test-RebornActivationRegistryAclPolicy {
    param(
        [Parameter(Mandatory)][string]$OwnerSid,
        [Parameter(Mandatory)][bool]$AccessRulesProtected,
        [Parameter(Mandatory)][object[]]$Rules
    )

    if ($script:PrivilegedRegistrySids -cnotcontains $OwnerSid) {
        return [pscustomobject]@{
            Valid = $false
            Reason = 'activation registry owner is not privileged'
        }
    }
    if (-not $AccessRulesProtected) {
        return [pscustomobject]@{
            Valid = $false
            Reason = 'activation registry DACL inheritance is enabled'
        }
    }

    $mutationRights =
        [Security.AccessControl.RegistryRights]::SetValue -bor
        [Security.AccessControl.RegistryRights]::CreateSubKey -bor
        [Security.AccessControl.RegistryRights]::Delete -bor
        [Security.AccessControl.RegistryRights]::ChangePermissions -bor
        [Security.AccessControl.RegistryRights]::TakeOwnership
    foreach ($rule in $Rules) {
        $type = if (
            $null -ne $rule.PSObject.Properties['Type']
        ) {
            [Security.AccessControl.AccessControlType]$rule.Type
        } else {
            [Security.AccessControl.AccessControlType]$rule.AccessControlType
        }
        $rights = if (
            $null -ne $rule.PSObject.Properties['Rights']
        ) {
            [Security.AccessControl.RegistryRights]$rule.Rights
        } else {
            [Security.AccessControl.RegistryRights]$rule.RegistryRights
        }
        if (
            $type -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            ($rights -band $mutationRights) -ne 0 -and
            $script:PrivilegedRegistrySids -cnotcontains
                (Get-RebornRegistryRuleSid $rule)
        ) {
            return [pscustomobject]@{
                Valid = $false
                Reason = (
                    'nonprivileged principal can mutate the activation ' +
                    'registry key')
            }
        }
    }
    return [pscustomobject]@{ Valid = $true; Reason = $null }
}

function Assert-RebornProtectedActivationRegistryAcl {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($script:ActivationRegistrySubKey, $false)
    }
    finally {
        $base.Dispose()
    }
    if ($null -eq $key) {
        throw 'The protected HKLM activation key does not exist.'
    }
    try {
        $sections =
            [Security.AccessControl.AccessControlSections]::Owner -bor
            [Security.AccessControl.AccessControlSections]::Access
        $security = $key.GetAccessControl($sections)
        $owner = $security.GetOwner(
            [Security.Principal.SecurityIdentifier]).Value
        $rules = @($security.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]))
        $policy = Test-RebornActivationRegistryAclPolicy `
            $owner $security.AreAccessRulesProtected $rules
        if (-not $policy.Valid) {
            throw "Unsafe HKLM activation ACL: $($policy.Reason)."
        }
    }
    finally {
        $key.Dispose()
    }
}

Export-ModuleMember -Function @(
    'Assert-RebornProtectedActivationRegistryAcl'
)
