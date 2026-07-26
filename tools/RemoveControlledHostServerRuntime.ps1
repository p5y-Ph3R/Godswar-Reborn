[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$ReceiptPath,

    [Parameter(Mandatory)]
    [string]$ClientInventoryReceiptPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedClientInventoryReceiptSha256,

    [switch]$AllowRuntimeRemoval
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'ControlledHostCleanupAuthorization.psm1',
    'ControlledHostClientInventoryReceipt.psm1',
    'ControlledHostClientRootLease.psm1',
    'ControlledHostRuntimeCleanup.psm1',
    'ControlledHostRuntimeCleanupReceipt.psm1',
    'ControlledHostRuntimeLock.psm1',
    'ControlledHostServerRuntime.psm1',
    'SecureNetworkActivationState.psm1',
    'SecureNetworkOperationLock.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName)
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-CleanupResumeAuthorization {
    param(
        [Parameter(Mandatory)][object]$Cleanup,
        [Parameter(Mandatory)][string]$ExpectedInventoryPath,
        [Parameter(Mandatory)][string]$ExpectedInventorySha256
    )

    if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
        throw 'Origin.exe must be closed before controlled-host cleanup.'
    }
    $record = $Cleanup.Record
    $inventoryPath =
        [IO.Path]::GetFullPath($ExpectedInventoryPath)
    if (-not $inventoryPath.Equals(
            [IO.Path]::GetFullPath(
                [string]$record.clientInventoryReceiptPath),
            [StringComparison]::OrdinalIgnoreCase) -or
        $ExpectedInventorySha256.ToUpperInvariant() -cne
            [string]$record.clientInventoryReceiptSha256) {
        throw 'Cleanup retry does not match its protected inventory pin.'
    }
    $inventory =
        Read-RebornControlledHostClientInventoryReceipt `
            $inventoryPath $ExpectedInventorySha256
    Assert-RebornControlledHostClientInventoryReceipt `
        $inventory 'C:\RebornNetworkAcceptanceClient' Stock |
        Out-Null

    $activation = Assert-RebornProtectedHklmActivationState
    if (
        -not $activation.Exists -or
        -not $activation.Complete -or
        [UInt64]$activation.Mode -ne 0 -or
        [UInt64]$activation.Environment -ne
            [UInt64]$record.activationEnvironment -or
        [UInt64]$activation.SequenceFloor -ne
            [UInt64]$record.activationSequenceFloor
    ) {
        throw (
            'Cleanup retry requires the exact protected disabled ' +
            'activation authority.')
    }

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::
                FindByThumbprint,
            [string]$record.trustRootThumbprint,
            $false)
        if ($matches.Count -ne 0) {
            throw 'Cleanup retry found the removed development root again.'
        }
    }
    finally {
        $store.Close()
        $store.Dispose()
    }
    $provider =
        [Security.Cryptography.CngProvider]::
            MicrosoftSoftwareKeyStorageProvider
    foreach ($keyName in @(
        [string]$record.manifestCurrentKeyName,
        [string]$record.manifestNextKeyName
    )) {
        if ([Security.Cryptography.CngKey]::Exists(
                $keyName,
                $provider,
                [Security.Cryptography.CngKeyOpenOptions]::None)) {
            throw "Cleanup retry found removed manifest key '$keyName'."
        }
    }
}

if (-not $AllowRuntimeRemoval) {
    throw 'Removal requires explicit -AllowRuntimeRemoval.'
}
if (-not (Test-IsAdministrator)) {
    throw 'Controlled-host runtime removal requires elevation.'
}

$receiptFullPath = [IO.Path]::GetFullPath($ReceiptPath)
if ([IO.Path]::GetFileName($receiptFullPath) -cne 'receipt.json') {
    throw 'Controlled-host runtime receipt must be named receipt.json.'
}
$runtime = (Split-Path -Parent $receiptFullPath).TrimEnd('\')
$parent = Split-Path -Parent $runtime
if ([IO.Path]::GetFileName($runtime) -cnotmatch '^\d{8}-\d{6}$' -or
    [IO.Path]::GetFileName($parent) -cne
        'RebornSecureNetworkRuntime') {
    throw "Refusing unsafe controlled-host runtime removal: $runtime"
}
$cleanupReceiptPath =
    Get-RebornControlledHostRuntimeCleanupReceiptPath $runtime
$resume = Test-Path -LiteralPath $cleanupReceiptPath -PathType Leaf
if ($resume) {
    $preflight =
        Read-RebornControlledHostRuntimeCleanupReceipt `
            $cleanupReceiptPath
    if (-not $preflight.RuntimeRoot.Equals(
            $runtime,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup retry names a different runtime root.'
    }
} else {
    $preflight =
        Read-RebornControlledHostRuntimeReceipt $receiptFullPath
}
if (-not $PSCmdlet.ShouldProcess(
        $runtime,
        'Remove the exact receipt-bound controlled-host server runtime')) {
    return
}

$operationLock =
    Enter-RebornSecureNetworkOperationLock -Name 'secure-bundle'
$runtimeLock = $null
$runtimeLease = $null
try {
    $runtimeLock = Enter-RebornControlledHostRuntimeLock `
        $runtime 'guarded runtime cleanup'
    $running = @(
        Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object {
                $_.CommandLine -is [string] -and
                $_.CommandLine.IndexOf(
                    $runtime,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0
            }
    )
    if ($running.Count -ne 0) {
        throw 'A process is still using the protected controlled-host runtime.'
    }

    if (Test-Path -LiteralPath $cleanupReceiptPath -PathType Leaf) {
        $cleanup =
            Read-RebornControlledHostRuntimeCleanupReceipt `
                $cleanupReceiptPath
        Assert-CleanupResumeAuthorization `
            $cleanup `
            $ClientInventoryReceiptPath `
            $ExpectedClientInventoryReceiptSha256
    } else {
        $runtimeLease =
            Enter-RebornControlledHostDirectoryLease $runtime
        try {
            $receipt =
                Read-RebornControlledHostRuntimeReceipt $receiptFullPath
            if (-not $runtime.Equals(
                    $receipt.RuntimeRoot,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    'Controlled-host runtime lock scope changed ' +
                    'during cleanup.')
            }
            $authority =
                Assert-RebornControlledHostFinalCleanupDependencies `
                    $runtime `
                    'C:\RebornNetworkAcceptanceClient' `
                    $ClientInventoryReceiptPath `
                    $ExpectedClientInventoryReceiptSha256
            Assert-RebornControlledHostDirectoryLease $runtimeLease |
                Out-Null
            $cleanup =
                New-RebornControlledHostRuntimeCleanupReceipt `
                    $runtime `
                    ([string]$runtimeLease.Identity) `
                    (Get-FileHash -LiteralPath $receiptFullPath `
                        -Algorithm SHA256).Hash `
                    (Get-FileHash -LiteralPath (
                        Join-Path $runtime 'receipt.sha256'
                    ) -Algorithm SHA256).Hash `
                    $ClientInventoryReceiptPath `
                    $ExpectedClientInventoryReceiptSha256 `
                    $authority.TrustReceiptSha256 `
                    $authority.ManifestKeyReceiptSha256 `
                    $authority.TrustRootThumbprint `
                    $authority.TrustRootSha256 `
                    $authority.ManifestCurrentKeyName `
                    $authority.ManifestNextKeyName `
                    $authority.ManifestCurrentTrustSha256 `
                    $authority.ManifestNextTrustSha256 `
                    ([UInt64]$authority.ActivationEnvironment) `
                    ([UInt64]$authority.ActivationSequenceFloor)
        }
        finally {
            Exit-RebornControlledHostDirectoryLease $runtimeLease
            $runtimeLease = $null
        }
    }

    $removed =
        Invoke-RebornControlledHostRuntimeCleanup `
            $cleanup.Path
    [pscustomobject]@{
        Result = 'Removed'
        RuntimeRoot = $runtime
        CleanupReceiptPath = $removed.Path
        ClientInventoryReceiptSha256 =
            [string]$removed.Record.clientInventoryReceiptSha256
        Recovery =
            'Re-run PrepareControlledHostServerRuntime.ps1 from pinned sources.'
    }
}
finally {
    if ($null -ne $runtimeLease) {
        Exit-RebornControlledHostDirectoryLease $runtimeLease
    }
    if ($null -ne $runtimeLock) {
        Exit-RebornControlledHostRuntimeLock $runtimeLock
    }
    Exit-RebornSecureNetworkOperationLock $operationLock
}
