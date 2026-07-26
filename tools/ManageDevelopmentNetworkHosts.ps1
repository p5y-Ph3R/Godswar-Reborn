[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply', 'Restore')]
    [string]$Mode = 'Status',

    [string]$HostsPath = (
        Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'),

    [string]$ReceiptPath = (
        Join-Path $env:ProgramData `
            ('RebornSecureNetworkBackups\development-hosts\' +
                'development-hosts-receipt.json')),

    [string]$OperationLockRoot = (
        Join-Path $env:ProgramData 'RebornSecureNetworkLocks'),

    [switch]$AllowHostsWrite,

    [switch]$AllowTestPath,

    [ValidateSet(
        'None',
        'AfterPrepared',
        'DuringManagedAppend',
        'AfterManagedWrite',
        'AfterRollbackBytesBeforeReceipt',
        'DuringRestoreTruncate',
        'AfterRestoreBytesBeforeReceipt')]
    [string]$TestFailurePoint = 'None',

    [switch]$LeaveInterruptedForTest,

    [switch]$TestDnsFlushFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$beginMarker = '# BEGIN REBORN SECURE NETWORK DEVELOPMENT'
$mapping = '127.0.0.1 login.reborn.test game.reborn.test'
$endMarker = '# END REBORN SECURE NETWORK DEVELOPMENT'
$managedNames = @('login.reborn.test', 'game.reborn.test')

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsWorkflow.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsRecovery.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsReceipt.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsFileIO.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkHostsAcl.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
)

function Assert-OriginClosed {
    if (-not $AllowTestPath -and
        (Get-Process -Name Origin -ErrorAction SilentlyContinue)) {
        throw 'Origin.exe must be closed before development DNS changes.'
    }
}

function Get-HostsState {
    param([Parameter(Mandatory)][string]$Path)

    Get-RebornDevelopmentHostsState `
        $Path $beginMarker $mapping $endMarker $managedNames
}

function Clear-ManagedDnsCache {
    Clear-RebornManagedDnsCache `
        -AllowTestPath:$AllowTestPath `
        -TestFailure:$TestDnsFlushFailure
}

function Confirm-ManagedDns {
    Confirm-RebornManagedDns `
        $managedNames `
        -AllowTestPath:$AllowTestPath `
        -TestFailure:$TestDnsFlushFailure
}

function Read-ActiveReceipt {
    param([Parameter(Mandatory)][string]$Path)

    return Read-RebornDevelopmentHostsActiveReceipt `
        $Path $hosts $beginMarker $mapping $endMarker
}

function Complete-PendingDnsFlush {
    param([Parameter(Mandatory)][object]$Loaded)

    try {
        Clear-ManagedDnsCache
    }
    catch {
        throw (
            'Exact hosts bytes are restored, but active PendingDnsFlush ' +
            'receipt remains. Retry this command. ' +
            $_.Exception.Message)
    }
    return Complete-RebornHostsReceipt `
        $Loaded ([string]$Loaded.Record.pendingCompletion) -DnsFlushed
}

function Resolve-ActiveReceipt {
    return Resolve-RebornDevelopmentHostsReceipt `
        $receipt $hosts $beginMarker $mapping $endMarker $managedNames
}

function Restore-ExactOriginal {
    param(
        [Parameter(Mandatory)][object]$Loaded,
        [Parameter(Mandatory)][string[]]$AcceptedCurrentSha256
    )

    return Restore-RebornDevelopmentHostsOriginal `
        $Loaded $AcceptedCurrentSha256 $hosts `
        $beginMarker $mapping $endMarker $managedNames
}

$hosts = [IO.Path]::GetFullPath($HostsPath)
$receipt = [IO.Path]::GetFullPath($ReceiptPath)
Assert-RebornDevelopmentHostsPaths `
    $hosts $receipt $Mode -AllowTestPath:$AllowTestPath
Assert-RebornHostsTestControls `
    $Mode `
    -AllowTestPath:$AllowTestPath `
    -FailurePoint $TestFailurePoint `
    -LeaveInterrupted:$LeaveInterruptedForTest `
    -DnsFlushFailure:$TestDnsFlushFailure

$status = Get-HostsState $hosts
if ($Mode -eq 'Status') {
    $receiptState = if (
        Test-Path -LiteralPath $receipt -PathType Leaf
    ) {
        try {
            (Read-ActiveReceipt $receipt).Record.state
        }
        catch {
            'Invalid'
        }
    } else {
        'None'
    }
    [pscustomobject]@{
        State = $status.State
        HostsPath = $hosts
        HostsSha256 = $status.Sha256
        ReceiptPath = $receipt
        ReceiptState = $receiptState
        ReceiptExists = Test-Path -LiteralPath $receipt -PathType Leaf
    }
    return
}

if (-not $AllowHostsWrite) {
    throw "$Mode requires explicit -AllowHostsWrite."
}
if (-not $PSCmdlet.ShouldProcess(
        $hosts,
        "$Mode exact loopback development DNS state")) {
    return
}

Assert-OriginClosed
$mutation = Enter-RebornDevelopmentHostsMutation
$operationLock = $null
try {
    $operationLock = Enter-RebornSecureNetworkOperationLock `
        -Name 'development-hosts' `
        -LockRoot $OperationLockRoot `
        -AllowTestPath:$AllowTestPath
    $receiptDirectory = Split-Path -Parent $receipt
    Initialize-RebornHostsReceiptDirectory `
        $receiptDirectory -AllowTestPath:$AllowTestPath
    $resolution = Resolve-ActiveReceipt

    if ($resolution.State -in @(
        'PreparedPartial',
        'PreparedOriginalPendingFlush',
        'RestoreInterrupted',
        'InstalledOriginalPendingFlush'
    )) {
        $completion = if (
            $resolution.State -in @(
                'PreparedPartial',
                'PreparedOriginalPendingFlush')
        ) {
            'RolledBack'
        } else {
            'Restored'
        }
        if (-not (
            $resolution.PSObject.Properties['BytesAlreadyRestored'] -and
            $resolution.BytesAlreadyRestored
        )) {
            $partialStatus = Get-HostsState $hosts
            Restore-ExactOriginal `
                $resolution.Loaded @($partialStatus.Sha256) | Out-Null
        }
        Set-RebornHostsReceiptPendingDnsFlush `
            $resolution.Loaded $completion | Out-Null
        $pending = Read-ActiveReceipt $receipt
        $history = Complete-PendingDnsFlush $pending
        if ($Mode -eq 'Restore') {
            [pscustomobject]@{
                Result = 'AlreadyRestored'
                HostsSha256 = (Get-HostsState $hosts).Sha256
                HistoryPath = $history
                RecoveredInterruptedWrite = $true
                Completion = $completion
            }
            return
        }
        $resolution = [pscustomobject]@{
            State = "Recovered$completion"
            Loaded = $null
            HistoryPath = $history
        }
    }

    if ($resolution.State -eq 'PendingDnsFlush') {
        $pendingCompletion =
            [string]$resolution.Loaded.Record.pendingCompletion
        $history = Complete-PendingDnsFlush $resolution.Loaded
        if ($Mode -eq 'Restore') {
            [pscustomobject]@{
                Result = 'AlreadyRestored'
                HostsSha256 = (Get-HostsState $hosts).Sha256
                HistoryPath = $history
                RecoveredPendingDnsFlush = $true
                Completion = $pendingCompletion
            }
            return
        }
        $resolution = [pscustomobject]@{
            State = "Recovered$pendingCompletion"
            Loaded = $null
            HistoryPath = $history
        }
    }

    if ($Mode -eq 'Apply') {
        if ($resolution.State -eq 'InstalledExact') {
            Confirm-ManagedDns
            [pscustomobject]@{
                Result = 'AlreadyInstalledExact'
                HostsSha256 =
                    $resolution.Loaded.Record.intendedAppliedSha256
                ReceiptPath = $receipt
                Recovered = $true
            }
            return
        }

        $status = Get-HostsState $hosts
        if ($status.State -ne 'Absent') {
            throw "Hosts Apply requires Absent state, got $($status.State)."
        }
        if (Test-Path -LiteralPath $receipt) {
            throw 'A completed receipt was not archived before Apply.'
        }

        $backup = Join-Path $receiptDirectory (
            "hosts-$([guid]::NewGuid().ToString('N')).original")
        Copy-Item -LiteralPath $hosts -Destination $backup
        $backup = Assert-RebornSingleLinkRegularFilePath `
            $backup 'hosts backup'
        if (-not $AllowTestPath) {
            Protect-RebornDevelopmentHostsArtifact `
                $backup -File | Out-Null
        }
        $backupStream = [IO.File]::Open(
            $backup,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        try {
            $backupStream.Flush($true)
        }
        finally {
            $backupStream.Dispose()
        }
        $backupHash = Get-RebornHostsFileSha256 $backup
        if ($backupHash -cne $status.Sha256) {
            throw 'Copied hosts backup does not match the captured original.'
        }
        $intendedBytes = Get-RebornManagedHostsBytes `
            $backup $beginMarker $mapping $endMarker
        $intendedHash = Get-RebornHostsByteSha256 $intendedBytes
        $record = [ordered]@{
            schemaVersion = 3
            state = 'Prepared'
            readerSid =
                [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            hostsPath = $hosts
            originalSha256 = $status.Sha256
            backupPath = $backup
            backupSha256 = $backupHash
            intendedAppliedSha256 = $intendedHash
            appliedSha256 = $null
            preparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            appliedUtc = $null
            rolledBackUtc = $null
            restoredUtc = $null
            pendingCompletion = $null
            dnsFlushPendingUtc = $null
            dnsFlushedUtc = $null
        }
        Write-RebornHostsReceiptAtomic $record $receipt
        $loaded = Read-ActiveReceipt $receipt

        try {
            if ($TestFailurePoint -eq 'AfterPrepared') {
                throw 'Simulated hosts interruption after Prepared.'
            }
            $immediate = Get-HostsState $hosts
            if (
                $immediate.State -ne 'Absent' -or
                $immediate.Sha256 -cne $status.Sha256
            ) {
                throw 'Hosts changed after backup and before mutation.'
            }
            if ($TestFailurePoint -eq 'DuringManagedAppend') {
                $originalLength =
                    (Get-Item -LiteralPath $backup).Length
                $partial = Get-RebornInterruptedHostsBytes `
                    $intendedBytes $originalLength
                try {
                    Write-RebornHostsBytesLocked `
                        $hosts $partial @($status.Sha256)
                }
                finally {
                    [Array]::Clear($partial, 0, $partial.Length)
                }
                throw 'Simulated hosts interruption during managed append.'
            } else {
                Write-RebornHostsBytesLocked `
                    $hosts $intendedBytes @($status.Sha256)
            }
            $applied = Get-HostsState $hosts
            if (
                $applied.State -ne 'InstalledExact' -or
                $applied.Sha256 -cne $intendedHash
            ) {
                throw 'Managed host mappings did not validate after Apply.'
            }
            $record.appliedSha256 = $applied.Sha256
            if ($TestFailurePoint -in @(
                'AfterManagedWrite',
                'AfterRollbackBytesBeforeReceipt')) {
                throw 'Simulated hosts interruption after managed write.'
            }
            Confirm-ManagedDns
            $record.state = 'InstalledExact'
            $record.appliedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            Write-RebornHostsReceiptAtomic $record $receipt
            [pscustomobject]@{
                Result = 'Applied'
                HostsSha256 = $applied.Sha256
                ReceiptPath = $receipt
                BackupPath = $backup
                RecoveredPriorState = $resolution.State
            }
        }
        catch {
            $saved = $_
            if ($LeaveInterruptedForTest -and
                $TestFailurePoint -ne 'None' -and
                $TestFailurePoint -ne
                    'AfterRollbackBytesBeforeReceipt') {
                throw
            }
            $current = Get-HostsState $hosts
            $accepted = @(
                [string]$record.originalSha256,
                [string]$record.intendedAppliedSha256
            )
            $partialRollback =
                Test-RebornHostsInterruptedManagedPrefix `
                    $hosts $loaded.BackupPath `
                    $beginMarker $mapping $endMarker
            if (
                $accepted -cnotcontains $current.Sha256 -and
                -not $partialRollback
            ) {
                throw (
                    'Hosts Apply failed and current bytes are ambiguous; ' +
                    'the Prepared receipt was retained. ' +
                    $saved.Exception.Message)
            }
            if ($partialRollback) {
                $accepted += $current.Sha256
            }
            Restore-ExactOriginal $loaded $accepted | Out-Null
            if (
                $LeaveInterruptedForTest -and
                $TestFailurePoint -eq
                    'AfterRollbackBytesBeforeReceipt'
            ) {
                throw (
                    'Simulated interruption after rollback bytes and ' +
                    'before PendingDnsFlush receipt.')
            }
            Set-RebornHostsReceiptPendingDnsFlush `
                $loaded 'RolledBack' | Out-Null
            $pending = Read-ActiveReceipt $receipt
            try {
                $history = Complete-PendingDnsFlush $pending
            }
            catch {
                throw (
                    $saved.Exception.Message +
                    ' Exact rollback passed; active PendingDnsFlush ' +
                    'receipt retained. ' +
                    $_.Exception.Message)
            }
            throw (
                $saved.Exception.Message +
                " Exact rollback passed; receipt archived at $history")
        }
        finally {
            [Array]::Clear($intendedBytes, 0, $intendedBytes.Length)
        }
        return
    }

    if ($resolution.State -in @(
        'RecoveredRestored',
        'RecoveredRolledBack'
    )) {
        [pscustomobject]@{
            Result = 'AlreadyRestored'
            HostsSha256 = (Get-HostsState $hosts).Sha256
            HistoryPath = $resolution.HistoryPath
        }
        return
    }
    if ($resolution.State -ne 'InstalledExact') {
        throw "Hosts Restore has no active installed receipt: $receipt"
    }

    $loaded = $resolution.Loaded
    $loaded.Record.state = 'Restoring'
    Write-RebornHostsReceiptAtomic $loaded.Record $receipt
    $loaded = Read-ActiveReceipt $receipt
    if ($TestFailurePoint -eq 'DuringRestoreTruncate') {
        $intended = [IO.File]::ReadAllBytes($hosts)
        try {
            $originalLength =
                (Get-Item -LiteralPath $loaded.BackupPath).Length
            $partial = Get-RebornInterruptedHostsBytes `
                $intended $originalLength
            try {
                Write-RebornHostsBytesLocked `
                    $hosts $partial @(
                        [string]$loaded.Record.intendedAppliedSha256)
            }
            finally {
                [Array]::Clear($partial, 0, $partial.Length)
            }
        }
        finally {
            [Array]::Clear($intended, 0, $intended.Length)
        }
        throw 'Simulated hosts interruption during Restore truncation.'
    }
    $restored = Restore-ExactOriginal `
        $loaded @([string]$loaded.Record.intendedAppliedSha256)
    if (
        $LeaveInterruptedForTest -and
        $TestFailurePoint -eq 'AfterRestoreBytesBeforeReceipt'
    ) {
        throw (
            'Simulated interruption after Restore bytes and before ' +
            'PendingDnsFlush receipt.')
    }
    Set-RebornHostsReceiptPendingDnsFlush `
        $loaded 'Restored' | Out-Null
    $pending = Read-ActiveReceipt $receipt
    $history = Complete-PendingDnsFlush $pending
    [pscustomobject]@{
        Result = 'Restored'
        HostsSha256 = $restored.Sha256
        HistoryPath = $history
    }
}
finally {
    if ($null -ne $operationLock) {
        Exit-RebornSecureNetworkOperationLock $operationLock
    }
    $mutation.ReleaseMutex()
    $mutation.Dispose()
}
