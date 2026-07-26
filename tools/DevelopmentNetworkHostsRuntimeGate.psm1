Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsWorkflow.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsRecovery.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsReceipt.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkOperationLock.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsAcl.psm1'
)

$script:BeginMarker = '# BEGIN REBORN SECURE NETWORK DEVELOPMENT'
$script:Mapping = '127.0.0.1 login.reborn.test game.reborn.test'
$script:EndMarker = '# END REBORN SECURE NETWORK DEVELOPMENT'
$script:ManagedNames = @('login.reborn.test', 'game.reborn.test')

function Enter-RebornDevelopmentHostsRuntimeLease {
    param(
        [string]$LockRoot = (
            Join-Path $env:ProgramData 'RebornSecureNetworkLocks'),
        [switch]$AllowTestPath
    )

    Enter-RebornSecureNetworkOperationReadLease `
        -Name 'development-hosts' `
        -LockRoot $LockRoot `
        -AllowTestPath:$AllowTestPath
}

function Enter-RebornDevelopmentHostsRuntimeLock {
    param(
        [string]$LockRoot = (
            Join-Path $env:ProgramData 'RebornSecureNetworkLocks'),
        [switch]$AllowTestPath
    )
    Enter-RebornDevelopmentHostsRuntimeLease `
        -LockRoot $LockRoot `
        -AllowTestPath:$AllowTestPath
}

function Exit-RebornDevelopmentHostsRuntimeLock {
    param([Parameter(Mandatory)][object]$Lock)
    Exit-RebornSecureNetworkOperationLock $Lock
}

function Assert-RebornDevelopmentHostsInstalledExact {
    param(
        [string]$HostsPath = (
            Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'),
        [string]$ReceiptPath = (
            Join-Path $env:ProgramData (
                'RebornSecureNetworkBackups\' +
                'development-hosts\' +
                'development-hosts-receipt.json')),
        [switch]$AllowTestPath
    )

    $hosts = [IO.Path]::GetFullPath($HostsPath)
    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    Assert-RebornDevelopmentHostsPaths `
        $hosts $receipt Status -AllowTestPath:$AllowTestPath
    if (-not $AllowTestPath) {
        $receiptRoot = Split-Path -Parent $receipt
        Assert-RebornDevelopmentHostsArtifactReadAcl `
            $receiptRoot | Out-Null
        Assert-RebornDevelopmentHostsArtifactReadAcl `
            $receipt -File | Out-Null
    }

    $loaded = Read-RebornDevelopmentHostsActiveReceipt `
        $receipt $hosts `
        $script:BeginMarker $script:Mapping $script:EndMarker
    if (-not $AllowTestPath) {
        Assert-RebornDevelopmentHostsArtifactReadAcl `
            $loaded.BackupPath -File | Out-Null
    }
    $status = Get-RebornDevelopmentHostsState `
        $hosts `
        $script:BeginMarker `
        $script:Mapping `
        $script:EndMarker `
        $script:ManagedNames
    if (
        [string]$loaded.Record.state -cne 'InstalledExact' -or
        $status.State -cne 'InstalledExact' -or
        $status.Sha256 -cne
            [string]$loaded.Record.intendedAppliedSha256 -or
        [string]$loaded.Record.appliedSha256 -cne
            [string]$loaded.Record.intendedAppliedSha256
    ) {
        throw (
            'Development hosts runtime gate requires the exact ' +
            'receipt-bound InstalledExact mapping.')
    }

    [pscustomobject]@{
        State = 'InstalledExact'
        HostsPath = $hosts
        HostsSha256 = $status.Sha256
        ReceiptPath = $loaded.Path
        ReceiptState = [string]$loaded.Record.state
        BackupPath = $loaded.BackupPath
        OriginalSha256 = [string]$loaded.Record.originalSha256
        IntendedAppliedSha256 =
            [string]$loaded.Record.intendedAppliedSha256
    }
}

Export-ModuleMember -Function @(
    'Enter-RebornDevelopmentHostsRuntimeLease',
    'Enter-RebornDevelopmentHostsRuntimeLock',
    'Exit-RebornDevelopmentHostsRuntimeLock',
    'Assert-RebornDevelopmentHostsInstalledExact'
)
