[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply', 'Restore')]
    [string]$Mode = 'Status',

    [string]$ClientRoot = 'C:\Godswar Origin',

    [string]$CandidatePath = (
        Join-Path $PSScriptRoot `
            '..\client\network-shim\bin\Release\Win32\Net.dll'),

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
    Join-Path $PSScriptRoot 'SecureNetworkBundleFiles.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force

$requiredDependencyCommands = @(
    'Assert-RebornControlledHostClientInventoryReceipt'
    'Assert-RebornControlledHostClientPostInventoryReboot'
    'Assert-RebornDirectChildDirectory'
    'Assert-RebornProtectedDirectoryPath'
    'Assert-RebornRegularFilePath'
    'Copy-RebornFileAtomic'
    'Enter-RebornControlledHostRuntimeSetLock'
    'Enter-RebornSecureNetworkOperationLock'
    'Exit-RebornControlledHostRuntimeSetLock'
    'Exit-RebornSecureNetworkOperationLock'
    'Get-RebornSecureBundleStatus'
    'Initialize-RebornProtectedDirectoryPath'
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

function Get-SecureStreamSha256 {
    param([Parameter(Mandatory)][IO.FileStream]$Stream)

    if (-not $Stream.CanRead -or -not $Stream.CanSeek) {
        throw 'Secure staged-file verification requires a readable seekable stream.'
    }
    $originalPosition = $Stream.Position
    $algorithm = $null
    $hash = $null
    try {
        $Stream.Position = 0
        $algorithm = [Security.Cryptography.SHA256]::Create()
        $hash = $algorithm.ComputeHash($Stream)
        return ([BitConverter]::ToString($hash)).Replace('-', '')
    }
    finally {
        $Stream.Position = $originalPosition
        if ($null -ne $hash) {
            [Array]::Clear($hash, 0, $hash.Length)
        }
        if ($null -ne $algorithm) {
            $algorithm.Dispose()
        }
    }
}

function Invoke-NativeOfflineGate {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$StockNet,
        [Parameter(Mandatory)][string]$Manifest,
        [Parameter(Mandatory)][string]$ScratchRoot,
        [Parameter(Mandatory)][string]$ExpectedCandidate,
        [Parameter(Mandatory)][string]$ExpectedStockNet,
        [Parameter(Mandatory)][string]$ExpectedChecks,
        [Parameter(Mandatory)][string]$ExpectedManifest,
        [switch]$IncludeSockets
    )

    $candidateFile = [IO.Path]::GetFullPath($Candidate)
    $outputDirectory = Split-Path -Parent $candidateFile
    $checksSource =
        Join-Path $outputDirectory 'Godswar.NetShim.Checks.exe'

    $scratchBase = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\')
    $filesystemRoot =
        [IO.Path]::GetPathRoot($scratchBase).TrimEnd('\')
    if ($scratchBase.Equals(
            $filesystemRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ScratchRoot cannot be a filesystem root.'
    }
    $scratchBase = Initialize-RebornProtectedDirectoryPath `
        $scratchBase 'secure probe scratch root'
    $probe = Join-Path $scratchBase (
        'secure-bundle-offline-probe-' +
        [Guid]::NewGuid().ToString('N'))
    $probe = Initialize-RebornProtectedDirectoryPath `
        $probe 'secure probe directory'
    $stagedChecks = Join-Path $probe 'Godswar.NetShim.Checks.exe'
    $stagedCandidate = Join-Path $probe 'Net.dll'
    $stagedLegacy = Join-Path $probe 'NetLegacy.dll'
    $stagedManifest = Join-Path $probe 'RebornNetwork.gwem'
    $locks = @()
    try {
        Copy-RebornFileAtomic `
            $checksSource $stagedChecks $ExpectedChecks
        Copy-RebornFileAtomic `
            $candidateFile $stagedCandidate $ExpectedCandidate
        Copy-RebornFileAtomic `
            $StockNet $stagedLegacy $ExpectedStockNet
        Copy-RebornFileAtomic `
            $Manifest $stagedManifest $ExpectedManifest

        foreach ($input in @(
            @($stagedChecks, $ExpectedChecks, 'verification executable'),
            @($stagedCandidate, $ExpectedCandidate, 'candidate Net.dll'),
            @($stagedLegacy, $ExpectedStockNet, 'stock Net.dll'),
            @($stagedManifest, $ExpectedManifest, 'endpoint manifest')
        )) {
            $lock = $null
            try {
                $lock = [IO.File]::Open(
                    [string]$input[0],
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read)
                if ((Get-SecureStreamSha256 $lock) -cne
                        [string]$input[1]) {
                    throw "Locked staged $($input[2]) SHA-256 mismatch."
                }
                $locks += $lock
                $lock = $null
            }
            finally {
                if ($null -ne $lock) {
                    $lock.Dispose()
                }
            }
        }

        if ($IncludeSockets) {
            & $stagedChecks
        } else {
            & $stagedChecks --offline
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Native candidate checks failed with exit code $LASTEXITCODE."
        }

        & $stagedChecks `
            --offline-manifest-probe `
            $stagedCandidate `
            $stagedManifest
        if ($LASTEXITCODE -ne 0) {
            throw (
                'Manifest does not match the candidate build verification ' +
                "key; probe exit code $LASTEXITCODE.")
        }

        & $stagedChecks --offline-probe $stagedCandidate
        if ($LASTEXITCODE -ne 0) {
            throw (
                'Offline stock-delegation probe failed with exit code ' +
                "$LASTEXITCODE."
            )
        }
    }
    finally {
        foreach ($lock in $locks) {
            $lock.Dispose()
        }
        Assert-RebornDirectChildDirectory `
            $probe $scratchBase 'secure probe cleanup directory' `
            -RequireProtected | Out-Null
        foreach ($staged in @(
            $stagedChecks,
            $stagedCandidate,
            $stagedLegacy,
            $stagedManifest
        )) {
            if (Test-Path -LiteralPath $staged -PathType Leaf) {
                Assert-RebornRegularFilePath `
                    $staged 'secure probe cleanup file' | Out-Null
                [IO.File]::Delete($staged)
            }
        }
        [IO.Directory]::Delete($probe, $false)
    }
}

$candidate = [IO.Path]::GetFullPath($CandidatePath)
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
    -OriginSha256 $supportedOriginSha256 `
    -LegacyNetSha256 $supportedLegacySha256 `
    -CandidateNetSha256 (
        $ExpectedCandidateSha256.ToUpperInvariant()) `
    -ManifestSha256 (
        $ExpectedManifestSha256.ToUpperInvariant()) `
    -ManifestTrustSha256 (
        $ExpectedTrustSha256.ToUpperInvariant())
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
        -StateProvider Hklm
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
            $policy.ManifestSha256 | Out-Null
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
        -StateProvider Hklm
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
            -StateProvider Hklm
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
            Invoke-NativeOfflineGate `
                -Candidate $candidate `
                -StockNet (Join-Path $client 'Net.dll') `
                -Manifest $manifest `
                -ScratchRoot (Join-Path $backup '.staging') `
                -ExpectedCandidate $policy.CandidateNetSha256 `
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
