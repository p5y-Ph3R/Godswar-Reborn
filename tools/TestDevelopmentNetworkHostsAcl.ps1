[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsAcl.psm1'
) -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Test-IssuedSecurity {
    param([switch]$File)

    $source = New-RebornDevelopmentHostsArtifactSecurity -File:$File
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Access
    $sddl = $source.GetSecurityDescriptorSddlForm($sections)
    $security = if ($File) {
        [Security.AccessControl.FileSecurity]::new()
    } else {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    $security.SetSecurityDescriptorSddlForm($sddl, $sections)

    $owner = $security.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    Assert-True (
        $owner -ceq 'S-1-5-32-544' -and
        $security.AreAccessRulesProtected
    ) 'issued hosts ACL round trip lost owner or protected DACL'

    $current =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $rules = @($security.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    $expected = @('S-1-5-18', 'S-1-5-32-544', $current) |
        Select-Object -Unique
    Assert-True (
        $rules.Count -eq $expected.Count -and
        @($rules | Where-Object {
            $_.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $expected -cnotcontains $_.IdentityReference.Value
        }).Count -eq 0
    ) 'issued hosts ACL contains an unexpected access rule'

    $currentRules = @($rules | Where-Object {
        $_.IdentityReference.Value -ceq $current
    })
    $mutation =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    Assert-True (
        $currentRules.Count -eq 1 -and
        ($currentRules[0].FileSystemRights -band $mutation) -eq 0 -and
        ($currentRules[0].FileSystemRights -band
            [Security.AccessControl.FileSystemRights]::Read) -eq
            [Security.AccessControl.FileSystemRights]::Read
    ) 'issued hosts ACL grants current SID mutation rights'

    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $trusted = @($rules | Where-Object {
            $_.IdentityReference.Value -ceq $sid
        })
        Assert-True (
            $trusted.Count -eq 1 -and
            ($trusted[0].FileSystemRights -band
                [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl
        ) "issued hosts ACL does not grant $sid full control"
    }
    return $true
}

$reparseRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-hosts-acl-test-$([guid]::NewGuid().ToString('N'))")
$reparseTarget = Join-Path $reparseRoot 'target'
$reparsePath = Join-Path $reparseRoot 'junction'
$reparseRejected = $false
try {
    [IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    New-Item -ItemType Junction `
        -Path $reparsePath -Target $reparseTarget | Out-Null
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Access
    $before = (Get-Acl -LiteralPath $reparseTarget).
        GetSecurityDescriptorSddlForm($sections)
    try {
        Protect-RebornDevelopmentHostsArtifact $reparsePath | Out-Null
    }
    catch {
        $reparseRejected = $_.Exception.Message -match 'reparse'
    }
    $after = (Get-Acl -LiteralPath $reparseTarget).
        GetSecurityDescriptorSddlForm($sections)
    Assert-True (
        $reparseRejected -and $before -ceq $after
    ) 'reparse-path rejection changed the followed target ACL'
}
finally {
    if (Test-Path -LiteralPath $reparsePath) {
        Remove-Item -LiteralPath $reparsePath -Force
    }
    $resolved = [IO.Path]::GetFullPath($reparseRoot)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected ACL test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

[pscustomobject]@{
    Result = 'Passed'
    ProtectedDirectoryAcl = Test-IssuedSecurity
    ProtectedFileAcl = Test-IssuedSecurity -File
    CurrentSidReadOnly = $true
    ReparsePrevalidation = $reparseRejected
}
