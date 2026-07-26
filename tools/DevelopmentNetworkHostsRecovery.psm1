Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsReceipt.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'DevelopmentNetworkHostsFileIO.psm1'
)

function Read-RebornDevelopmentHostsActiveReceipt {
    param(
        [string]$ReceiptPath,
        [string]$HostsPath,
        [string]$BeginMarker,
        [string]$Mapping,
        [string]$EndMarker
    )

    Read-RebornHostsReceipt `
        $ReceiptPath $HostsPath $BeginMarker $Mapping $EndMarker
}

function Get-RebornDevelopmentHostsStatus {
    param(
        [string]$HostsPath,
        [string]$BeginMarker,
        [string]$Mapping,
        [string]$EndMarker,
        [string[]]$ManagedNames
    )

    Get-RebornDevelopmentHostsState `
        $HostsPath $BeginMarker $Mapping $EndMarker $ManagedNames
}

function Resolve-RebornDevelopmentHostsReceipt {
    param(
        [string]$ReceiptPath,
        [string]$HostsPath,
        [string]$BeginMarker,
        [string]$Mapping,
        [string]$EndMarker,
        [string[]]$ManagedNames
    )

    if (-not (Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) {
        return [pscustomobject]@{
            State = 'None'
            Loaded = $null
            HistoryPath = $null
        }
    }

    $loaded = Read-RebornDevelopmentHostsActiveReceipt `
        $ReceiptPath $HostsPath $BeginMarker $Mapping $EndMarker
    $record = $loaded.Record
    $status = Get-RebornDevelopmentHostsStatus `
        $HostsPath $BeginMarker $Mapping $EndMarker $ManagedNames
    $isOriginal = (
        $status.State -eq 'Absent' -and
        $status.Sha256 -ceq [string]$record.originalSha256)
    $isManaged = (
        $status.State -eq 'InstalledExact' -and
        $status.Sha256 -ceq [string]$record.intendedAppliedSha256)
    $isInterruptedPrefix =
        Test-RebornHostsInterruptedManagedPrefix `
            $HostsPath $loaded.BackupPath `
            $BeginMarker $Mapping $EndMarker

    switch ([string]$record.state) {
        'Prepared' {
            if ($isOriginal) {
                return [pscustomobject]@{
                    State = 'PreparedOriginalPendingFlush'
                    Loaded = $loaded
                    HistoryPath = $null
                    BytesAlreadyRestored = $true
                }
            }
            if ($isManaged) {
                $record.state = 'InstalledExact'
                $record.appliedSha256 = $record.intendedAppliedSha256
                $record.appliedUtc =
                    [DateTimeOffset]::UtcNow.ToString('O')
                Write-RebornHostsReceiptAtomic $record $loaded.Path
                return [pscustomobject]@{
                    State = 'InstalledExact'
                    Loaded = Read-RebornDevelopmentHostsActiveReceipt `
                        $ReceiptPath $HostsPath `
                        $BeginMarker $Mapping $EndMarker
                    HistoryPath = $null
                }
            }
            if ($isInterruptedPrefix) {
                return [pscustomobject]@{
                    State = 'PreparedPartial'
                    Loaded = $loaded
                    HistoryPath = $null
                }
            }
        }
        'InstalledExact' {
            if ($isManaged) {
                return [pscustomobject]@{
                    State = 'InstalledExact'
                    Loaded = $loaded
                    HistoryPath = $null
                }
            }
            if ($isOriginal) {
                return [pscustomobject]@{
                    State = 'InstalledOriginalPendingFlush'
                    Loaded = $loaded
                    HistoryPath = $null
                    BytesAlreadyRestored = $true
                }
            }
        }
        'Restoring' {
            if ($isManaged -or $isInterruptedPrefix -or $isOriginal) {
                return [pscustomobject]@{
                    State = 'RestoreInterrupted'
                    Loaded = $loaded
                    HistoryPath = $null
                    BytesAlreadyRestored = $isOriginal
                }
            }
        }
        'PendingDnsFlush' {
            if ($isOriginal) {
                return [pscustomobject]@{
                    State = 'PendingDnsFlush'
                    Loaded = $loaded
                    HistoryPath = $null
                }
            }
        }
        { $_ -in @('RolledBack', 'Restored') } {
            if ($isOriginal) {
                $history = Move-RebornHostsReceiptToHistory `
                    $loaded.Path $record.state
                return [pscustomobject]@{
                    State = "Recovered$($record.state)"
                    Loaded = $null
                    HistoryPath = $history
                }
            }
        }
    }

    throw (
        'Active hosts receipt and hosts bytes do not form a verified ' +
        'recoverable state; refusing any mutation.')
}

function Restore-RebornDevelopmentHostsOriginal {
    param(
        [object]$Loaded,
        [string[]]$AcceptedCurrentSha256,
        [string]$HostsPath,
        [string]$BeginMarker,
        [string]$Mapping,
        [string]$EndMarker,
        [string[]]$ManagedNames
    )

    $original = [IO.File]::ReadAllBytes($Loaded.BackupPath)
    try {
        if ((Get-RebornHostsByteSha256 $original) -cne
                [string]$Loaded.Record.originalSha256) {
            throw 'Hosts backup changed immediately before Restore.'
        }
        Write-RebornHostsBytesLocked `
            $HostsPath $original $AcceptedCurrentSha256
    }
    finally {
        [Array]::Clear($original, 0, $original.Length)
    }
    $restored = Get-RebornDevelopmentHostsStatus `
        $HostsPath $BeginMarker $Mapping $EndMarker $ManagedNames
    if (
        $restored.State -ne 'Absent' -or
        $restored.Sha256 -cne
            [string]$Loaded.Record.originalSha256
    ) {
        throw 'Exact hosts rollback verification failed.'
    }
    return $restored
}

Export-ModuleMember -Function @(
    'Read-RebornDevelopmentHostsActiveReceipt',
    'Resolve-RebornDevelopmentHostsReceipt',
    'Restore-RebornDevelopmentHostsOriginal'
)
