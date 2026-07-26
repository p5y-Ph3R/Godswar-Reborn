Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

function New-RebornControlledHostReadOnlyArtifactSecurity {
    param(
        [switch]$File,
        [Security.Principal.SecurityIdentifier]$ReaderSid = (
            [Security.Principal.WindowsIdentity]::GetCurrent().User),
        [Security.Principal.SecurityIdentifier]$OwnerSid = (
            [Security.Principal.SecurityIdentifier]::new(
                'S-1-5-32-544'))
    )

    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $inheritance = if ($File) {
        [Security.AccessControl.InheritanceFlags]::None
    } else {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    $security = if ($File) {
        [Security.AccessControl.FileSecurity]::new()
    } else {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($OwnerSid)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $ReaderSid,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    return $security
}

function Assert-RebornControlledHostReadOnlyArtifactAcl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$File,
        [switch]$AllowCurrentUserOwner
    )

    $resolved = if ($File) {
        Assert-RebornSingleLinkRegularFilePath `
            $Path 'controlled-host read-only artifact'
    } else {
        Assert-RebornDirectoryPath `
            $Path 'controlled-host read-only directory'
    }
    $acl = Get-Acl -LiteralPath $resolved
    $owner = $acl.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allowedOwners = @('S-1-5-18', 'S-1-5-32-544')
    if ($AllowCurrentUserOwner) {
        $allowedOwners += $reader
    }
    if ($owner -notin $allowedOwners -or
        -not $acl.AreAccessRulesProtected) {
        throw 'Controlled-host artifact owner or inheritance is unsafe.'
    }

    $expected = @(
        'S-1-5-18',
        'S-1-5-32-544',
        $reader
    ) | Select-Object -Unique
    $rightsBySid = @{}
    $rules = $acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if ($rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $sid -notin $expected) {
            throw 'Controlled-host artifact ACL has an unexpected rule.'
        }
        if (-not $rightsBySid.ContainsKey($sid)) {
            $rightsBySid[$sid] =
                [Security.AccessControl.FileSystemRights]0
        }
        $rightsBySid[$sid] =
            $rightsBySid[$sid] -bor $rule.FileSystemRights
    }
    foreach ($sid in $expected) {
        if (-not $rightsBySid.ContainsKey($sid)) {
            throw 'Controlled-host artifact ACL is missing an issued SID.'
        }
    }
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        if (($rightsBySid[$sid] -band
                [Security.AccessControl.FileSystemRights]::FullControl) -ne
            [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Controlled-host trusted principal lacks full control.'
        }
    }
    $mutation =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    $readerRights = $rightsBySid[$reader]
    if (($readerRights -band $mutation) -ne 0 -or
        ($readerRights -band
            [Security.AccessControl.FileSystemRights]::Read) -ne
            [Security.AccessControl.FileSystemRights]::Read) {
        throw (
            'Controlled-host artifact does not grant the exact current ' +
            'SID read-only access.')
    }
    [pscustomobject]@{
        Path = $resolved
        ReaderSid = $reader
        ReaderRights = $readerRights
        IsFile = [bool]$File
    }
}

function Protect-RebornControlledHostReadOnlyArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$File,
        [scriptblock]$SetAclAction,
        [switch]$AllowTestHook
    )

    if ($null -ne $SetAclAction -and -not $AllowTestHook) {
        throw 'Custom ACL mutation is restricted to explicit test hooks.'
    }
    $resolved = if ($File) {
        Assert-RebornSingleLinkRegularFilePath `
            $Path 'controlled-host ACL mutation target'
    } else {
        Assert-RebornDirectoryPath `
            $Path 'controlled-host ACL mutation target'
    }
    $security =
        New-RebornControlledHostReadOnlyArtifactSecurity -File:$File
    if ($null -eq $SetAclAction) {
        Set-Acl -LiteralPath $resolved -AclObject $security
    } else {
        & $SetAclAction $resolved $security
    }
    Assert-RebornControlledHostReadOnlyArtifactAcl `
        $resolved -File:$File
}

Export-ModuleMember -Function @(
    'New-RebornControlledHostReadOnlyArtifactSecurity',
    'Assert-RebornControlledHostReadOnlyArtifactAcl',
    'Protect-RebornControlledHostReadOnlyArtifact'
)
