Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkFileHandleSafety.psm1'
)

function Test-RebornSecureNetworkLockAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-RebornSecureNetworkLockSecurity {
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

function Assert-RebornSecureNetworkLockReadAcl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$File
    )

    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544') -or
        -not $acl.AreAccessRulesProtected) {
        throw 'Secure-network lock ACL owner or inheritance is unsafe.'
    }
    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $trusted = @('S-1-5-18', 'S-1-5-32-544')
    $expected = @($trusted + $reader) | Select-Object -Unique
    $rightsBySid = @{}
    $rules = $acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if ($rule.AccessControlType -ne
            [Security.AccessControl.AccessControlType]::Allow) {
            throw 'Secure-network lock ACL contains a deny rule.'
        }
        if ($sid -notin $expected) {
            throw 'Secure-network lock ACL contains an unexpected rule.'
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
            throw 'Secure-network lock ACL is missing an issued SID.'
        }
    }
    foreach ($sid in $trusted) {
        if (($rightsBySid[$sid] -band
                [Security.AccessControl.FileSystemRights]::FullControl) -ne
            [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Secure-network lock trusted SID lacks full control.'
        }
    }
    $mutationRights =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    $readerRights = $rightsBySid[$reader]
    if (($readerRights -band $mutationRights) -ne 0 -or
        ($readerRights -band
            [Security.AccessControl.FileSystemRights]::Read) -ne
            [Security.AccessControl.FileSystemRights]::Read) {
        throw (
            'Secure-network lock does not grant the exact current SID ' +
            'read-only access.')
    }
    return [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($Path)
        ReaderSid = $reader
        ReaderRights = $readerRights
        IsFile = [bool]$File
    }
}

function Resolve-RebornSecureNetworkLockRoot {
    param(
        [string]$LockRoot,
        [switch]$AllowTestPath
    )

    $resolved = [IO.Path]::GetFullPath($LockRoot).TrimEnd('\')
    if ($AllowTestPath) {
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith(
                $temporary,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Test operation-lock root must remain under temp.'
        }
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            [IO.Directory]::CreateDirectory($resolved) | Out-Null
        }
        return Assert-RebornDirectoryPath `
            $resolved 'test operation-lock root'
    }

    $issued = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData 'RebornSecureNetworkLocks')
    ).TrimEnd('\')
    if (-not $resolved.Equals(
            $issued,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production operation-lock root must equal its issued path.'
    }
    if (-not (Test-Path -LiteralPath $issued -PathType Container)) {
        if (-not (Test-RebornSecureNetworkLockAdministrator)) {
            throw (
                'Only an elevated mutation tool may initialize the ' +
                'secure-network operation-lock root.')
        }
        [IO.Directory]::CreateDirectory(
            $issued,
            (New-RebornSecureNetworkLockSecurity)) | Out-Null
    }
    return Assert-RebornProtectedDirectoryPath `
        $issued 'secure-network operation-lock root' `
        -ProtectContents -RequireProtectedAcl
}

function Enter-RebornSecureNetworkOperationLock {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
        [string]$Name,
        [string]$LockRoot = (
            Join-Path $env:ProgramData 'RebornSecureNetworkLocks'),
        [switch]$AllowTestPath
    )

    $root = Resolve-RebornSecureNetworkLockRoot `
        $LockRoot -AllowTestPath:$AllowTestPath
    $path = Join-Path $root "$Name.lock"
    if (Test-Path -LiteralPath $path) {
        Assert-RebornSingleLinkRegularFilePath `
            $path 'secure-network operation-lock file' | Out-Null
    }
    try {
        $stream = [IO.FileStream]::new(
            $path,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Secure-network operation lock is already held: $Name"
    }
    try {
        Assert-RebornSingleLinkFileStream `
            $stream 'secure-network operation-lock file'
        if (-not $AllowTestPath) {
            if (-not (Test-RebornSecureNetworkLockAdministrator)) {
                throw (
                    'A production secure-network mutation lock requires ' +
                    'an elevated process.')
            }
            Set-Acl -LiteralPath $root -AclObject (
                New-RebornSecureNetworkLockSecurity)
            Set-Acl -LiteralPath $path -AclObject (
                New-RebornSecureNetworkLockSecurity -File)
            Assert-RebornSecureNetworkLockReadAcl $root | Out-Null
            Assert-RebornSecureNetworkLockReadAcl $path -File | Out-Null
        }
        $record = [Text.UTF8Encoding]::new($false).GetBytes(
            "pid=$PID`nstartedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))`n")
        try {
            $stream.SetLength(0)
            $stream.Write($record, 0, $record.Length)
            $stream.Flush($true)
        }
        finally {
            [Array]::Clear($record, 0, $record.Length)
        }
        return [pscustomobject]@{
            Name = $Name
            Path = $path
            Stream = $stream
        }
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Enter-RebornSecureNetworkOperationReadLease {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
        [string]$Name,
        [string]$LockRoot = (
            Join-Path $env:ProgramData 'RebornSecureNetworkLocks'),
        [switch]$AllowTestPath
    )

    $root = if ($AllowTestPath) {
        $candidate = [IO.Path]::GetFullPath($LockRoot).TrimEnd('\')
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $candidate.StartsWith(
                $temporary,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $candidate -PathType Container)) {
            throw (
                'Test read lease requires an existing lock root under temp; ' +
                'it will not create one.')
        }
        Assert-RebornDirectoryPath `
            $candidate 'test operation-lock root'
    } else {
        $issued = [IO.Path]::GetFullPath(
            (Join-Path $env:ProgramData 'RebornSecureNetworkLocks')
        ).TrimEnd('\')
        $candidate = [IO.Path]::GetFullPath($LockRoot).TrimEnd('\')
        if (-not $candidate.Equals(
                $issued,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Production operation-lock root must equal its issued path.'
        }
        Assert-RebornDirectoryPath `
            $candidate 'secure-network operation-lock root' | Out-Null
        Assert-RebornSecureNetworkLockReadAcl $candidate | Out-Null
        $candidate
    }
    $path = Join-Path $root "$Name.lock"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw (
            'Secure-network read lease requires an existing issued lock ' +
            'file; it will not create one.')
    }
    Assert-RebornSingleLinkRegularFilePath `
        $path 'secure-network operation-lock file' | Out-Null
    if (-not $AllowTestPath) {
        Assert-RebornSecureNetworkLockReadAcl $path -File | Out-Null
    }
    try {
        $stream = [IO.FileStream]::new(
            $path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
    }
    catch [IO.IOException] {
        throw "Secure-network operation lock is already held: $Name"
    }
    return [pscustomobject]@{
        Name = $Name
        Path = $path
        Stream = $stream
        ReadOnly = $true
    }
}

function Exit-RebornSecureNetworkOperationLock {
    param([Parameter(Mandatory)][object]$Lock)

    if ($null -ne $Lock.Stream) {
        $Lock.Stream.Dispose()
    }
}

Export-ModuleMember -Function @(
    'New-RebornSecureNetworkLockSecurity',
    'Assert-RebornSecureNetworkLockReadAcl',
    'Enter-RebornSecureNetworkOperationLock',
    'Enter-RebornSecureNetworkOperationReadLease',
    'Exit-RebornSecureNetworkOperationLock'
)
