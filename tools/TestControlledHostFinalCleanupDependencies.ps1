[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$trustModulePath =
    Join-Path $PSScriptRoot 'DevelopmentNetworkTrustReceipt.psm1'
Import-Module $trustModulePath -Force
if (-not (Get-Command Read-RebornTrustReceipt -ErrorAction SilentlyContinue)) {
    throw 'Trust-receipt commands were not imported for cleanup.'
}

$module = Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostCleanupAuthorization.psm1'
) -Force -PassThru
if (-not (Get-Command Read-RebornTrustReceipt -ErrorAction SilentlyContinue)) {
    throw (
        'Cleanup authorization unloaded caller trust-receipt commands.')
}
$pathDependencyAvailable = & $module {
    [bool](Get-Command Assert-RebornProtectedRegularFilePath `
        -ErrorAction SilentlyContinue)
}
if (-not $pathDependencyAvailable) {
    throw 'Cleanup authorization did not retain its path-safety dependency.'
}
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostRuntimeLock.psm1'
) -Force
$pathDependencyAfterLockImport = & $module {
    [bool](Get-Command Assert-RebornProtectedRegularFilePath `
        -ErrorAction SilentlyContinue)
}
if (-not $pathDependencyAfterLockImport) {
    throw (
        'Runtime-lock import unloaded cleanup path-safety commands.')
}
foreach ($cleanupModuleName in @(
        'ControlledHostClientInventoryReceipt.psm1',
        'ControlledHostClientRootLease.psm1',
        'ControlledHostRuntimeCleanup.psm1',
        'ControlledHostRuntimeCleanupReceipt.psm1',
        'ControlledHostRuntimeLock.psm1',
        'ControlledHostServerRuntime.psm1',
        'SecureNetworkActivationState.psm1',
        'SecureNetworkOperationLock.psm1'
    )) {
    Import-Module (Join-Path $PSScriptRoot $cleanupModuleName)
}
if (-not (Get-Command Enter-RebornControlledHostDirectoryLease `
        -ErrorAction SilentlyContinue)) {
    throw 'Final cleanup did not import its direct directory-lease dependency.'
}
$manifestTrustSha256 = 'A' * 64

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Copy-Record {
    param([Parameter(Mandatory)][object]$Value)

    $Value | ConvertTo-Json -Depth 8 | ConvertFrom-Json
}

function Invoke-FinalReceiptState {
    param([object]$Trust, [object]$Keys)

    & $module {
        param($TrustRecord, $KeyRecord, $ExpectedTrustSha256)

        Assert-RebornControlledHostFinalCleanupReceiptState `
            $TrustRecord $KeyRecord $ExpectedTrustSha256
    } $Trust $Keys $manifestTrustSha256
}

function Invoke-FinalResourceState {
    param([bool]$Root, [bool]$CurrentKey, [bool]$NextKey)

    & $module {
        param($RootPresent, $CurrentPresent, $NextPresent)

        Assert-RebornControlledHostFinalCleanupResourceAbsence `
            $RootPresent $CurrentPresent $NextPresent
    } $Root $CurrentKey $NextKey
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    $errorText = $null
    try {
        & $Action
    }
    catch {
        $errorText = $_.Exception.Message
    }
    Assert-True (
        $null -ne $errorText -and $errorText -match $Pattern
    ) "$Message; error was: $errorText"
}

$trust = [pscustomobject]@{
    state = 'Removed'
    installedByScript = $true
}
$keys = [pscustomobject]@{
    schemaVersion = 1
    state = 'Removed'
    current = [pscustomobject]@{
        keyName = 'Reborn-Network-Manifest-Development-Current-v1'
        removed = $true
    }
    next = [pscustomobject]@{
        keyName = 'Reborn-Network-Manifest-Development-Next-v1'
        removed = $true
    }
    currentTrustSha256 = $manifestTrustSha256
}

Invoke-FinalReceiptState $trust $keys
Invoke-FinalResourceState $false $false $false

foreach ($state in @('Installed', 'RemovalPending')) {
    $candidate = Copy-Record $trust
    $candidate.state = $state
    Assert-Throws {
        Invoke-FinalReceiptState $candidate $keys
    } 'root receipt in Removed state' (
        "final cleanup accepted trust state $state")
}

foreach ($state in @('Issued', 'RemovalPending')) {
    $candidate = Copy-Record $keys
    $candidate.state = $state
    $candidate.current.removed = $state -eq 'RemovalPending'
    $candidate.next.removed = $false
    Assert-Throws {
        Invoke-FinalReceiptState $trust $candidate
    } 'both keys Removed' (
        "final cleanup accepted manifest-key state $state")
}

$wrongIdentity = Copy-Record $keys
$wrongIdentity.next.keyName = 'Reborn-Network-Manifest-Development-Other-v1'
Assert-Throws {
    Invoke-FinalReceiptState $trust $wrongIdentity
} 'both keys Removed' 'final cleanup accepted a different key identity'

$wrongTrust = Copy-Record $keys
$wrongTrust.currentTrustSha256 = 'B' * 64
Assert-Throws {
    Invoke-FinalReceiptState $trust $wrongTrust
} 'both keys Removed' 'final cleanup accepted a different trust binding'

Assert-Throws {
    Invoke-FinalResourceState $true $false $false
} 'root to be absent' 'final cleanup accepted an installed issued root'
Assert-Throws {
    Invoke-FinalResourceState $false $true $false
} 'both exact issued' 'final cleanup accepted the current issued key'
Assert-Throws {
    Invoke-FinalResourceState $false $false $true
} 'both exact issued' 'final cleanup accepted the next issued key'

[pscustomobject]@{
    Result = 'Passed'
    InstalledTrustRejected = $true
    PendingTrustRejected = $true
    IssuedKeysRejected = $true
    PendingKeysRejected = $true
    WrongIdentityRejected = $true
    InstalledResourcesRejected = $true
    RemovedAndAbsentAccepted = $true
}
