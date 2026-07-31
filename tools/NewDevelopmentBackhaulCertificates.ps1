[CmdletBinding()]
param(
    [string]$OutputDirectory,

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$PasswordEnvironmentVariable =
        'GODSWAR_BACKHAUL_DEVELOPMENT_CERTIFICATE_PASSWORD',

    [ValidateRange(1, 14)]
    [int]$ValidDays = 7
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'Development backhaul certificates can only be created on Windows.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory =
        Join-Path $repoRoot 'artifacts\backhaul-development-tls'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutput) {
    throw (
        'Refusing to alter an existing backhaul-certificate path: ' +
        $resolvedOutput
    )
}

$password = [Environment]::GetEnvironmentVariable(
    $PasswordEnvironmentVariable,
    [EnvironmentVariableTarget]::Process)
if ([string]::IsNullOrEmpty($password)) {
    throw (
        "Set process environment variable $PasswordEnvironmentVariable " +
        'to a nonempty development PFX password.'
    )
}
if ([Text.Encoding]::UTF8.GetByteCount($password) -gt 4096) {
    throw 'The development PFX password exceeds 4096 UTF-8 bytes.'
}

$paths = [ordered]@{
    RootPfx = Join-Path $resolvedOutput 'backhaul-development-root.pfx'
    RootCer = Join-Path $resolvedOutput 'backhaul-development-root.cer'
    GatewayPfx = Join-Path $resolvedOutput 'backhaul-development-gateway.pfx'
    GatewayCer = Join-Path $resolvedOutput 'backhaul-development-gateway.cer'
    WorkerAPfx = Join-Path $resolvedOutput 'backhaul-development-worker-a.pfx'
    WorkerACer = Join-Path $resolvedOutput 'backhaul-development-worker-a.cer'
    WorkerBPfx = Join-Path $resolvedOutput 'backhaul-development-worker-b.pfx'
    WorkerBCer = Join-Path $resolvedOutput 'backhaul-development-worker-b.cer'
    Manifest = Join-Path $resolvedOutput 'backhaul-development-manifest.json'
}

function New-RandomSerial {
    $serial = New-Object byte[] 16
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($serial)
    }
    finally {
        $generator.Dispose()
    }
    $serial[0] = $serial[0] -band 0x7F
    if (($serial | Where-Object { $_ -ne 0 }).Count -eq 0) {
        $serial[15] = 1
    }
    return ,$serial
}

function New-BackhaulLeaf {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Issuer,

        [Parameter(Mandatory)][string]$Subject,
        [Parameter(Mandatory)][string]$DnsName,
        [Parameter(Mandatory)][string]$EnhancedKeyUsageOid,
        [Parameter(Mandatory)][DateTimeOffset]$NotBefore,
        [Parameter(Mandatory)][DateTimeOffset]$NotAfter
    )

    $hash = [Security.Cryptography.HashAlgorithmName]::SHA256
    $padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
    $key = [Security.Cryptography.RSA]::Create(2048)
    $publicCertificate = $null
    $certificate = $null
    $serial = $null
    try {
        $request =
            New-Object Security.Cryptography.X509Certificates.CertificateRequest(
                $Subject,
                $key,
                $hash,
                $padding)
        [void]$request.CertificateExtensions.Add(
            (New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
                $false,
                $false,
                0,
                $true)))
        [void]$request.CertificateExtensions.Add(
            (New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true)))
        $oids =
            New-Object Security.Cryptography.OidCollection
        [void]$oids.Add(
            (New-Object Security.Cryptography.Oid(
                $EnhancedKeyUsageOid)))
        [void]$request.CertificateExtensions.Add(
            (New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                $oids,
                $true)))
        $names =
            New-Object Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
        $names.AddDnsName($DnsName)
        [void]$request.CertificateExtensions.Add($names.Build($true))
        [void]$request.CertificateExtensions.Add(
            (New-Object Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(
                $request.PublicKey,
                $false)))

        $serial = New-RandomSerial
        $publicCertificate = $request.Create(
            $Issuer,
            $NotBefore,
            $NotAfter,
            $serial)
        $certificate =
            [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
                $publicCertificate,
                $key)

        return [pscustomobject]@{
            Certificate = $certificate
            PublicCertificate = $publicCertificate
            Key = $key
            Subject = $Subject
            DnsName = $DnsName
            EnhancedKeyUsageOid = $EnhancedKeyUsageOid
        }
    }
    catch {
        if ($certificate) {
            $certificate.Dispose()
        }
        if ($publicCertificate) {
            $publicCertificate.Dispose()
        }
        $key.Dispose()
        throw
    }
    finally {
        if ($serial) {
            [Array]::Clear($serial, 0, $serial.Length)
        }
    }
}

function Write-CertificateFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $bytes = $Certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    try {
        [IO.File]::WriteAllBytes($Path, $bytes)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Write-PrivatePfx {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Security.Cryptography.X509Certificates.X509Certificate2]$PublicIssuer,
        [Parameter(Mandatory)][string]$Password
    )

    $collection =
        New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
    [void]$collection.Add($Certificate)
    if ($PublicIssuer) {
        [void]$collection.Add($PublicIssuer)
    }
    $bytes = $collection.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $Password)
    try {
        [IO.File]::WriteAllBytes($Path, $bytes)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

$rootKey = $null
$rootCertificate = $null
$publicRoot = $null
$leaves = @()
$createdOutput = $false
$completed = $false
try {
    New-Item -ItemType Directory -Path $resolvedOutput `
        -ErrorAction Stop | Out-Null
    $createdOutput = $true
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $resolvedOutput `
        /inheritance:r `
        /grant:r "${identity}:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not restrict the backhaul-certificate directory ACL.'
    }

    $hash = [Security.Cryptography.HashAlgorithmName]::SHA256
    $padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $leafNotAfter = $notBefore.AddDays($ValidDays)
    $rootNotAfter = $leafNotAfter.AddDays(1)
    $rootKey = [Security.Cryptography.RSA]::Create(3072)
    $rootRequest =
        New-Object Security.Cryptography.X509Certificates.CertificateRequest(
            'CN=Reborn Backhaul Development Root CA',
            $rootKey,
            $hash,
            $padding)
    [void]$rootRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
            $true,
            $false,
            0,
            $true)))
    [void]$rootRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            (
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign
            ),
            $true)))
    [void]$rootRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(
            $rootRequest.PublicKey,
            $false)))
    $rootCertificate = $rootRequest.CreateSelfSigned(
        $notBefore,
        $rootNotAfter)
    $publicRoot =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $rootCertificate.RawData)

    $leaves += New-BackhaulLeaf `
        $rootCertificate `
        'CN=Reborn Backhaul Development Gateway' `
        'gateway.backhaul.reborn.test' `
        '1.3.6.1.5.5.7.3.2' `
        $notBefore `
        $leafNotAfter
    $leaves += New-BackhaulLeaf `
        $rootCertificate `
        'CN=Reborn Backhaul Development Worker A' `
        'worker-a.backhaul.reborn.test' `
        '1.3.6.1.5.5.7.3.1' `
        $notBefore `
        $leafNotAfter
    $leaves += New-BackhaulLeaf `
        $rootCertificate `
        'CN=Reborn Backhaul Development Worker B' `
        'worker-b.backhaul.reborn.test' `
        '1.3.6.1.5.5.7.3.1' `
        $notBefore `
        $leafNotAfter

    Write-CertificateFile $paths.RootCer $rootCertificate
    Write-PrivatePfx $paths.RootPfx $rootCertificate $null $password
    $filePairs = @(
        @($paths.GatewayCer, $paths.GatewayPfx),
        @($paths.WorkerACer, $paths.WorkerAPfx),
        @($paths.WorkerBCer, $paths.WorkerBPfx)
    )
    for ($index = 0; $index -lt $leaves.Count; $index++) {
        Write-CertificateFile `
            $filePairs[$index][0] `
            $leaves[$index].Certificate
        Write-PrivatePfx `
            $filePairs[$index][1] `
            $leaves[$index].Certificate `
            $publicRoot `
            $password
    }

    $sha256 = [Security.Cryptography.HashAlgorithmName]::SHA256
    $gatewayPin =
        $leaves[0].Certificate.GetCertHashString($sha256)
    $workerAPin =
        $leaves[1].Certificate.GetCertHashString($sha256)
    $workerBPin =
        $leaves[2].Certificate.GetCertHashString($sha256)
    $manifest = [ordered]@{
        schemaVersion = 1
        purpose = 'development-only-backhaul-mtls'
        trustStoreInstalled = $false
        passwordEnvironmentVariable = $PasswordEnvironmentVariable
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        notAfterUtc = $leafNotAfter.ToString('O')
        root = [ordered]@{
            pfx = [IO.Path]::GetFileName($paths.RootPfx)
            cer = [IO.Path]::GetFileName($paths.RootCer)
            certificateSha256 =
                $rootCertificate.GetCertHashString($sha256)
        }
        gateway = [ordered]@{
            pfx = [IO.Path]::GetFileName($paths.GatewayPfx)
            cer = [IO.Path]::GetFileName($paths.GatewayCer)
            leafSha256 = $gatewayPin
            ekuOid = '1.3.6.1.5.5.7.3.2'
        }
        workers = @(
            [ordered]@{
                name = 'worker-a'
                pfx = [IO.Path]::GetFileName($paths.WorkerAPfx)
                cer = [IO.Path]::GetFileName($paths.WorkerACer)
                leafSha256 = $workerAPin
                ekuOid = '1.3.6.1.5.5.7.3.1'
            },
            [ordered]@{
                name = 'worker-b'
                pfx = [IO.Path]::GetFileName($paths.WorkerBPfx)
                cer = [IO.Path]::GetFileName($paths.WorkerBCer)
                leafSha256 = $workerBPin
                ekuOid = '1.3.6.1.5.5.7.3.1'
            }
        )
    }
    $json = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        $paths.Manifest,
        $json,
        [Text.UTF8Encoding]::new($false))
    $completed = $true

    [pscustomobject]@{
        OutputDirectory = $resolvedOutput
        RootPfx = $paths.RootPfx
        RootCer = $paths.RootCer
        GatewayPfx = $paths.GatewayPfx
        GatewayCer = $paths.GatewayCer
        WorkerAPfx = $paths.WorkerAPfx
        WorkerACer = $paths.WorkerACer
        WorkerBPfx = $paths.WorkerBPfx
        WorkerBCer = $paths.WorkerBCer
        Manifest = $paths.Manifest
        GatewayLeafSha256 = $gatewayPin
        WorkerALeafSha256 = $workerAPin
        WorkerBLeafSha256 = $workerBPin
        PasswordEnvironmentVariable = $PasswordEnvironmentVariable
        TrustStoreInstalled = $false
        NotAfterUtc = $leafNotAfter
    }
}
finally {
    foreach ($leaf in $leaves) {
        $leaf.Certificate.Dispose()
        $leaf.PublicCertificate.Dispose()
        $leaf.Key.Dispose()
    }
    if ($publicRoot) {
        $publicRoot.Dispose()
    }
    if ($rootCertificate) {
        $rootCertificate.Dispose()
    }
    if ($rootKey) {
        $rootKey.Dispose()
    }
    $password = $null
    if (-not $completed -and $createdOutput -and
        (Test-Path -LiteralPath $resolvedOutput)) {
        [IO.Directory]::Delete($resolvedOutput, $true)
    }
}
