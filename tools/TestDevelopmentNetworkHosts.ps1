[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath =
    Join-Path $PSScriptRoot 'ManageDevelopmentNetworkHosts.ps1'
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force
$root = Join-Path ([IO.Path]::GetTempPath()) (
    "reborn-hosts-test-$([guid]::NewGuid().ToString('N'))")

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function New-HostsFixture {
    param([Parameter(Mandatory)][string]$Name)

    $directory = Join-Path $root $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $hosts = Join-Path $directory 'hosts'
    $receipt = Join-Path $directory 'receipt\hosts-receipt.json'
    [IO.File]::WriteAllText(
        $hosts,
        "127.0.0.1 localhost`r`n",
        [Text.Encoding]::ASCII)
    [pscustomobject]@{
        Root = $directory
        Hosts = $hosts
        Receipt = $receipt
        OriginalSha256 = (
            Get-FileHash -LiteralPath $hosts -Algorithm SHA256).Hash
    }
}

function Invoke-HostsTool {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)]
        [ValidateSet('Status', 'Apply', 'Restore')]
        [string]$Mode,
        [hashtable]$Extra = @{}
    )

    $parameters = @{
        Mode = $Mode
        HostsPath = $Fixture.Hosts
        ReceiptPath = $Fixture.Receipt
        OperationLockRoot = (Join-Path $Fixture.Root 'operation-lock')
        AllowTestPath = $true
    }
    if ($Mode -ne 'Status') {
        $parameters.AllowHostsWrite = $true
        $parameters.Confirm = $false
    }
    foreach ($entry in $Extra.GetEnumerator()) {
        $parameters[$entry.Key] = $entry.Value
    }
    return & $scriptPath @parameters
}

function Get-ReceiptHistory {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [string]$State = '*'
    )

    $directory = Split-Path -Parent $Fixture.Receipt
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return @()
    }
    return @(
        Get-ChildItem -LiteralPath $directory -File |
            Where-Object {
                $_.Name -like "hosts-receipt.history-*-$State.json"
            }
    )
}

function Assert-ExactOriginal {
    param([Parameter(Mandatory)][object]$Fixture)

    $status = Invoke-HostsTool $Fixture Status
    Assert-True (
        $status.State -eq 'Absent' -and
        $status.HostsSha256 -ceq $Fixture.OriginalSha256 -and
        -not $status.ReceiptExists
    ) "$($Fixture.Root) is not exact original with no active receipt"
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    $errorText = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $errorText = $_.Exception.Message
    }
    Assert-True (
        $null -ne $errorText -and $errorText -match $Pattern
    ) "$Message; error was: $errorText"
}

[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $arbitraryProductionReceipt =
        Join-Path $root 'arbitrary-production-receipt.json'
    Assert-Throws {
        & $scriptPath `
            -Mode Status `
            -HostsPath (
                Join-Path $env:SystemRoot 'System32\drivers\etc\hosts') `
            -ReceiptPath $arbitraryProductionReceipt
    } 'Production ReceiptPath must equal' (
        'production Status accepted an arbitrary receipt path')

    $readOnly = New-HostsFixture 'status-whatif-readonly'
    $readOnlyLockRoot = Join-Path $readOnly.Root 'operation-lock'
    Invoke-HostsTool $readOnly Status | Out-Null
    Assert-True (
        -not (Test-Path -LiteralPath $readOnlyLockRoot) -and
        -not (Test-Path -LiteralPath $readOnly.Receipt) -and
        (Get-FileHash $readOnly.Hosts -Algorithm SHA256).Hash -ceq
            $readOnly.OriginalSha256
    ) 'Status created operation state or changed hosts bytes'
    Invoke-HostsTool $readOnly Apply @{ WhatIf = $true } | Out-Null
    Assert-True (
        -not (Test-Path -LiteralPath $readOnlyLockRoot) -and
        -not (Test-Path -LiteralPath $readOnly.Receipt) -and
        (Get-FileHash $readOnly.Hosts -Algorithm SHA256).Hash -ceq
            $readOnly.OriginalSha256
    ) '-WhatIf created operation state or changed hosts bytes'

    $cycles = New-HostsFixture 'two-cycles'
    $before = Invoke-HostsTool $cycles Status
    Assert-True (
        $before.State -eq 'Absent' -and
        $before.ReceiptState -eq 'None'
    ) 'initial hosts fixture was not absent'

    Invoke-HostsTool $cycles Apply | Out-Null
    $installed = Invoke-HostsTool $cycles Status
    Assert-True (
        $installed.State -eq 'InstalledExact' -and
        $installed.ReceiptState -eq 'InstalledExact'
    ) 'first Apply did not reach InstalledExact'

    $installedBytes = [IO.File]::ReadAllBytes($cycles.Hosts)
    try {
        [IO.File]::AppendAllText(
            $cycles.Hosts,
            "192.0.2.1 unrelated.reborn.test`r`n",
            [Text.Encoding]::ASCII)
        $changedHash = (
            Get-FileHash $cycles.Hosts -Algorithm SHA256).Hash
        Assert-Throws {
            Invoke-HostsTool $cycles Restore
        } 'refusing any mutation' (
            'Restore did not reject unrelated hosts changes')
        Assert-True (
            (Get-FileHash $cycles.Hosts -Algorithm SHA256).Hash -ceq
                $changedHash
        ) 'rejected Restore still changed unrelated hosts bytes'
        [IO.File]::WriteAllBytes($cycles.Hosts, $installedBytes)
    }
    finally {
        [Array]::Clear($installedBytes, 0, $installedBytes.Length)
    }

    Invoke-HostsTool $cycles Restore | Out-Null
    Assert-ExactOriginal $cycles
    Assert-True (
        @(Get-ReceiptHistory $cycles 'Restored').Count -eq 1
    ) 'first Restore did not archive one Restored receipt'

    Invoke-HostsTool $cycles Apply | Out-Null
    Invoke-HostsTool $cycles Restore | Out-Null
    Assert-ExactOriginal $cycles
    Assert-True (
        @(Get-ReceiptHistory $cycles 'Restored').Count -eq 2
    ) 'second Apply/Restore cycle did not preserve both histories'

    $failed = New-HostsFixture 'failed-apply-retry'
    Assert-Throws {
        Invoke-HostsTool $failed Apply @{
            TestFailurePoint = 'AfterManagedWrite'
        }
    } 'Exact rollback passed' (
        'injected Apply failure did not report exact rollback')
    Assert-ExactOriginal $failed
    Assert-True (
        @(Get-ReceiptHistory $failed 'RolledBack').Count -eq 1
    ) 'failed Apply did not archive a RolledBack receipt'
    Invoke-HostsTool $failed Apply | Out-Null
    Invoke-HostsTool $failed Restore | Out-Null
    Assert-ExactOriginal $failed

    $pendingRollback = New-HostsFixture 'pending-dns-rollback'
    Assert-Throws {
        Invoke-HostsTool $pendingRollback Apply @{
            TestDnsFlushFailure = $true
        }
    } 'PendingDnsFlush' (
        'failed rollback DNS flush did not retain a pending receipt')
    $pendingRollbackStatus =
        Invoke-HostsTool $pendingRollback Status
    Assert-True (
        $pendingRollbackStatus.State -eq 'Absent' -and
        $pendingRollbackStatus.ReceiptState -eq 'PendingDnsFlush'
    ) 'rollback DNS failure did not preserve exact bytes plus pending state'
    $retriedApply = Invoke-HostsTool $pendingRollback Apply
    Assert-True (
        $retriedApply.Result -eq 'Applied' -and
        $retriedApply.RecoveredPriorState -eq 'RecoveredRolledBack'
    ) 'Apply did not finish the pending rollback flush before retry'
    Invoke-HostsTool $pendingRollback Restore | Out-Null
    Assert-ExactOriginal $pendingRollback

    $pendingRestore = New-HostsFixture 'pending-dns-restore'
    Invoke-HostsTool $pendingRestore Apply | Out-Null
    Assert-Throws {
        Invoke-HostsTool $pendingRestore Restore @{
            TestDnsFlushFailure = $true
        }
    } 'PendingDnsFlush' (
        'failed Restore DNS flush did not retain a pending receipt')
    $pendingRestoreStatus = Invoke-HostsTool $pendingRestore Status
    $writeTimeBeforeRetry =
        (Get-Item -LiteralPath $pendingRestore.Hosts).LastWriteTimeUtc
    Assert-True (
        $pendingRestoreStatus.State -eq 'Absent' -and
        $pendingRestoreStatus.ReceiptState -eq 'PendingDnsFlush'
    ) 'Restore DNS failure did not leave exact bytes plus pending state'
    $retriedRestore = Invoke-HostsTool $pendingRestore Restore
    Assert-True (
        $retriedRestore.Result -eq 'AlreadyRestored' -and
        $retriedRestore.RecoveredPendingDnsFlush -and
        $retriedRestore.Completion -eq 'Restored' -and
        (Get-Item -LiteralPath $pendingRestore.Hosts).LastWriteTimeUtc -eq
            $writeTimeBeforeRetry
    ) 'Restore retry rewrote hosts or did not finish only the pending flush'
    Assert-ExactOriginal $pendingRestore

    $partialAppend = New-HostsFixture 'partial-append-recovery'
    Assert-Throws {
        Invoke-HostsTool $partialAppend Apply @{
            TestFailurePoint = 'DuringManagedAppend'
            LeaveInterruptedForTest = $true
        }
    } 'during managed append' (
        'partial managed append interruption was not injected')
    $partialAppendStatus = Invoke-HostsTool $partialAppend Status
    Assert-True (
        $partialAppendStatus.State -eq 'Conflict' -and
        $partialAppendStatus.ReceiptState -eq 'Prepared'
    ) 'partial append did not retain its Prepared authority'
    $partialAppendRetry = Invoke-HostsTool $partialAppend Apply
    Assert-True (
        $partialAppendRetry.Result -eq 'Applied' -and
        $partialAppendRetry.RecoveredPriorState -eq 'RecoveredRolledBack'
    ) 'partial append was not rolled back before Apply retry'
    Invoke-HostsTool $partialAppend Restore | Out-Null
    Assert-ExactOriginal $partialAppend

    $partialTruncate = New-HostsFixture 'partial-truncate-recovery'
    Invoke-HostsTool $partialTruncate Apply | Out-Null
    Assert-Throws {
        Invoke-HostsTool $partialTruncate Restore @{
            TestFailurePoint = 'DuringRestoreTruncate'
            LeaveInterruptedForTest = $true
        }
    } 'during Restore truncation' (
        'partial Restore truncation interruption was not injected')
    $partialTruncateStatus = Invoke-HostsTool $partialTruncate Status
    Assert-True (
        $partialTruncateStatus.State -eq 'Conflict' -and
        $partialTruncateStatus.ReceiptState -eq 'Restoring'
    ) 'partial truncation did not retain its Restoring authority'
    $partialTruncateRetry = Invoke-HostsTool $partialTruncate Restore
    Assert-True (
        $partialTruncateRetry.Result -eq 'AlreadyRestored' -and
        $partialTruncateRetry.RecoveredInterruptedWrite -and
        $partialTruncateRetry.Completion -eq 'Restored'
    ) 'partial Restore truncation did not recover through exact backup'
    Assert-ExactOriginal $partialTruncate

    $rollbackGap = New-HostsFixture 'rollback-receipt-gap'
    Assert-Throws {
        Invoke-HostsTool $rollbackGap Apply @{
            TestFailurePoint = 'AfterRollbackBytesBeforeReceipt'
            LeaveInterruptedForTest = $true
        }
    } 'before PendingDnsFlush receipt' (
        'rollback byte/receipt interruption was not injected')
    $rollbackGapStatus = Invoke-HostsTool $rollbackGap Status
    Assert-True (
        $rollbackGapStatus.State -eq 'Absent' -and
        $rollbackGapStatus.ReceiptState -eq 'Prepared'
    ) 'rollback gap did not retain exact original plus Prepared receipt'
    $rollbackGapRetry = Invoke-HostsTool $rollbackGap Apply
    Assert-True (
        $rollbackGapRetry.Result -eq 'Applied' -and
        $rollbackGapRetry.RecoveredPriorState -eq 'RecoveredRolledBack'
    ) 'rollback gap was archived without required DNS flush'
    Invoke-HostsTool $rollbackGap Restore | Out-Null
    Assert-ExactOriginal $rollbackGap

    $restoreGap = New-HostsFixture 'restore-receipt-gap'
    Invoke-HostsTool $restoreGap Apply | Out-Null
    Assert-Throws {
        Invoke-HostsTool $restoreGap Restore @{
            TestFailurePoint = 'AfterRestoreBytesBeforeReceipt'
            LeaveInterruptedForTest = $true
        }
    } 'before PendingDnsFlush receipt' (
        'Restore byte/receipt interruption was not injected')
    $restoreGapStatus = Invoke-HostsTool $restoreGap Status
    Assert-True (
        $restoreGapStatus.State -eq 'Absent' -and
        $restoreGapStatus.ReceiptState -eq 'Restoring'
    ) 'Restore gap did not retain exact original plus Restoring receipt'
    $restoreGapRetry = Invoke-HostsTool $restoreGap Restore
    Assert-True (
        $restoreGapRetry.Result -eq 'AlreadyRestored' -and
        $restoreGapRetry.RecoveredInterruptedWrite
    ) 'Restore gap did not flush before receipt archival'
    Assert-ExactOriginal $restoreGap

    $preparedOriginal = New-HostsFixture 'prepared-original'
    Assert-Throws {
        Invoke-HostsTool $preparedOriginal Apply @{
            TestFailurePoint = 'AfterPrepared'
            LeaveInterruptedForTest = $true
        }
    } 'after Prepared' (
        'Prepared-original interruption was not injected')
    $preparedOriginalStatus =
        Invoke-HostsTool $preparedOriginal Status
    Assert-True (
        $preparedOriginalStatus.State -eq 'Absent' -and
        $preparedOriginalStatus.ReceiptState -eq 'Prepared'
    ) 'Prepared-original interruption state was not retained'
    $recoveredApply = Invoke-HostsTool $preparedOriginal Apply
    Assert-True (
        $recoveredApply.Result -eq 'Applied' -and
        $recoveredApply.RecoveredPriorState -eq
            'RecoveredRolledBack'
    ) 'Prepared-original did not reconcile before retry'
    Invoke-HostsTool $preparedOriginal Restore | Out-Null
    Assert-ExactOriginal $preparedOriginal
    Assert-True (
        @(Get-ReceiptHistory $preparedOriginal 'RolledBack').Count -eq 1
    ) 'Prepared-original recovery was not archived as RolledBack'

    $preparedManaged = New-HostsFixture 'prepared-managed'
    Assert-Throws {
        Invoke-HostsTool $preparedManaged Apply @{
            TestFailurePoint = 'AfterManagedWrite'
            LeaveInterruptedForTest = $true
        }
    } 'after managed write' (
        'Prepared-managed interruption was not injected')
    $preparedManagedStatus = Invoke-HostsTool $preparedManaged Status
    Assert-True (
        $preparedManagedStatus.State -eq 'InstalledExact' -and
        $preparedManagedStatus.ReceiptState -eq 'Prepared'
    ) 'Prepared-managed interruption state was not retained'
    $recoveredRestore =
        Invoke-HostsTool $preparedManaged Restore
    Assert-True (
        $recoveredRestore.Result -eq 'Restored'
    ) 'Prepared-managed did not reconcile into Restore'
    Assert-ExactOriginal $preparedManaged

    $installedCrash = New-HostsFixture 'installed-restore-crash'
    Invoke-HostsTool $installedCrash Apply | Out-Null
    $installedReceipt =
        Get-Content $installedCrash.Receipt -Raw | ConvertFrom-Json
    [IO.File]::WriteAllBytes(
        $installedCrash.Hosts,
        [IO.File]::ReadAllBytes([string]$installedReceipt.backupPath))
    $alreadyRestored = Invoke-HostsTool $installedCrash Restore
    Assert-True (
        $alreadyRestored.Result -eq 'AlreadyRestored'
    ) 'InstalledExact/exact-original crash state was not reconciled'
    Assert-ExactOriginal $installedCrash

    $tamper = New-HostsFixture 'receipt-backup-tamper'
    Invoke-HostsTool $tamper Apply | Out-Null
    $appliedHash = (
        Get-FileHash $tamper.Hosts -Algorithm SHA256).Hash
    $receiptBytes = [IO.File]::ReadAllBytes($tamper.Receipt)
    try {
        $record =
            Get-Content $tamper.Receipt -Raw | ConvertFrom-Json
        $record.intendedAppliedSha256 = '0' * 64
        [IO.File]::WriteAllText(
            $tamper.Receipt,
            ($record | ConvertTo-Json -Depth 6),
            [Text.UTF8Encoding]::new($false))
        Assert-Throws {
            Invoke-HostsTool $tamper Restore
        } 'intended hash is not authoritative' (
            'Restore accepted a modified active receipt')
        Assert-True (
            (Get-FileHash $tamper.Hosts -Algorithm SHA256).Hash -ceq
                $appliedHash
        ) 'receipt-tamper rejection changed hosts'
        [IO.File]::WriteAllBytes($tamper.Receipt, $receiptBytes)

        $wrongReader =
            Get-Content $tamper.Receipt -Raw | ConvertFrom-Json
        $wrongReader.readerSid = 'S-1-5-21-1-2-3-1001'
        [IO.File]::WriteAllText(
            $tamper.Receipt,
            ($wrongReader | ConvertTo-Json -Depth 6),
            [Text.UTF8Encoding]::new($false))
        Assert-Throws {
            Invoke-HostsTool $tamper Restore
        } 'malformed or outside policy' (
            'Restore accepted a receipt issued to another SID')
        Assert-True (
            (Get-FileHash $tamper.Hosts -Algorithm SHA256).Hash -ceq
                $appliedHash
        ) 'wrong-SID receipt rejection changed hosts'
        [IO.File]::WriteAllBytes($tamper.Receipt, $receiptBytes)
    }
    finally {
        [Array]::Clear($receiptBytes, 0, $receiptBytes.Length)
    }

    $validRecord =
        Get-Content $tamper.Receipt -Raw | ConvertFrom-Json
    $backupPath = [string]$validRecord.backupPath
    $backupBytes = [IO.File]::ReadAllBytes($backupPath)
    try {
        $damaged = [byte[]]$backupBytes.Clone()
        $damaged[0] = $damaged[0] -bxor 1
        [IO.File]::WriteAllBytes($backupPath, $damaged)
        Assert-Throws {
            Invoke-HostsTool $tamper Restore
        } 'backup changed after capture' (
            'Restore accepted a modified hosts backup')
        Assert-True (
            (Get-FileHash $tamper.Hosts -Algorithm SHA256).Hash -ceq
                $appliedHash
        ) 'backup-tamper rejection changed hosts'
        [IO.File]::WriteAllBytes($backupPath, $backupBytes)
    }
    finally {
        [Array]::Clear($backupBytes, 0, $backupBytes.Length)
    }
    Invoke-HostsTool $tamper Restore | Out-Null
    Assert-ExactOriginal $tamper

    $conflict = New-HostsFixture 'foreign-conflict'
    [IO.File]::AppendAllText(
        $conflict.Hosts,
        "192.0.2.1 login.reborn.test`r`n",
        [Text.Encoding]::ASCII)
    $conflictStatus = Invoke-HostsTool $conflict Status
    Assert-True (
        $conflictStatus.State -eq 'Conflict'
    ) 'foreign mapping was not classified Conflict'
    Assert-Throws {
        Invoke-HostsTool $conflict Apply
    } 'requires Absent state' (
        'Apply accepted a foreign managed-name mapping')

    [pscustomobject]@{
        Result = 'Passed'
        TwoCycles = $true
        FailedApplyRetry = $true
        PreparedOriginalRecovery = $true
        PreparedManagedRecovery = $true
        InstalledRestoreCrashRecovery = $true
        ReceiptTamperRefusal = $true
        WrongReaderSidRefusal = $true
        BackupTamperRefusal = $true
        UnrelatedChangeRefusal = $true
        ProductionReceiptPathBinding = $true
        PendingRollbackDnsRetry = $true
        PendingRestoreDnsRetry = $true
        PartialAppendRecovery = $true
        PartialTruncateRecovery = $true
        RollbackReceiptGapRecovery = $true
        RestoreReceiptGapRecovery = $true
        StatusAndWhatIfReadOnly = $true
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temporary = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $temporary,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

& (Join-Path $PSScriptRoot 'TestDevelopmentNetworkHostsHardLinks.ps1')
& (Join-Path $PSScriptRoot 'TestDevelopmentNetworkHostsRuntimeGate.ps1')
& (Join-Path $PSScriptRoot 'TestDevelopmentNetworkHostsAcl.ps1')
