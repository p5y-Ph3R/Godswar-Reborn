[CmdletBinding()]
param(
    [string]$OutputDirectory,

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$PasswordEnvironmentVariable =
        'GODSWAR_SECURE_CERTIFICATE_PASSWORD',

    [ValidateRange(1, 30)]
    [int]$ValidDays = 14,

    [switch]$InstallCurrentUserTrust
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    throw 'Development Schannel certificates can only be created on Windows.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\network-tls'
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutput) {
    throw (
        'Refusing to alter an existing directory or its ACL. ' +
        "Choose a new development-certificate directory: $resolvedOutput"
    )
}

$password = [Environment]::GetEnvironmentVariable(
    $PasswordEnvironmentVariable,
    [EnvironmentVariableTarget]::Process
)
if ([string]::IsNullOrEmpty($password)) {
    throw (
        "Set process environment variable $PasswordEnvironmentVariable " +
        'to a nonempty PFX password before running this command.'
    )
}

$rootPath = Join-Path $resolvedOutput 'reborn-development-root.cer'
$serverPath = Join-Path $resolvedOutput 'reborn-development-server.pfx'
$serverCerPath = Join-Path $resolvedOutput 'reborn-development-server.cer'
$receiptPath = Join-Path $resolvedOutput 'current-user-trust-receipt.json'

foreach ($path in @($rootPath, $serverPath, $serverCerPath, $receiptPath)) {
    if (Test-Path -LiteralPath $path) {
        throw "Refusing to overwrite existing development trust material: $path"
    }
}

New-Item -ItemType Directory -Path $resolvedOutput | Out-Null

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $resolvedOutput `
    /inheritance:r `
    /grant:r "${currentIdentity}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not restrict development-certificate directory ACL: $resolvedOutput"
}

$hashAlgorithm = [Security.Cryptography.HashAlgorithmName]::SHA256
$signaturePadding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
$notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
$leafNotAfter = $notBefore.AddDays($ValidDays)
$rootNotAfter = $leafNotAfter.AddDays(1)

$rootKey = [Security.Cryptography.RSA]::Create(3072)
$leafKey = [Security.Cryptography.RSA]::Create(2048)
$rootCertificate = $null
$leafPublicCertificate = $null
$leafCertificate = $null
$pfxRootCertificate = $null

try {
    $rootRequest = New-Object Security.Cryptography.X509Certificates.CertificateRequest(
        'CN=Reborn Development Root CA',
        $rootKey,
        $hashAlgorithm,
        $signaturePadding
    )
    $rootRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
            $true,
            $false,
            0,
            $true
        ))
    )
    $rootRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            (
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign
            ),
            $true
        ))
    )
    $rootRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(
            $rootRequest.PublicKey,
            $false
        ))
    )
    $rootCertificate = $rootRequest.CreateSelfSigned(
        $notBefore.AddDays(-1),
        $rootNotAfter
    )

    $leafRequest = New-Object Security.Cryptography.X509Certificates.CertificateRequest(
        'CN=login.reborn.test',
        $leafKey,
        $hashAlgorithm,
        $signaturePadding
    )
    $leafRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
            $false,
            $false,
            0,
            $true
        ))
    )
    $leafRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            (
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
            ),
            $true
        ))
    )
    $serverAuthOids =
        New-Object Security.Cryptography.OidCollection
    [void]$serverAuthOids.Add(
        (New-Object Security.Cryptography.Oid(
            '1.3.6.1.5.5.7.3.1',
            'TLS Web Server Authentication'
        ))
    )
    $leafRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            $serverAuthOids,
            $true
        ))
    )
    $subjectAlternativeNames =
        New-Object Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
    $subjectAlternativeNames.AddDnsName('login.reborn.test')
    $subjectAlternativeNames.AddDnsName('game.reborn.test')
    $leafRequest.CertificateExtensions.Add(
        $subjectAlternativeNames.Build($true)
    )
    $leafRequest.CertificateExtensions.Add(
        (New-Object Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(
            $leafRequest.PublicKey,
            $false
        ))
    )

    $serial = New-Object byte[] 16
    $serialGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $serialGenerator.GetBytes($serial)
    }
    finally {
        $serialGenerator.Dispose()
    }
    $serial[0] = $serial[0] -band 0x7F
    if (($serial | Where-Object { $_ -ne 0 }).Count -eq 0) {
        $serial[15] = 1
    }

    $leafPublicCertificate = $leafRequest.Create(
        $rootCertificate,
        $notBefore,
        $leafNotAfter,
        $serial
    )
    $leafCertificate =
        [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
            $leafPublicCertificate,
            $leafKey
        )

    [IO.File]::WriteAllBytes(
        $rootPath,
        $rootCertificate.Export(
            [Security.Cryptography.X509Certificates.X509ContentType]::Cert
        )
    )
    [IO.File]::WriteAllBytes(
        $serverCerPath,
        $leafCertificate.Export(
            [Security.Cryptography.X509Certificates.X509ContentType]::Cert
        )
    )
    $pfxRootCertificate =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $rootCertificate.RawData
        )
    $pfxCertificates =
        New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
    [void]$pfxCertificates.Add($leafCertificate)
    [void]$pfxCertificates.Add($pfxRootCertificate)
    $pfxBytes = $pfxCertificates.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $password
    )
    try {
        [IO.File]::WriteAllBytes($serverPath, $pfxBytes)
    }
    finally {
        [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $rootSha256 = (
            [BitConverter]::ToString(
                $sha256.ComputeHash($rootCertificate.RawData)
            )
        ).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }

    $installedByScript = $false
    $receipt = [ordered]@{
        version = 1
        storeLocation = 'CurrentUser'
        storeName = 'Root'
        thumbprint = $rootCertificate.Thumbprint
        rootSha256 = $rootSha256
        installedByScript = $false
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($InstallCurrentUserTrust) {
        $publicRoot =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $rootCertificate.RawData
            )
        $store = New-Object Security.Cryptography.X509Certificates.X509Store(
            [Security.Cryptography.X509Certificates.StoreName]::Root,
            [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
        )
        try {
            $store.Open(
                [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
            )
            $existing = $store.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $rootCertificate.Thumbprint,
                $false
            )
            if ($existing.Count -eq 0) {
                # Write the guarded cleanup receipt before changing trust. If
                # the process is interrupted during Store.Add, cleanup can
                # safely remove the exact root or report it already absent.
                $receipt.installedByScript = $true
                $receipt |
                    ConvertTo-Json |
                    Set-Content -LiteralPath $receiptPath -Encoding UTF8
                $store.Add($publicRoot)
                $installedByScript = $true
            }
        }
        finally {
            $store.Dispose()
            $publicRoot.Dispose()
        }
    }

    $receipt.installedByScript = $installedByScript
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8

    [pscustomobject]@{
        RootCertificate = $rootPath
        ServerCertificate = $serverPath
        ServerPublicCertificate = $serverCerPath
        TrustReceipt = $receiptPath
        RootThumbprint = $rootCertificate.Thumbprint
        CurrentUserTrustInstalled = $installedByScript
        PasswordEnvironmentVariable = $PasswordEnvironmentVariable
        LoginDnsName = 'login.reborn.test'
        GameDnsName = 'game.reborn.test'
        NotAfterUtc = $leafCertificate.NotAfter.ToUniversalTime()
    }
}
finally {
    if ($pfxRootCertificate) {
        $pfxRootCertificate.Dispose()
    }
    if ($leafCertificate) {
        $leafCertificate.Dispose()
    }
    if ($leafPublicCertificate) {
        $leafPublicCertificate.Dispose()
    }
    if ($rootCertificate) {
        $rootCertificate.Dispose()
    }
    $leafKey.Dispose()
    $rootKey.Dispose()
    $password = $null
}
