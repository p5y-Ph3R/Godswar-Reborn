[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [Parameter(Mandatory)]
    [ValidatePattern('^godswar_secure_acceptance_\d{8}_\d{6}$')]
    [string]$ExpectedDatabaseName,

    [string]$ManagedReleaseDirectory = (
        Join-Path $PSScriptRoot `
            '..\src\Godswar.Server\bin\Release\net10.0'),

    [string]$OptionsPath = (
        Join-Path $PSScriptRoot '..\appsettings.json'),

    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][string]$RootCertificatePath,
    [Parameter(Mandatory)][string]$TrustReceiptPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ManifestTrustPath,
    [Parameter(Mandatory)][string]$ManifestKeyReceiptPath,
    [Parameter(Mandatory)][string]$NativeChecksPath,
    [Parameter(Mandatory)][string]$CertificatePasswordSecretPath,
    [Parameter(Mandatory)][string]$PostgresConnectionSecretPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManagedReleaseSetSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOptionsSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCertificateSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCertificateSecretSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedPostgresSecretSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedRootCertificateSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedTrustReceiptSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestTrustSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedManifestKeyReceiptSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedNativeChecksSha256,

    [switch]$AllowRuntimeWrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostServerRuntime.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostManagedRelease.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleFiles.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkOperationLock.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostRuntimeLock.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureEndpointManifestValidation.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Initialize-RuntimeDirectory {
    param([Parameter(Mandatory)][string]$Path)

    Assert-RebornDirectoryPath (
        Split-Path -Parent $Path
    ) 'controlled-host runtime parent' | Out-Null
    if (-not (Test-Path -LiteralPath $Path)) {
        [IO.Directory]::CreateDirectory(
            $Path,
            (New-RebornControlledHostRuntimeSecurity)) | Out-Null
    }
    Assert-RebornProtectedDirectoryPath `
        $Path 'controlled-host runtime directory' `
        -ProtectContents `
        -RequireProtectedAcl | Out-Null
}

function Invoke-Icacls {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & icacls.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed with exit code $LASTEXITCODE."
    }
}

function Write-Receipt {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][object]$Record
    )

    $receipt = Join-Path $Directory 'receipt.json'
    $checksum = Join-Path $Directory 'receipt.sha256'
    [IO.File]::WriteAllText(
        $receipt,
        ($Record | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $checksum,
        (Get-Sha256 $receipt),
        [Text.UTF8Encoding]::new($false))
}

$sourceRelease = [IO.Path]::GetFullPath(
    $ManagedReleaseDirectory).TrimEnd('\')
$expectedSourceRelease = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot `
        '..\src\Godswar.Server\bin\Release\net10.0')).TrimEnd('\')
$sourceOptions = [IO.Path]::GetFullPath($OptionsPath)
$expectedSourceOptions = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\appsettings.json'))
$sourceCertificate = [IO.Path]::GetFullPath($CertificatePath)
$sourceRootCertificate = [IO.Path]::GetFullPath($RootCertificatePath)
$sourceTrustReceipt = [IO.Path]::GetFullPath($TrustReceiptPath)
$sourceManifest = [IO.Path]::GetFullPath($ManifestPath)
$sourceManifestTrust = [IO.Path]::GetFullPath($ManifestTrustPath)
$sourceManifestKeyReceipt =
    [IO.Path]::GetFullPath($ManifestKeyReceiptPath)
$sourceNativeChecks = [IO.Path]::GetFullPath($NativeChecksPath)
$sourceCertificateSecret =
    [IO.Path]::GetFullPath($CertificatePasswordSecretPath)
$sourcePostgresSecret =
    [IO.Path]::GetFullPath($PostgresConnectionSecretPath)
$runtime = Get-RebornControlledHostRuntimeRoot $ExpectedDatabaseName
$stamp = [IO.Path]::GetFileName($runtime)
$artifactRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot (
        '..\artifacts\controlled-host-acceptance\' +
        $stamp))).TrimEnd('\')

foreach ($scope in @(
    @($sourceRelease, $expectedSourceRelease, 'ManagedReleaseDirectory'),
    @($sourceOptions, $expectedSourceOptions, 'OptionsPath'),
    @(
        $sourceCertificate,
        (Join-Path $artifactRoot 'tls\reborn-development-server.pfx'),
        'CertificatePath'
    ),
    @(
        $sourceRootCertificate,
        (Join-Path $artifactRoot 'tls\reborn-development-root.cer'),
        'RootCertificatePath'
    ),
    @(
        $sourceTrustReceipt,
        (Join-Path $artifactRoot 'tls\current-user-trust-receipt.json'),
        'TrustReceiptPath'
    ),
    @(
        $sourceManifest,
        [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot `
                '..\artifacts\secure-network\RebornNetwork.gwem')),
        'ManifestPath'
    ),
    @(
        $sourceManifestTrust,
        [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot (
                '..\artifacts\secure-network\' +
                'development-manifest-trust.json'))),
        'ManifestTrustPath'
    ),
    @(
        $sourceManifestKeyReceipt,
        [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot (
                '..\artifacts\secure-network\' +
                'development-manifest-key-receipt.json'))),
        'ManifestKeyReceiptPath'
    ),
    @(
        $sourceNativeChecks,
        [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot (
                '..\client\network-shim\bin\Release\Win32\' +
                'Godswar.NetShim.Checks.exe'))),
        'NativeChecksPath'
    ),
    @(
        $sourceCertificateSecret,
        (Join-Path $artifactRoot `
            'tls\certificate-password.dpapi.clixml'),
        'CertificatePasswordSecretPath'
    ),
    @(
        $sourcePostgresSecret,
        (Join-Path $artifactRoot `
            'tls\postgres-connection.dpapi.clixml'),
        'PostgresConnectionSecretPath'
    )
)) {
    if (-not $scope[0].Equals(
            $scope[1],
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($scope[2]) is outside the exact controlled-host source."
    }
}

$sourceSet =
    Get-RebornControlledHostManagedReleaseSet $sourceRelease
if ($sourceSet.SetSha256 -cne
        $ExpectedManagedReleaseSetSha256.ToUpperInvariant()) {
    throw 'Source managed release set hash mismatch.'
}
$sourceFiles = @(
    @($sourceOptions, $ExpectedOptionsSha256, 'server options'),
    @($sourceCertificate, $ExpectedCertificateSha256, 'TLS PFX'),
    @(
        $sourceRootCertificate,
        $ExpectedRootCertificateSha256,
        'issued root'
    ),
    @(
        $sourceTrustReceipt,
        $ExpectedTrustReceiptSha256,
        'trust receipt'
    ),
    @(
        $sourceManifest,
        $ExpectedManifestSha256,
        'endpoint manifest'
    ),
    @(
        $sourceManifestTrust,
        $ExpectedManifestTrustSha256,
        'endpoint-manifest trust'
    ),
    @(
        $sourceManifestKeyReceipt,
        $ExpectedManifestKeyReceiptSha256,
        'manifest-key receipt'
    ),
    @(
        $sourceNativeChecks,
        $ExpectedNativeChecksSha256,
        'native checks'
    ),
    @(
        $sourceCertificateSecret,
        $ExpectedCertificateSecretSha256,
        'certificate secret'
    ),
    @(
        $sourcePostgresSecret,
        $ExpectedPostgresSecretSha256,
        'PostgreSQL secret'
    )
)
foreach ($file in $sourceFiles) {
    Assert-RebornSingleLinkRegularFilePath `
        $file[0] "controlled-host source $($file[2])" | Out-Null
    if ((Get-Sha256 $file[0]) -cne
            ([string]$file[1]).ToUpperInvariant()) {
        throw "Source $($file[2]) hash mismatch."
    }
}
$sourceManifestAuthority =
    Read-RebornSecureEndpointManifestForRestore `
        $sourceManifest `
        $sourceManifestTrust `
        0
if ($sourceManifestAuthority.ManifestSha256 -cne
        $ExpectedManifestSha256.ToUpperInvariant() -or
    $sourceManifestAuthority.TrustSha256 -cne
        $ExpectedManifestTrustSha256.ToUpperInvariant()) {
    throw 'Source endpoint manifest authority mismatch.'
}

$state = if (-not (Test-Path -LiteralPath $runtime)) {
    'SourceVerified'
} else {
    try {
        Assert-RebornControlledHostRuntime `
            $runtime `
            $ExpectedManagedReleaseSetSha256 `
            $ExpectedOptionsSha256 `
            $ExpectedCertificateSha256 `
            $ExpectedCertificateSecretSha256 `
            $ExpectedPostgresSecretSha256 `
            $ExpectedRootCertificateSha256 `
            $ExpectedTrustReceiptSha256 `
            $ExpectedManifestSha256 `
            $ExpectedManifestTrustSha256 `
            $ExpectedManifestKeyReceiptSha256 `
            $ExpectedNativeChecksSha256 | Out-Null
        'Protected'
    }
    catch {
        'Conflict'
    }
}
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        State = $state
        RuntimeRoot = $runtime
        ManagedReleaseSetSha256 = $sourceSet.SetSha256
        OptionsSha256 = Get-Sha256 $sourceOptions
        CertificateSha256 = Get-Sha256 $sourceCertificate
        CertificateSecretSha256 = Get-Sha256 $sourceCertificateSecret
        PostgresSecretSha256 = Get-Sha256 $sourcePostgresSecret
        RootCertificateSha256 = Get-Sha256 $sourceRootCertificate
        TrustReceiptSha256 = Get-Sha256 $sourceTrustReceipt
        ManifestSha256 = Get-Sha256 $sourceManifest
        ManifestTrustSha256 = Get-Sha256 $sourceManifestTrust
        ManifestKeyReceiptSha256 =
            Get-Sha256 $sourceManifestKeyReceipt
        NativeChecksSha256 = Get-Sha256 $sourceNativeChecks
        Elevated = Test-IsAdministrator
    }
    return
}

if (-not $AllowRuntimeWrite) {
    throw 'Apply requires explicit -AllowRuntimeWrite.'
}
if (-not (Test-IsAdministrator)) {
    throw 'Controlled-host runtime Apply requires elevation.'
}
if ($state -eq 'Conflict') {
    throw 'Controlled-host runtime target conflicts with reviewed inputs.'
}
if ($state -eq 'Protected') {
    [pscustomobject]@{
        Result = 'AlreadyProtected'
        RuntimeRoot = $runtime
        ReceiptPath = Join-Path $runtime 'receipt.json'
    }
    return
}
if (-not $PSCmdlet.ShouldProcess(
        $runtime,
        'Create the exact protected controlled-host server runtime')) {
    return
}

$operationLock =
    Enter-RebornSecureNetworkOperationLock -Name 'secure-bundle'
$runtimeLock = $null
try {
$runtimeLock = Enter-RebornControlledHostRuntimeLock `
    $runtime 'runtime preparation'
$commonRoot = Split-Path -Parent $runtime
$staging = Join-Path $commonRoot (
    ".$stamp-$([guid]::NewGuid().ToString('N')).stage")
$moved = $false
try {
    Initialize-RuntimeDirectory $commonRoot
    if (Test-Path -LiteralPath $runtime) {
        throw 'Controlled-host runtime target appeared during preparation.'
    }
    Initialize-RuntimeDirectory $staging
    $managedTarget = Join-Path $staging 'managed'
    $tlsTarget = Join-Path $staging 'tls'
    $bundleTarget = Join-Path $staging 'bundle'
    [IO.Directory]::CreateDirectory($managedTarget) | Out-Null
    [IO.Directory]::CreateDirectory($tlsTarget) | Out-Null
    [IO.Directory]::CreateDirectory($bundleTarget) | Out-Null

    foreach ($file in $sourceSet.Files) {
        Copy-RebornFileAtomic `
            $file.Path `
            (Join-Path $managedTarget $file.Name) `
            $file.Sha256
    }
    Copy-RebornFileAtomic `
        $sourceOptions `
        (Join-Path $staging 'appsettings.json') `
        $ExpectedOptionsSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceCertificate `
        (Join-Path $tlsTarget 'reborn-development-server.pfx') `
        $ExpectedCertificateSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceRootCertificate `
        (Join-Path $tlsTarget 'reborn-development-root.cer') `
        $ExpectedRootCertificateSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceTrustReceipt `
        (Join-Path $tlsTarget 'current-user-trust-receipt.json') `
        $ExpectedTrustReceiptSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceManifest `
        (Join-Path $bundleTarget 'RebornNetwork.gwem') `
        $ExpectedManifestSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceManifestTrust `
        (Join-Path $bundleTarget 'development-manifest-trust.json') `
        $ExpectedManifestTrustSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceManifestKeyReceipt `
        (Join-Path $bundleTarget `
            'development-manifest-key-receipt.json') `
        $ExpectedManifestKeyReceiptSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceNativeChecks `
        (Join-Path $bundleTarget 'Godswar.NetShim.Checks.exe') `
        $ExpectedNativeChecksSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourceCertificateSecret `
        (Join-Path $tlsTarget 'certificate-password.dpapi.clixml') `
        $ExpectedCertificateSecretSha256.ToUpperInvariant()
    Copy-RebornFileAtomic `
        $sourcePostgresSecret `
        (Join-Path $tlsTarget 'postgres-connection.dpapi.clixml') `
        $ExpectedPostgresSecretSha256.ToUpperInvariant()

    Write-Receipt $staging ([ordered]@{
        schemaVersion = 1
        mode = 'ControlledHostServerRuntime'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runtimeRoot = $runtime
        databaseName = $ExpectedDatabaseName
        readerSid =
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        managedReleaseSetSha256 =
            $ExpectedManagedReleaseSetSha256.ToUpperInvariant()
        optionsSha256 = $ExpectedOptionsSha256.ToUpperInvariant()
        certificateSha256 =
            $ExpectedCertificateSha256.ToUpperInvariant()
        certificateSecretSha256 =
            $ExpectedCertificateSecretSha256.ToUpperInvariant()
        postgresSecretSha256 =
            $ExpectedPostgresSecretSha256.ToUpperInvariant()
        rootCertificateSha256 =
            $ExpectedRootCertificateSha256.ToUpperInvariant()
        trustReceiptSha256 =
            $ExpectedTrustReceiptSha256.ToUpperInvariant()
        manifestSha256 =
            $ExpectedManifestSha256.ToUpperInvariant()
        manifestTrustSha256 =
            $ExpectedManifestTrustSha256.ToUpperInvariant()
        manifestKeyReceiptSha256 =
            $ExpectedManifestKeyReceiptSha256.ToUpperInvariant()
        nativeChecksSha256 =
            $ExpectedNativeChecksSha256.ToUpperInvariant()
        activationEnvironment =
            ([UInt64]$sourceManifestAuthority.Environment).ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        activationSequenceFloor =
            ([UInt64]$sourceManifestAuthority.Sequence).ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        recovery = 'Remove only through RemoveControlledHostServerRuntime.ps1.'
    })
    Invoke-Icacls @(
        (Join-Path $staging '*'),
        '/reset',
        '/T',
        '/L',
        '/C',
        '/Q'
    )
    Invoke-Icacls @(
        $staging,
        '/setowner',
        '*S-1-5-32-544',
        '/T',
        '/L',
        '/C',
        '/Q'
    )
    [IO.Directory]::Move($staging, $runtime)
    $moved = $true
    $verified = Assert-RebornControlledHostRuntime `
        $runtime `
        $ExpectedManagedReleaseSetSha256 `
        $ExpectedOptionsSha256 `
        $ExpectedCertificateSha256 `
        $ExpectedCertificateSecretSha256 `
        $ExpectedPostgresSecretSha256 `
        $ExpectedRootCertificateSha256 `
        $ExpectedTrustReceiptSha256 `
        $ExpectedManifestSha256 `
        $ExpectedManifestTrustSha256 `
        $ExpectedManifestKeyReceiptSha256 `
        $ExpectedNativeChecksSha256
    [pscustomobject]@{
        Result = 'Protected'
        RuntimeRoot = $runtime
        ReceiptPath = $verified.ReceiptPath
        ManagedReleaseSetSha256 = $sourceSet.SetSha256
        ProtectedRootCertificatePath =
            Join-Path $runtime 'tls\reborn-development-root.cer'
        ProtectedTrustReceiptPath =
            Join-Path $runtime 'tls\current-user-trust-receipt.json'
        ProtectedManifestTrustPath =
            Join-Path $runtime 'bundle\development-manifest-trust.json'
        ProtectedManifestPath =
            Join-Path $runtime 'bundle\RebornNetwork.gwem'
        ProtectedManifestKeyReceiptPath =
            Join-Path $runtime `
                'bundle\development-manifest-key-receipt.json'
        ProtectedNativeChecksPath =
            Join-Path $runtime 'bundle\Godswar.NetShim.Checks.exe'
        RebootRequired = $false
    }
}
finally {
    if (-not $moved -and
        (Test-Path -LiteralPath $staging -PathType Container)) {
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        if ((Split-Path -Parent $resolvedStaging) -cne $commonRoot -or
            [IO.Path]::GetFileName($resolvedStaging) -cnotmatch
                '^\.\d{8}-\d{6}-[0-9a-f]{32}\.stage$') {
            throw "Refusing unsafe runtime staging cleanup: $resolvedStaging"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
}
finally {
    if ($null -ne $runtimeLock) {
        Exit-RebornControlledHostRuntimeLock $runtimeLock
    }
    Exit-RebornSecureNetworkOperationLock $operationLock
}
