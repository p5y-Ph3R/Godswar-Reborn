Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

function New-RebornDevelopmentHostsArtifactSecurity {
    param([switch]$File)

    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
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
    $security.SetOwner($administrators)
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
            $reader,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    return $security
}

function Assert-RebornDevelopmentHostsArtifactReadAcl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$File
    )

    $resolved = if ($File) {
        Assert-RebornSingleLinkRegularFilePath `
            $Path 'development hosts protected artifact'
    } else {
        Assert-RebornDirectoryPath `
            $Path 'development hosts protected directory'
    }
    $acl = Get-Acl -LiteralPath $resolved
    $owner = $acl.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544') -or
        -not $acl.AreAccessRulesProtected) {
        throw 'Development hosts artifact owner or inheritance is unsafe.'
    }

    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
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
            throw 'Development hosts artifact ACL has an unexpected rule.'
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
            throw 'Development hosts artifact ACL is missing an issued SID.'
        }
    }
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        if (($rightsBySid[$sid] -band
                [Security.AccessControl.FileSystemRights]::FullControl) -ne
            [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Development hosts trusted principal lacks full control.'
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
            'Development hosts artifact does not grant the exact current ' +
            'SID read-only access.')
    }
    return [pscustomobject]@{
        Path = $resolved
        ReaderSid = $reader
        ReaderRights = $readerRights
        IsFile = [bool]$File
    }
}

function Protect-RebornDevelopmentHostsArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$File
    )

    $resolved = if ($File) {
        Assert-RebornSingleLinkRegularFilePath `
            $Path 'development hosts artifact before ACL protection'
    } else {
        Assert-RebornDirectoryPath `
            $Path 'development hosts directory before ACL protection'
    }
    Set-Acl -LiteralPath $resolved -AclObject (
        New-RebornDevelopmentHostsArtifactSecurity -File:$File)
    Assert-RebornDevelopmentHostsArtifactReadAcl `
        $resolved -File:$File
}

Export-ModuleMember -Function @(
    'New-RebornDevelopmentHostsArtifactSecurity',
    'Assert-RebornDevelopmentHostsArtifactReadAcl',
    'Protect-RebornDevelopmentHostsArtifact'
)
