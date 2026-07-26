Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsAcl.psm1'
)

function Test-RebornHostsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-RebornDevelopmentHostsPaths {
    param(
        [Parameter(Mandatory)][string]$ResolvedHosts,
        [Parameter(Mandatory)][string]$ResolvedReceipt,
        [Parameter(Mandatory)]
        [ValidateSet('Status', 'Apply', 'Restore')]
        [string]$Mode,
        [switch]$AllowTestPath
    )

    $systemHosts = [IO.Path]::GetFullPath(
        (Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'))
    if (-not $AllowTestPath) {
        if (-not $ResolvedHosts.Equals(
                $systemHosts,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A non-system hosts path requires explicit -AllowTestPath.'
        }
        $issuedReceipt = [IO.Path]::GetFullPath(
            (Join-Path $env:ProgramData (
                'RebornSecureNetworkBackups\' +
                'development-hosts\' +
                'development-hosts-receipt.json')))
        if (-not $ResolvedReceipt.Equals(
                $issuedReceipt,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Production ReceiptPath must equal the issued protected ' +
                'development-hosts receipt path.')
        }
        if ($Mode -ne 'Status' -and
            -not (Test-RebornHostsAdministrator)) {
            throw 'Hosts Apply/Restore requires an elevated PowerShell process.'
        }
    } else {
        $temporaryRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        foreach ($candidate in @($ResolvedHosts, $ResolvedReceipt)) {
            if (-not $candidate.StartsWith(
                    $temporaryRoot,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    'Test hosts and receipt paths must remain below the ' +
                    'current temporary directory.')
            }
        }
    }

    foreach ($candidate in @($ResolvedHosts, $ResolvedReceipt)) {
        $root = [IO.Path]::GetPathRoot($candidate).TrimEnd('\')
        if ($candidate.TrimEnd('\').Equals(
                $root,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing a filesystem-root path: $candidate"
        }
    }
    if (Test-Path -LiteralPath $ResolvedHosts -PathType Leaf) {
        Assert-RebornSingleLinkRegularFilePath `
            $ResolvedHosts 'hosts file' | Out-Null
    }
}

function Assert-RebornHostsTestControls {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Status', 'Apply', 'Restore')]
        [string]$Mode,
        [switch]$AllowTestPath,
        [Parameter(Mandatory)][string]$FailurePoint,
        [switch]$LeaveInterrupted,
        [switch]$DnsFlushFailure
    )

    if (
        ($FailurePoint -ne 'None' -or
            $LeaveInterrupted -or
            $DnsFlushFailure) -and
        -not $AllowTestPath
    ) {
        throw 'Hosts fault injection is available only with -AllowTestPath.'
    }
    if ($LeaveInterrupted -and $FailurePoint -eq 'None') {
        throw '-LeaveInterruptedForTest requires a test failure point.'
    }
    if (
        ($Mode -eq 'Apply' -and
            $FailurePoint -in @(
                'DuringRestoreTruncate',
                'AfterRestoreBytesBeforeReceipt')) -or
        ($Mode -eq 'Restore' -and
            $FailurePoint -notin @(
                'None',
                'DuringRestoreTruncate',
                'AfterRestoreBytesBeforeReceipt'))
    ) {
        throw 'The selected hosts failure point does not apply to this mode.'
    }
}

function Enter-RebornDevelopmentHostsMutation {
    $mutex = [Threading.Mutex]::new(
        $false,
        'Local\RebornDevelopmentNetworkHostsV1')
    try {
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
        }
        if (-not $acquired) {
            throw 'Another development-hosts mutation is active.'
        }
        return $mutex
    }
    catch {
        $mutex.Dispose()
        throw
    }
}

function Initialize-RebornHostsReceiptDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowTestPath
    )

    if ($AllowTestPath) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            New-Item -ItemType Directory -Path $Path | Out-Null
        }
        Assert-RebornDirectoryPath $Path 'test hosts receipt directory' |
            Out-Null
        return
    }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        $grandparent = Split-Path -Parent $parent
        Assert-RebornProtectedDirectoryPath `
            $grandparent 'development hosts backup grandparent' `
            -ProtectChildren | Out-Null
        [IO.Directory]::CreateDirectory(
            $parent,
            (New-RebornDevelopmentHostsArtifactSecurity)) | Out-Null
    } else {
        Assert-RebornProtectedDirectoryPath `
            $parent 'development hosts shared backup parent' `
            -ProtectContents -RequireProtectedAcl | Out-Null
        Protect-RebornDevelopmentHostsArtifact $parent | Out-Null
    }
    Assert-RebornDevelopmentHostsArtifactReadAcl $parent | Out-Null
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        [IO.Directory]::CreateDirectory(
            $Path,
            (New-RebornDevelopmentHostsArtifactSecurity)) | Out-Null
    } else {
        Assert-RebornProtectedDirectoryPath `
            $Path 'development hosts receipt directory' `
            -ProtectContents -RequireProtectedAcl | Out-Null
        Protect-RebornDevelopmentHostsArtifact $Path | Out-Null
    }
    foreach ($entry in @(
        Get-ChildItem -LiteralPath $Path -Force
    )) {
        if ($entry.PSIsContainer -or
            $entry.Name -notmatch (
                '^(development-hosts-receipt\.json' +
                '(\.previous)?|' +
                'development-hosts-receipt\.history-' +
                '\d{8}-\d{9}-[0-9a-f]{32}-' +
                '(RolledBack|Restored)\.json|' +
                'hosts-[0-9a-f]{32}\.original)$')) {
            throw 'Development hosts receipt directory has an unknown entry.'
        }
        Protect-RebornDevelopmentHostsArtifact `
            $entry.FullName -File | Out-Null
    }
    Assert-RebornDevelopmentHostsArtifactReadAcl $Path | Out-Null
}

function Clear-RebornManagedDnsCache {
    param(
        [switch]$AllowTestPath,
        [switch]$TestFailure
    )

    if ($AllowTestPath) {
        if ($TestFailure) {
            throw 'Simulated Windows DNS resolver-cache flush failure.'
        }
        return
    }
    & ipconfig.exe /flushdns | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not flush the Windows DNS resolver cache.'
    }
}

function Confirm-RebornManagedDns {
    param(
        [Parameter(Mandatory)][string[]]$ManagedNames,
        [switch]$AllowTestPath,
        [switch]$TestFailure
    )

    Clear-RebornManagedDnsCache `
        -AllowTestPath:$AllowTestPath `
        -TestFailure:$TestFailure
    if ($AllowTestPath) {
        return
    }
    foreach ($name in $ManagedNames) {
        $addresses = @([Net.Dns]::GetHostAddresses($name))
        if ($addresses.Count -eq 0) {
            throw "Managed development DNS did not resolve: $name"
        }
        foreach ($address in $addresses) {
            if (-not [Net.IPAddress]::IsLoopback($address)) {
                throw "$name resolved outside loopback: $address"
            }
        }
    }
}

function Get-RebornInterruptedHostsBytes {
    param(
        [Parameter(Mandatory)][byte[]]$Intended,
        [Parameter(Mandatory)][int]$OriginalLength
    )

    if ($Intended.Length -le ($OriginalLength + 1)) {
        throw 'Managed hosts suffix is too short for interruption testing.'
    }
    $partialLength = [int](
        $OriginalLength +
        [Math]::Max(
            1,
            [Math]::Floor(
                ($Intended.Length - $OriginalLength) / 2)))
    $partial = New-Object byte[] $partialLength
    [Array]::Copy($Intended, $partial, $partialLength)
    return $partial
}

Export-ModuleMember -Function @(
    'Assert-RebornDevelopmentHostsPaths',
    'Assert-RebornHostsTestControls',
    'Enter-RebornDevelopmentHostsMutation',
    'Initialize-RebornHostsReceiptDirectory',
    'Clear-RebornManagedDnsCache',
    'Confirm-RebornManagedDns',
    'Get-RebornInterruptedHostsBytes'
)
