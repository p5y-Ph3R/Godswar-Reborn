Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostClientInventoryCore.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

function New-RebornControlledHostWritableOutputFileSecurity {
    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $currentUser =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $none = [Security.AccessControl.InheritanceFlags]::None
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $none,
                $propagation,
                $allow))
    }
    $fileRights =
        [Security.AccessControl.FileSystemRights]::Read -bor
        [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Synchronize
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $currentUser,
            $fileRights,
            $none,
            $propagation,
            $allow))
    return $security
}

function Assert-RebornControlledHostWritableOutputFileSecurity {
    param(
        [Parameter(Mandatory)]
        [Security.AccessControl.FileSecurity]$Security,
        [Security.Principal.SecurityIdentifier]$CurrentUser = (
            [Security.Principal.WindowsIdentity]::GetCurrent().User)
    )

    $administrators = 'S-1-5-32-544'
    $owner = $Security.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    if ($owner -cne $administrators -or
        -not $Security.AreAccessRulesProtected) {
        throw 'Exact writable client output ACL is not protected.'
    }

    $writeRights =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes
    $requiredRights =
        [Security.AccessControl.FileSystemRights]::Read -bor
        [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Synchronize
    $forbiddenRights =
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $currentRights = [Security.AccessControl.FileSystemRights]0
    $rules = @($Security.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if ($rule.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Deny) {
            if ($sid -ceq $CurrentUser.Value -and
                ($rule.FileSystemRights -band $requiredRights) -ne 0) {
                throw (
                    'Exact writable client output denies required issued-user ' +
                    'access.')
            }
            continue
        }
        if ($sid -ceq $CurrentUser.Value) {
            $currentRights = $currentRights -bor $rule.FileSystemRights
            continue
        }
        if ($sid -notin @('S-1-5-18', $administrators) -and
            ($rule.FileSystemRights -band $writeRights) -ne 0) {
            throw (
                'Exact writable client output grants another principal ' +
                'write access.')
        }
    }
    if (($currentRights -band $requiredRights) -ne $requiredRights -or
        ($currentRights -band $forbiddenRights) -ne 0) {
        throw (
            'Exact writable client output lacks bounded current-user ' +
            'read/write access.')
    }
    return $true
}

function Resolve-RebornControlledHostWritableOutputFile {
    param([Parameter(Mandatory)][string]$ClientRoot)

    $root = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    ) 'controlled-host mutable-output root'
    $relativePaths =
        @(Get-RebornControlledHostWritableOutputFileRelativePaths)
    if ($relativePaths.Count -ne 1 -or
        [string]$relativePaths[0] -cne 'patcher\patcher.log') {
        throw 'Exact writable client output policy is not the reviewed file.'
    }
    $path = Assert-RebornSingleLinkRegularFilePath `
        (Join-Path $root $relativePaths[0]) `
        'exact writable client output'
    $item = Get-Item -LiteralPath $path -Force
    if ([Int64]$item.Length -gt
        (Get-RebornControlledHostMaximumWritableOutputFileBytes)) {
        throw 'Exact writable client output exceeds its bounded file size.'
    }
    Assert-RebornControlledHostWritableFileIsDataOnly `
        $path $relativePaths[0]
    Assert-RebornProtectedDirectoryPath `
        (Split-Path -Parent $path) `
        'exact writable client output parent' `
        -ProtectContents `
        -ProtectChildren | Out-Null
    return $path
}

function Assert-RebornControlledHostWritableOutputFileAcls {
    param([Parameter(Mandatory)][string]$ClientRoot)

    $path = Resolve-RebornControlledHostWritableOutputFile $ClientRoot
    Assert-RebornControlledHostWritableOutputFileSecurity `
        (Get-Acl -LiteralPath $path) | Out-Null
    return $true
}

function Assert-RebornControlledHostWritableOutputFileInactive {
    param([Parameter(Mandatory)][string]$ClientRoot)

    $path = Resolve-RebornControlledHostWritableOutputFile $ClientRoot
    Assert-RebornProtectedRegularFilePath `
        $path 'inactive exact client output' | Out-Null
    return $true
}

function Get-RebornControlledHostWritableOutputFileState {
    param([Parameter(Mandatory)][string]$ClientRoot)

    try {
        Assert-RebornControlledHostWritableOutputFileAcls `
            $ClientRoot | Out-Null
        return 'Active'
    }
    catch {
        try {
            Assert-RebornControlledHostWritableOutputFileInactive `
                $ClientRoot | Out-Null
            return 'Inactive'
        }
        catch {
            return 'Invalid'
        }
    }
}

function Enable-RebornControlledHostWritableOutputFile {
    param([Parameter(Mandatory)][string]$ClientRoot)

    Assert-RebornControlledHostWritableOutputFileInactive `
        $ClientRoot | Out-Null
    $path = Resolve-RebornControlledHostWritableOutputFile $ClientRoot
    Set-Acl -LiteralPath $path -AclObject (
        New-RebornControlledHostWritableOutputFileSecurity)
    Assert-RebornControlledHostWritableOutputFileAcls `
        $ClientRoot | Out-Null
}

function Disable-RebornControlledHostWritableOutputFile {
    param([Parameter(Mandatory)][string]$ClientRoot)

    $path = Resolve-RebornControlledHostWritableOutputFile $ClientRoot
    & icacls.exe $path '/reset' '/L' '/Q' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Resetting the exact client output ACL failed: $LASTEXITCODE"
    }
    Assert-RebornControlledHostWritableOutputFileInactive `
        $ClientRoot | Out-Null
}

Export-ModuleMember -Function @(
    'New-RebornControlledHostWritableOutputFileSecurity',
    'Assert-RebornControlledHostWritableOutputFileSecurity',
    'Assert-RebornControlledHostWritableOutputFileAcls',
    'Assert-RebornControlledHostWritableOutputFileInactive',
    'Get-RebornControlledHostWritableOutputFileState',
    'Enable-RebornControlledHostWritableOutputFile',
    'Disable-RebornControlledHostWritableOutputFile'
)
