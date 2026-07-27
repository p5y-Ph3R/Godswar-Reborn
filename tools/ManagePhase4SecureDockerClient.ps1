[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply', 'Restore')]
    [string]$Mode = 'Status',

    [string]$CampaignRoot,

    [switch]$AllowMutation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'Phase4SecureDockerClientCampaign.psm1',
    'Phase4SecureDockerClientRuntime.psm1',
    'Phase4SecureDockerClientBundle.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkActivationState.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientMutableOutput.psm1'
) -Force

$activationModulePath =
    Join-Path $PSScriptRoot 'SecureNetworkActivationState.psm1'
$pins = Get-RebornPhase4SecureDockerPins
$CampaignRoot = Resolve-RebornPhase4ManagerCampaignRoot `
    $CampaignRoot $pins
$bundleTool = Join-Path $PSScriptRoot 'InstallSecureNetworkBundle.ps1'
$hostsTool = Join-Path $PSScriptRoot 'ManageDevelopmentNetworkHosts.ps1'
$bundleBackupRoot = Join-Path $CampaignRoot 'bundle-backups'

function Test-EmbeddedDevelopmentRoot {
    $property = $pins.PSObject.Properties['ClientTlsTrustMode']
    return $null -ne $property -and
        [string]$property.Value -ceq 'EmbeddedDevelopmentRoot'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-MutationAuthority {
    if (-not $AllowMutation) {
        throw "$Mode requires explicit -AllowMutation."
    }
    if (-not (Test-IsAdministrator)) {
        throw "$Mode requires an elevated console."
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.User.Value -ceq 'S-1-5-18') {
        throw 'Phase 4 client mutation must never run as SYSTEM.'
    }
}

function Assert-OriginClosed {
    $expected = Join-Path $pins.ClientRoot 'Origin.exe'
    $running = @(
        Get-CimInstance Win32_Process -Filter "Name='Origin.exe'" |
            Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.Equals(
                    $expected,
                    [StringComparison]::OrdinalIgnoreCase)
            })
    if ($running.Count) {
        throw 'Disposable Origin.exe must be closed; it will not be terminated.'
    }
}

function Get-Phase4ActivationState {
    # Child transaction scripts intentionally force-reload this module. A
    # fresh local import prevents their script-scope reload from invalidating
    # the campaign's later activation-state read.
    Import-Module $activationModulePath -Force
    return Get-RebornActivationState -Provider Hklm
}

function Get-BundleArguments {
    return Get-RebornPhase4BundleArguments `
        $pins $bundleBackupRoot
}

function Get-ResultObject {
    param(
        [Parameter(Mandatory)][object[]]$Output,
        [Parameter(Mandatory)][string]$Operation
    )

    $matches = @(
        $Output |
            Where-Object {
                $null -ne $_ -and
                $null -ne $_.PSObject.Properties['Result']
            })
    if ($matches.Count -ne 1) {
        throw "$Operation did not return one result authority."
    }
    return $matches[0]
}

function Get-BundleStatus {
    $bundleArguments = Get-BundleArguments
    $output = @(& $bundleTool @bundleArguments -Mode Status)
    $objects = @(
        $output |
            Where-Object {
                $null -ne $_ -and
                $null -ne $_.PSObject.Properties['State']
            })
    if ($objects.Count -ne 1) {
        throw 'Secure bundle Status did not return one state.'
    }
    return $objects[0]
}

function Get-RecoveryBundleState {
    return Get-RebornPhase4LiveBundleRecoveryState $pins
}

function Get-HostsStatus {
    return & $hostsTool -Mode Status
}

function Assert-ActivationState {
    param([UInt64]$ExpectedMode)

    $state = Get-Phase4ActivationState
    if (-not $state.Exists -or
        -not $state.Complete -or
        [UInt64]$state.Mode -ne $ExpectedMode -or
        [UInt64]$state.Environment -ne $pins.ActivationEnvironment -or
        [UInt64]$state.SequenceFloor -ne $pins.ManifestSequence) {
        throw (
            'HKLM activation is not the exact complete state at retained ' +
            "manifest floor $($pins.ManifestSequence).")
    }
    return $state
}

function Get-BackupBaselineNames {
    return @(
        Get-RebornPhase4BundleBackupBaselineNames $bundleBackupRoot)
}

function Update-Campaign {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$State
    )

    $updated = Copy-RebornPhase4CampaignRecord $Record
    $updated.state = $State
    return (
        Write-RebornPhase4CampaignReceipt `
            $updated $CampaignRoot -Pins $pins
    ).Record
}

function Get-ReceiptFileHash {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Campaign authority is absent: $Path"
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Resolve-BundleBackupPath {
    param([Parameter(Mandatory)][object]$Record)

    return Resolve-RebornPhase4BundleBackupPath `
        $Record $bundleBackupRoot $pins
}

function Assert-ReadyForApply {
    Assert-OriginClosed
    Assert-RebornPhase4PinnedInputs $pins | Out-Null
    Assert-RebornPhase4SecureDockerRuntime $pins | Out-Null
    Assert-ActivationState 0 | Out-Null
    $bundle = Get-BundleStatus
    $hosts = Get-HostsStatus
    $root = Get-RebornPhase4RootStatus $pins
    $mutableOutput =
        Get-RebornControlledHostWritableOutputFileState $pins.ClientRoot
    if ($bundle.State -cne 'Stock' -or
        $hosts.State -cne 'Absent' -or
        $hosts.ReceiptExists -or
        $hosts.HostsSha256 -cne $pins.OriginalHostsSha256 -or
        $root.State -cne 'Absent' -or
        $mutableOutput -cne 'Inactive') {
        throw 'Phase 4 Apply prerequisites are not exact stock/absent state.'
    }
}

function Assert-InstalledExact {
    Assert-RebornPhase4SecureDockerRuntime $pins | Out-Null
    Assert-ActivationState 1 | Out-Null
    $bundle = Get-BundleStatus
    $hosts = Get-HostsStatus
    $root = Get-RebornPhase4RootStatus $pins
    $mutableOutput =
        Get-RebornControlledHostWritableOutputFileState $pins.ClientRoot
    $expectedRootState = if (Test-EmbeddedDevelopmentRoot) {
        'Absent'
    }
    else {
        'InstalledExact'
    }
    if ($bundle.State -cne 'InstalledExact' -or
        $hosts.State -cne 'InstalledExact' -or
        $hosts.ReceiptState -cne 'InstalledExact' -or
        $root.State -cne $expectedRootState -or
        $mutableOutput -cne 'Active') {
        throw 'Phase 4 client campaign did not reach exact installed state.'
    }
}

function Assert-RestoredExact {
    param([switch]$RecoverySafe)

    Assert-RebornPhase4SecureDockerRuntime $pins | Out-Null
    Assert-ActivationState 0 | Out-Null
    $bundleState = if ($RecoverySafe) {
        Get-RecoveryBundleState
    }
    else {
        (Get-BundleStatus).State
    }
    $hosts = Get-HostsStatus
    $root = Get-RebornPhase4RootStatus $pins
    $mutableOutput =
        Get-RebornControlledHostWritableOutputFileState $pins.ClientRoot
    if ($bundleState -cne 'Stock' -or
        $hosts.State -cne 'Absent' -or
        $hosts.ReceiptExists -or
        $hosts.HostsSha256 -cne $pins.OriginalHostsSha256 -or
        $root.State -cne 'Absent' -or
        $mutableOutput -cne 'Inactive') {
        throw 'Phase 4 client campaign did not restore exact predecessor state.'
    }
}

function Invoke-CampaignRestore {
    param([Parameter(Mandatory)][object]$Record)

    $current = Update-Campaign $Record 'RestorePending'
    $bundleState = Get-RecoveryBundleState
    if ($bundleState -ne 'Stock') {
        if ($bundleState -ne 'InstalledExact' -and
            $bundleState -ne 'RecoverablePartial') {
            throw "Unsupported bundle recovery state: $bundleState"
        }
        $backupPath = Resolve-BundleBackupPath $current
        $bundleArguments = Get-BundleArguments
        $bundleOutput = @(
            & $bundleTool @bundleArguments `
                -Mode Restore `
                -ApplyBackupPath $backupPath `
                -AllowHklmWrite `
                -Confirm:$false)
        $bundleRestore = Get-ResultObject `
            $bundleOutput 'Secure bundle Restore'
        if ($bundleRestore.Result -cne 'StockFilesRestored') {
            throw 'Secure bundle Restore did not restore stock files.'
        }
    }
    $current.bundleState = 'Restored'
    $current = Update-Campaign $current 'BundleRestored'

    $hosts = Get-HostsStatus
    if ($hosts.State -eq 'InstalledExact') {
        $hostsOutput = @(
            & $hostsTool -Mode Restore `
                -AllowHostsWrite `
                -Confirm:$false)
        $hostsRestore = Get-ResultObject `
            $hostsOutput 'Development hosts Restore'
        if ($hostsRestore.Result -notin @('Restored', 'AlreadyRestored')) {
            throw 'Development hosts Restore did not restore original bytes.'
        }
    } elseif ($hosts.State -ne 'Absent' -or $hosts.ReceiptExists) {
        throw 'Development hosts state is ambiguous during Restore.'
    }
    $current.hostsState = 'Restored'
    $current = Update-Campaign $current 'HostsRestored'

    $root = Get-RebornPhase4RootStatus $pins
    if (Test-EmbeddedDevelopmentRoot) {
        if ($root.State -cne 'Absent') {
            throw 'Embedded-root campaign found unexpected CurrentUser trust.'
        }
        $current.trustState = 'EmbeddedRootReleased'
    }
    else {
        if ($root.State -eq 'InstalledExact') {
            if ($current.trustState -notin @(
                    'PendingInstall', 'Installed', 'RemovalPending')) {
                throw 'Campaign receipt does not authorize root removal.'
            }
            $current.trustState = 'RemovalPending'
            $current = Update-Campaign $current 'HostsRestored'
            Remove-RebornPhase4Root $pins | Out-Null
        } elseif ($root.State -ne 'Absent') {
            throw 'CurrentUser development root is ambiguous during Restore.'
        }
        $current.trustState = 'Removed'
    }
    $current = Update-Campaign $current 'TrustRemoved'
    Disable-RebornControlledHostWritableOutputFile $pins.ClientRoot
    Assert-RestoredExact -RecoverySafe
    return Update-Campaign $current 'Restored'
}

function Get-CampaignStatus {
    Assert-RebornPhase4PinnedInputs $pins | Out-Null
    $docker = Assert-RebornPhase4SecureDockerRuntime $pins
    $activation = Get-Phase4ActivationState
    $bundle = Get-BundleStatus
    $hosts = Get-HostsStatus
    $root = Get-RebornPhase4RootStatus $pins
    $mutableOutput =
        Get-RebornControlledHostWritableOutputFileState $pins.ClientRoot
    $handoff = Read-RebornPhase4CampaignReceipt `
        $CampaignRoot -Pins $pins
    $installedRootState = if (Test-EmbeddedDevelopmentRoot) {
        'Absent'
    }
    else {
        'InstalledExact'
    }
    $campaignState = if (
        $bundle.State -eq 'Stock' -and
        $hosts.State -eq 'Absent' -and
        -not $hosts.ReceiptExists -and
        $root.State -eq 'Absent' -and
        $mutableOutput -eq 'Inactive' -and
        [UInt64]$activation.Mode -eq 0
    ) {
        if ($null -ne $handoff -and
            $handoff.Record.state -eq 'Restored') {
            'Restored'
        } else {
            'Ready'
        }
    } elseif (
        $bundle.State -eq 'InstalledExact' -and
        $hosts.State -eq 'InstalledExact' -and
        $root.State -eq $installedRootState -and
        $mutableOutput -eq 'Active' -and
        [UInt64]$activation.Mode -eq 1 -and
        $null -ne $handoff -and
        $handoff.Record.state -eq 'InstalledExact'
    ) {
        'InstalledExact'
    } else {
        'RecoveryRequired'
    }
    return [pscustomobject]@{
        State = $campaignState
        DockerState = $docker.State
        BundleState = $bundle.State
        HostsState = $hosts.State
        RootState = $root.State
        TlsTrustMode = if (Test-EmbeddedDevelopmentRoot) {
            'EmbeddedDevelopmentRoot'
        }
        else {
            'CurrentUserRoot'
        }
        MutableOutputState = $mutableOutput
        ActivationMode = [UInt64]$activation.Mode
        ActivationEnvironment = [UInt64]$activation.Environment
        SequenceFloor = [UInt64]$activation.SequenceFloor
        ManifestSequence = $pins.ManifestSequence
        HandoffState = if ($null -eq $handoff) {
            'Absent'
        } else {
            $handoff.Record.state
        }
        HandoffPath = if ($null -eq $handoff) {
            ''
        } else {
            $handoff.Path
        }
    }
}

if ($Mode -eq 'Status') {
    Get-CampaignStatus
    return
}

Assert-MutationAuthority
Assert-OriginClosed
$operationLock = $null
try {
    $operationLock = Enter-RebornSecureNetworkOperationLock `
        -Name 'phase4-secure-docker-client'

    if ($Mode -eq 'Apply') {
        $existing = Get-CampaignStatus
        if ($existing.State -eq 'InstalledExact') {
            [pscustomobject]@{
                Result = 'AlreadyInstalledExact'
                State = $existing.State
                HandoffPath = $existing.HandoffPath
            }
            return
        }
        if ($existing.State -eq 'RecoveryRequired') {
            throw 'Phase 4 campaign requires Restore before a new Apply.'
        }
        Assert-ReadyForApply
        if (-not $PSCmdlet.ShouldProcess(
                $pins.ClientRoot,
                'Install exact secure client routing for secure Docker')) {
            return
        }

        Resolve-RebornPhase4CampaignRoot $CampaignRoot -Create |
            Out-Null
        $backupBaselineNames = @(Get-BackupBaselineNames)
        $record = New-RebornPhase4CampaignRecord `
            -BackupBaselineNames $backupBaselineNames `
            -Pins $pins
        $record = (
            Write-RebornPhase4CampaignReceipt `
                $record $CampaignRoot -Pins $pins
        ).Record
        try {
            Enable-RebornControlledHostWritableOutputFile $pins.ClientRoot
            if (Test-EmbeddedDevelopmentRoot) {
                if ((Get-RebornPhase4RootStatus $pins).State -cne 'Absent') {
                    throw 'Embedded-root Apply requires an absent trust-store root.'
                }
                $record.trustState = 'EmbeddedRootPinned'
            }
            else {
                Add-RebornPhase4Root $pins
                $record.trustState = 'Installed'
            }
            $record = Update-Campaign $record 'TrustInstalled'

            $record.hostsState = 'Pending'
            $record = Update-Campaign $record 'HostsPending'
            $hostsOutput = @(
                & $hostsTool -Mode Apply `
                    -AllowHostsWrite `
                    -Confirm:$false)
            $hostsApply = Get-ResultObject `
                $hostsOutput 'Development hosts Apply'
            if ($hostsApply.Result -notin @(
                    'Applied', 'AlreadyInstalledExact')) {
                throw 'Development hosts Apply did not install exact mappings.'
            }
            $record.hostsState = 'Installed'
            $record.hostsReceiptPath = [string]$hostsApply.ReceiptPath
            $record.hostsReceiptSha256 =
                Get-ReceiptFileHash $record.hostsReceiptPath
            $record.hostsBackupPath = [string]$hostsApply.BackupPath
            $record.hostsBackupSha256 =
                Get-ReceiptFileHash $record.hostsBackupPath
            $record = Update-Campaign $record 'HostsApplied'

            $record.bundleState = 'Pending'
            $record = Update-Campaign $record 'BundlePending'
            $bundleArguments = Get-BundleArguments
            $bundleOutput = @(
                & $bundleTool @bundleArguments `
                    -Mode Apply `
                    -AllowHklmWrite `
                    -ControlledHostSocketChecks `
                    -Confirm:$false)
            $bundleApply = Get-ResultObject `
                $bundleOutput 'Secure bundle Apply'
            if ($bundleApply.Result -cne 'InstalledExact') {
                throw 'Secure bundle Apply did not install exact client files.'
            }
            $record.bundleState = 'Installed'
            $record.bundleBackupPath =
                [string]$bundleApply.BackupPath
            $record.bundleReceiptSha256 = Get-ReceiptFileHash (
                Join-Path $record.bundleBackupPath 'receipt.json')
            $record.bundleChecksumSha256 = Get-ReceiptFileHash (
                Join-Path $record.bundleBackupPath 'receipt.sha256')
            $record = Update-Campaign $record 'BundleApplied'

            Assert-InstalledExact
            $record = Update-Campaign $record 'InstalledExact'
            [pscustomobject]@{
                Result = 'InstalledExact'
                State = $record.state
                CampaignId = $record.campaignId
                HandoffPath = (
                    Read-RebornPhase4CampaignReceipt `
                        $CampaignRoot -Pins $pins).Path
                BundleBackupPath = $record.bundleBackupPath
                SequenceFloor = $pins.ManifestSequence
                DockerState = 'HealthyExact'
            }
        }
        catch {
            $primary = $_
            try {
                $latest = Read-RebornPhase4CampaignReceipt `
                    $CampaignRoot -Pins $pins
                if ($null -ne $latest) {
                    Invoke-CampaignRestore $latest.Record | Out-Null
                }
            }
            catch {
                throw (
                    "Phase 4 Apply failed: $($primary.Exception.Message) " +
                    "Automatic Restore also failed: $($_.Exception.Message)")
            }
            throw $primary
        }
        return
    }

    $handoff = Read-RebornPhase4CampaignReceipt `
        $CampaignRoot -Pins $pins
    if ($null -eq $handoff) {
        $current = Get-CampaignStatus
        if ($current.State -ne 'Ready') {
            throw 'Restore requires the protected checksummed campaign handoff.'
        }
        [pscustomobject]@{
            Result = 'AlreadyRestored'
            State = $current.State
            SequenceFloor = $current.SequenceFloor
            DockerState = $current.DockerState
        }
        return
    }
    if ([string]$handoff.Record.state -ceq 'Restored') {
        Assert-RestoredExact -RecoverySafe
        $activation = Assert-ActivationState 0
        [pscustomobject]@{
            Result = 'AlreadyRestored'
            State = 'Restored'
            SequenceFloor = [UInt64]$activation.SequenceFloor
            DockerState = 'HealthyExact'
        }
        return
    }
    $restoreDescription = if (Test-EmbeddedDevelopmentRoot) {
        'Restore exact stock client and hosts'
    }
    else {
        'Restore exact stock client, hosts, and CurrentUser trust'
    }
    if (-not $PSCmdlet.ShouldProcess(
            $pins.ClientRoot,
            $restoreDescription)) {
        return
    }
    $restored = Invoke-CampaignRestore $handoff.Record
    [pscustomobject]@{
        Result = 'Restored'
        State = $restored.state
        CampaignId = $restored.campaignId
        SequenceFloor = $pins.ManifestSequence
        DockerState = 'HealthyExact'
    }
}
finally {
    if ($null -ne $operationLock -and
        $null -ne $operationLock.Stream) {
        $operationLock.Stream.Dispose()
    }
}
