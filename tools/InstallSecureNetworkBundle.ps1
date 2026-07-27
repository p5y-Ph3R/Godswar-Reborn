[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply', 'Restore')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$CandidatePath = (
        Join-Path $PSScriptRoot `
            '..\client\network-shim\bin\Release\Win32\Net.dll'),

    [string]$CandidateOriginPath,

    [ValidateScript({
        [string]::IsNullOrEmpty($_) -or
        $_ -cmatch '^[0-9A-Fa-f]{64}$'
    })]
    [string]$ExpectedCandidateOriginSha256,

    [ValidateScript({
        [string]::IsNullOrEmpty($_) -or
        $_ -cmatch '^[0-9A-Fa-f]{64}$'
    })]
    [string]$ExpectedOriginSha256,

    [string]$ManifestPath,

    [string]$TrustPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCandidateSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedChecksSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedTrustSha256,

    [string]$BackupRoot = (
        Join-Path (
            [Environment]::GetFolderPath('CommonApplicationData')
        ) 'RebornSecureNetworkBackups'),

    [string]$ApplyBackupPath,

    [string]$ClientInventoryReceiptPath,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedClientInventoryReceiptSha256,

    [switch]$AllowHklmWrite,

    [switch]$ControlledHostSocketChecks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$supportedOriginSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$supportedLegacySha256 =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$expectedOrigin = if (
    [string]::IsNullOrWhiteSpace($ExpectedOriginSha256)) {
    $supportedOriginSha256
}
else {
    $ExpectedOriginSha256.ToUpperInvariant()
}
if ($expectedOrigin -cne $supportedOriginSha256) {
    throw 'Expected stock Origin SHA-256 is unsupported.'
}

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTransaction.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostClientInventoryReceipt.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostRuntimeLock.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkNativeOfflineGate.psm1'
) -Force

$requiredDependencyCommands = @(
    'Assert-RebornControlledHostClientInventoryReceipt'
    'Assert-RebornControlledHostClientPostInventoryReboot'
    'Assert-RebornProtectedDirectoryPath'
    'Enter-RebornControlledHostRuntimeSetLock'
    'Enter-RebornSecureNetworkOperationLock'
    'Exit-RebornControlledHostRuntimeSetLock'
    'Exit-RebornSecureNetworkOperationLock'
    'Get-RebornSecureBundleStatus'
    'Initialize-RebornProtectedDirectoryPath'
    'Invoke-RebornSecureBundleNativeOfflineGate'
    'Invoke-RebornSecureBundleApply'
    'Invoke-RebornSecureBundleRestore'
    'New-RebornSecureBundlePolicy'
    'Read-RebornControlledHostClientInventoryReceipt'
)
foreach ($commandName in $requiredDependencyCommands) {
    if ($null -eq (Get-Command $commandName `
            -CommandType Function -ErrorAction SilentlyContinue)) {
        throw "Secure bundle dependency is not in script scope: $commandName"
    }
}

function Assert-OriginClosed {
    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed for a secure bundle mutation.'
    }
}

function Enter-SecureBundleMutation {
    $mutex = [Threading.Mutex]::new(
        $false,
        'Local\RebornSecureNetworkBundleMutationV1')
    try {
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
        }
        if (-not $acquired) {
            throw 'Another secure network bundle mutation is active.'
        }
        return $mutex
    }
    catch {
        $mutex.Dispose()
        throw
    }
}

$candidate = [IO.Path]::GetFullPath($CandidatePath)
$pairedOrigin =
    -not [string]::IsNullOrWhiteSpace($ExpectedCandidateOriginSha256)
$hasCandidateOriginPath =
    -not [string]::IsNullOrWhiteSpace($CandidateOriginPath)
if (($hasCandidateOriginPath -and -not $pairedOrigin) -or
    ($Mode -ne 'Restore' -and
        $pairedOrigin -ne $hasCandidateOriginPath)) {
    throw (
        'Candidate Origin path and expected SHA-256 must be supplied ' +
        'together.')
}
$candidateOrigin = if ($hasCandidateOriginPath) {
    [IO.Path]::GetFullPath($CandidateOriginPath)
} else {
    ''
}
if ($Mode -ne 'Restore' -and
    ([string]::IsNullOrWhiteSpace($ManifestPath) -or
        [string]::IsNullOrWhiteSpace($TrustPath))) {
    throw "$Mode requires -ManifestPath and -TrustPath."
}
$manifest = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    ''
} else {
    [IO.Path]::GetFullPath($ManifestPath)
}
$trust = if ([string]::IsNullOrWhiteSpace($TrustPath)) {
    ''
} else {
    [IO.Path]::GetFullPath($TrustPath)
}
$client = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
$backup = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
$policy = New-RebornSecureBundlePolicy `
    -OriginSha256 $expectedOrigin `
    -LegacyNetSha256 $supportedLegacySha256 `
    -CandidateNetSha256 (
        $ExpectedCandidateSha256.ToUpperInvariant()) `
    -ManifestSha256 (
        $ExpectedManifestSha256.ToUpperInvariant()) `
    -ManifestTrustSha256 (
        $ExpectedTrustSha256.ToUpperInvariant()) `
    -CandidateOriginSha256 $(
        if ($pairedOrigin) {
            $ExpectedCandidateOriginSha256.ToUpperInvariant()
        } else {
            $supportedOriginSha256
        })
$controlledClient =
    $client.Equals(
        'C:\RebornNetworkAcceptanceClient',
        [StringComparison]::OrdinalIgnoreCase)
$inventoryReceipt = $null
if ($controlledClient) {
    if ([string]::IsNullOrWhiteSpace($ClientInventoryReceiptPath) -or
        [string]::IsNullOrWhiteSpace(
            $ExpectedClientInventoryReceiptSha256)) {
        throw (
            'The controlled-host client requires its protected inventory ' +
            'receipt path and SHA-256.')
    }
    $inventoryReceipt =
        Read-RebornControlledHostClientInventoryReceipt `
            $ClientInventoryReceiptPath `
            $ExpectedClientInventoryReceiptSha256
}

if ($Mode -eq 'Status') {
    $bundleStatus = Get-RebornSecureBundleStatus `
        -Policy $policy `
        -ClientRoot $client `
        -CandidatePath $candidate `
        -ManifestPath $manifest `
        -TrustPath $trust `
        -StateProvider Hklm `
        -CandidateOriginPath $candidateOrigin
    if ($controlledClient -and
        $bundleStatus.State -in @('Stock', 'InstalledExact')) {
        $inventoryMode = if (
            $bundleStatus.State -eq 'Stock'
        ) { 'Stock' } else { 'InstalledExact' }
        Assert-RebornControlledHostClientInventoryReceipt `
            $inventoryReceipt `
            $client `
            $inventoryMode `
            $policy.CandidateNetSha256 `
            $policy.LegacyNetSha256 `
            $policy.ManifestSha256 `
            $(if ($pairedOrigin) {
                $policy.CandidateOriginSha256
            } else {
                $null
            }) | Out-Null
    }
    $bundleStatus
    return
}

if (-not $AllowHklmWrite) {
    throw (
        "$Mode requires explicit -AllowHklmWrite and elevation; " +
        'offline transaction tests use the module directly.'
    )
}

if ($Mode -eq 'Apply') {
    $preflight = Get-RebornSecureBundleStatus `
        -Policy $policy `
        -ClientRoot $client `
        -CandidatePath $candidate `
        -ManifestPath $manifest `
        -TrustPath $trust `
        -StateProvider Hklm `
        -CandidateOriginPath $candidateOrigin
    if ($preflight.State -ne 'Stock') {
        throw "Secure bundle Apply requires exact Stock state, got $($preflight.State)."
    }

    if (-not $PSCmdlet.ShouldProcess(
            $client,
            'Verify and install signed secure network bundle')) {
        return
    }

    $operationLock =
        Enter-RebornSecureNetworkOperationLock -Name 'secure-bundle'
    $runtimeSetLock = $null
    try {
        $runtimeSetLock =
            Enter-RebornControlledHostRuntimeSetLock
        if ($controlledClient) {
            $rebootGate =
                Assert-RebornControlledHostClientPostInventoryReboot `
                    $ClientInventoryReceiptPath `
                    $ExpectedClientInventoryReceiptSha256
            $inventoryReceipt = $rebootGate.Receipt
        }
        $lockedPreflight = Get-RebornSecureBundleStatus `
            -Policy $policy `
            -ClientRoot $client `
            -CandidatePath $candidate `
            -ManifestPath $manifest `
            -TrustPath $trust `
            -StateProvider Hklm `
            -CandidateOriginPath $candidateOrigin
        if ($lockedPreflight.State -ne 'Stock') {
            throw (
                'Secure bundle state changed before the operation lock; ' +
                "got $($lockedPreflight.State).")
        }

        $mutation = Enter-SecureBundleMutation
        try {
            Assert-OriginClosed
            Assert-RebornProtectedDirectoryPath `
                $client 'ClientRoot' -ProtectContents | Out-Null
            if ($controlledClient) {
                Assert-RebornControlledHostClientInventoryReceipt `
                    $inventoryReceipt $client Stock | Out-Null
            }
            $backup = Initialize-RebornProtectedDirectoryPath `
                $backup 'BackupRoot'
            Invoke-RebornSecureBundleNativeOfflineGate `
                -Candidate $candidate `
                -CandidateOrigin $(if ($pairedOrigin) {
                    $candidateOrigin
                } else {
                    Join-Path $client 'Origin.exe'
                }) `
                -StockNet (Join-Path $client 'Net.dll') `
                -Manifest $manifest `
                -ScratchRoot (Join-Path $backup '.staging') `
                -ExpectedCandidate $policy.CandidateNetSha256 `
                -ExpectedCandidateOrigin `
                    $policy.CandidateOriginSha256 `
                -ExpectedStockNet $policy.LegacyNetSha256 `
                -ExpectedChecks $ExpectedChecksSha256.ToUpperInvariant() `
                -ExpectedManifest $policy.ManifestSha256 `
                -IncludeSockets:$ControlledHostSocketChecks

            Assert-OriginClosed
            $applyResult = Invoke-RebornSecureBundleApply `
                -Policy $policy `
                -ClientRoot $client `
                -CandidatePath $candidate `
                -ManifestPath $manifest `
                -TrustPath $trust `
                -BackupRoot $backup `
                -StateProvider Hklm `
                -AllowHklmWrite `
                -CandidateOriginPath $candidateOrigin `
                -PreCommitValidation {
                    param([IO.FileStream]$LockedOriginStream)
                    if ($controlledClient) {
                        Assert-RebornControlledHostClientInventoryReceipt `
                            $inventoryReceipt `
                            $client `
                            InstalledExact `
                            $policy.CandidateNetSha256 `
                            $policy.LegacyNetSha256 `
                            $policy.ManifestSha256 `
                            $(if ($pairedOrigin) {
                                $policy.CandidateOriginSha256
                            } else {
                                $null
                            }) `
                            -LockedOriginStream $LockedOriginStream | Out-Null
                    }
                }
            $applyResult
        }
        finally {
            $mutation.ReleaseMutex()
            $mutation.Dispose()
        }
    }
    finally {
        if ($null -ne $runtimeSetLock) {
            Exit-RebornControlledHostRuntimeSetLock `
                $runtimeSetLock
        }
        Exit-RebornSecureNetworkOperationLock $operationLock
    }
    return
}

if ([string]::IsNullOrWhiteSpace($ApplyBackupPath)) {
    throw 'Restore requires -ApplyBackupPath from the Apply receipt.'
}
if (-not $PSCmdlet.ShouldProcess(
        $client,
        'Disable secure routing and restore the exact predecessor files')) {
    return
}
$operationLock =
    Enter-RebornSecureNetworkOperationLock -Name 'secure-bundle'
$runtimeSetLock = $null
try {
    $runtimeSetLock =
        Enter-RebornControlledHostRuntimeSetLock
    if ($controlledClient) {
        $inventoryReceipt =
            Read-RebornControlledHostClientInventoryReceipt `
                $ClientInventoryReceiptPath `
                $ExpectedClientInventoryReceiptSha256
    }
    $mutation = Enter-SecureBundleMutation
    try {
        Assert-OriginClosed
        $restoreResult = Invoke-RebornSecureBundleRestore `
            -Policy $policy `
            -ClientRoot $client `
            -CandidatePath $candidate `
            -ManifestPath $manifest `
            -TrustPath $trust `
            -ApplyBackupPath $ApplyBackupPath `
            -BackupRoot $backup `
            -StateProvider Hklm `
            -AllowHklmWrite
        if ($controlledClient) {
            Assert-RebornControlledHostClientInventoryReceipt `
                $inventoryReceipt $client Stock | Out-Null
        }
        $restoreResult
    }
    finally {
        $mutation.ReleaseMutex()
        $mutation.Dispose()
    }
}
finally {
    if ($null -ne $runtimeSetLock) {
        Exit-RebornControlledHostRuntimeSetLock $runtimeSetLock
    }
    Exit-RebornSecureNetworkOperationLock $operationLock
}
