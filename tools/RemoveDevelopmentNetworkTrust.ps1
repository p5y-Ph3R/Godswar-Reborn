[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'MigrateLegacyReceipt', 'Remove')]
    [string]$Mode = 'Status',

    [Parameter(Mandatory)]
    [string]$ReceiptPath,

    [string]$ClientRoot = 'C:\RebornNetworkAcceptanceClient',

    [string]$ClientInventoryReceiptPath,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedClientInventoryReceiptSha256,

    [switch]$AllowReceiptMigration,

    [switch]$AllowTrustRemoval,

    [switch]$AllowTestPath,

    [ValidateSet('None', 'AfterPendingReceipt', 'AfterStoreRemove')]
    [string]$TestFailurePoint = 'None'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentNetworkTrustReceipt.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Resolve-TrustScope {
    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    $tlsRoot = Split-Path -Parent $receipt
    if ([IO.Path]::GetFileName($receipt) -cne
        'current-user-trust-receipt.json') {
        throw 'Trust receipt must use its exact issued filename.'
    }
    if ($AllowTestPath) {
        $temporary = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $receipt.StartsWith(
                $temporary,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Test trust receipt must remain below temp.'
        }
        return [pscustomobject]@{
            Receipt = $receipt
            TlsRoot = $tlsRoot
            RuntimeRoot = $null
        }
    }

    if ($Mode -eq 'MigrateLegacyReceipt') {
        $issuedRoot = [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot (
                '..\artifacts\controlled-host-acceptance'))
        ).TrimEnd('\')
        if (-not $receipt.StartsWith(
                $issuedRoot + '\',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Legacy migration requires the exact issued TLS scope.'
        }
        $relative = $receipt.Substring($issuedRoot.Length).TrimStart('\')
        if ($relative -cnotmatch (
                '^\d{8}-\d{6}\\tls\\' +
                'current-user-trust-receipt\.json$')) {
            throw 'Legacy migration requires the exact issued TLS scope.'
        }
        return [pscustomobject]@{
            Receipt = $receipt
            TlsRoot = $tlsRoot
            RuntimeRoot = $null
        }
    }

    $runtimeBase = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData 'RebornSecureNetworkRuntime')
    ).TrimEnd('\')
    if (-not $receipt.StartsWith(
            $runtimeBase + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Trust removal requires the exact protected runtime TLS scope.'
    }
    $relative = $receipt.Substring($runtimeBase.Length).TrimStart('\')
    if ($relative -cnotmatch (
            '^\d{8}-\d{6}\\tls\\' +
            'current-user-trust-receipt\.json$')) {
        throw 'Trust removal requires the exact protected runtime TLS scope.'
    }
    $runtimeRoot = Split-Path -Parent $tlsRoot
    Assert-RebornProtectedDirectoryPath `
        $runtimeRoot 'controlled-host runtime root' `
        -ProtectContents -RequireProtectedAcl | Out-Null
    Assert-RebornSingleLinkRegularFilePath `
        $receipt 'protected trust receipt' | Out-Null
    [pscustomobject]@{
        Receipt = $receipt
        TlsRoot = $tlsRoot
        RuntimeRoot = $runtimeRoot
    }
}

function Get-ExactStoreCertificate {
    param([object]$Store, [object]$Record)

    $matches = $Store.Certificates.Find(
        [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
        [string]$Record.thumbprint,
        $false)
    if ($matches.Count -gt 1) {
        throw 'More than one CurrentUser root matched the receipt.'
    }
    if ($matches.Count -eq 0) {
        return $null
    }
    return Get-RebornTrustCertificateDescriptor $matches[0]
}

function Assert-LegacyPfxScope {
    param([object]$Scope, [object]$RootDescriptor)

    $rootPath = Join-Path $Scope.TlsRoot 'reborn-development-root.cer'
    $pfxPath = Join-Path $Scope.TlsRoot 'reborn-development-server.pfx'
    $secretPath = Join-Path (
        $Scope.TlsRoot) 'certificate-password.dpapi.clixml'
    foreach ($path in @($rootPath, $pfxPath, $secretPath)) {
        Assert-RebornSingleLinkRegularFilePath `
            $path 'issued TLS migration artifact' | Out-Null
    }
    $securePassword = Import-Clixml -LiteralPath $secretPath
    if ($securePassword -isnot [Security.SecureString]) {
        throw 'Issued PFX password artifact is not a SecureString.'
    }
    $collection =
        [Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    $passwordBstr = [IntPtr]::Zero
    $plainPassword = $null
    try {
        $passwordBstr =
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
                $securePassword)
        $plainPassword =
            [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
                $passwordBstr)
        $collection.Import(
            $pfxPath,
            $plainPassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        $private = @($collection | Where-Object HasPrivateKey)
        $root = @($collection | Where-Object {
            -not $_.HasPrivateKey -and
            $_.Subject -ceq 'CN=Reborn Development Root CA'
        })
        if ($collection.Count -ne 2 -or
            $private.Count -ne 1 -or
            $root.Count -ne 1) {
            throw 'Issued PFX does not contain one private leaf and public root.'
        }
        Assert-RebornTrustCertificateDescriptor `
            (Get-RebornTrustCertificateDescriptor $root[0]) `
            ([pscustomobject]@{
                subject = $RootDescriptor.Subject
                thumbprint = $RootDescriptor.Thumbprint
                rootSha256 = $RootDescriptor.RootSha256
            })
    }
    finally {
        foreach ($certificate in $collection) {
            $certificate.Dispose()
        }
        $plainPassword = $null
        if ($passwordBstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR(
                $passwordBstr)
        }
        $securePassword.Dispose()
    }
    [pscustomobject]@{
        RootPath = $rootPath
        PfxPath = $pfxPath
        RootSha256 = Get-RebornTrustFileSha256 $rootPath
        PfxSha256 = Get-RebornTrustFileSha256 $pfxPath
    }
}

$scope = Resolve-TrustScope
if ($TestFailurePoint -ne 'None' -and -not $AllowTestPath) {
    throw 'Trust fault injection requires explicit -AllowTestPath.'
}

if ($Mode -eq 'Status') {
    if (-not (Test-Path -LiteralPath $scope.Receipt -PathType Leaf)) {
        [pscustomobject]@{
            State = 'Absent'
            ReceiptPath = $scope.Receipt
        }
        return
    }
    $loaded = Read-RebornTrustReceipt $scope.Receipt
    [pscustomobject]@{
        State = $loaded.Record.state
        ReceiptPath = $loaded.Path
        InstalledByScript = $loaded.Record.installedByScript
        Thumbprint = $loaded.Record.thumbprint
    }
    return
}

if ($Mode -eq 'MigrateLegacyReceipt') {
    if (-not $AllowReceiptMigration) {
        throw 'MigrateLegacyReceipt requires -AllowReceiptMigration.'
    }
    try {
        $legacy = Get-Content -LiteralPath $scope.Receipt -Raw |
            ConvertFrom-Json
    }
    catch {
        throw 'Legacy trust receipt is not valid JSON.'
    }
    $rootPath = Join-Path $scope.TlsRoot 'reborn-development-root.cer'
    $root = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $rootPath)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $rootDescriptor = Get-RebornTrustCertificateDescriptor $root
        $artifacts = Assert-LegacyPfxScope $scope $rootDescriptor
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $rootDescriptor.Thumbprint,
            $false)
        if ($matches.Count -ne 1) {
            throw 'Legacy receipt root is not installed exactly once.'
        }
        Assert-RebornTrustCertificateDescriptor `
            (Get-RebornTrustCertificateDescriptor $matches[0]) `
            ([pscustomobject]@{
                subject = $rootDescriptor.Subject
                thumbprint = $rootDescriptor.Thumbprint
                rootSha256 = $rootDescriptor.RootSha256
            })
        $record = ConvertFrom-RebornLegacyTrustReceipt `
            $legacy $rootDescriptor `
            $artifacts.RootSha256 $artifacts.PfxSha256
        if (-not $PSCmdlet.ShouldProcess(
                $scope.Receipt,
                'Issue durable schema-2 cleanup authority')) {
            return
        }
        Write-RebornTrustReceiptAtomic $record $scope.Receipt
        Read-RebornTrustReceipt $scope.Receipt | Out-Null
        [pscustomobject]@{
            Result = 'Migrated'
            ReceiptPath = $scope.Receipt
            StoreAddPerformed = $false
        }
    }
    finally {
        $store.Dispose()
        $root.Dispose()
    }
    return
}

if (-not $AllowTrustRemoval) {
    throw 'Remove requires explicit -AllowTrustRemoval.'
}
if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
    throw 'Origin.exe must be closed before development trust removal.'
}
$loaded = Read-RebornTrustReceipt $scope.Receipt
if (-not $AllowTestPath) {
    if ([string]::IsNullOrWhiteSpace(
            $ClientInventoryReceiptPath) -or
        [string]::IsNullOrWhiteSpace(
            $ExpectedClientInventoryReceiptSha256)) {
        throw (
            'Trust removal requires the protected stock client ' +
            'inventory receipt and its expected SHA-256.')
    }
    Import-Module (
        Join-Path $PSScriptRoot (
            'ControlledHostCleanupAuthorization.psm1')
    ) -Force
    Assert-RebornControlledHostCleanupAuthorization `
        $scope.RuntimeRoot $ClientRoot `
        $ClientInventoryReceiptPath `
        $ExpectedClientInventoryReceiptSha256 | Out-Null
}
if (-not $PSCmdlet.ShouldProcess(
        "Cert:\CurrentUser\Root\$($loaded.Record.thumbprint)",
        'Remove the exact receipt-bound development root')) {
    return
}
$runtimeLock = $null
$bundleLock = $null
$store = $null
try {
    if (-not $AllowTestPath) {
        Import-Module (
            Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
        ) -Force
        Import-Module (
            Join-Path $PSScriptRoot 'ControlledHostRuntimeLock.psm1'
        ) -Force
        $bundleLock = Enter-RebornSecureNetworkOperationLock `
            -Name 'secure-bundle'
        $runtimeLock = Enter-RebornControlledHostRuntimeLock `
            -RuntimeRoot $scope.RuntimeRoot `
            -Purpose 'development trust cleanup'
        Assert-RebornControlledHostCleanupAuthorization `
            $scope.RuntimeRoot $ClientRoot `
            $ClientInventoryReceiptPath `
            $ExpectedClientInventoryReceiptSha256 | Out-Null
    }
    $loaded = Read-RebornTrustReceipt $scope.Receipt
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $find = { Get-ExactStoreCertificate $store $loaded.Record }
    $remove = {
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            [string]$loaded.Record.thumbprint,
            $false)
        if ($matches.Count -ne 1) {
            throw 'Exact root disappeared before guarded removal.'
        }
        $store.Remove($matches[0])
    }
    $result = Invoke-RebornTrustRemovalTransaction `
        $loaded $find $remove -FailurePoint $TestFailurePoint
    [pscustomobject]@{
        Result = $result.Result
        ReceiptPath = $result.Loaded.Path
        Thumbprint = $result.Loaded.Record.thumbprint
    }
}
finally {
    if ($null -ne $store) {
        $store.Dispose()
    }
    if ($null -ne $runtimeLock) {
        Exit-RebornControlledHostRuntimeLock $runtimeLock
    }
    if ($null -ne $bundleLock) {
        Exit-RebornSecureNetworkOperationLock $bundleLock
    }
}
