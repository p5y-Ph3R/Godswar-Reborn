[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'The development backhaul certificate check requires Windows.'
}

$generator = Join-Path $PSScriptRoot `
    'NewDevelopmentBackhaulCertificates.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'reborn-backhaul-cert-check-' +
    [Guid]::NewGuid().ToString('N'))
$output = Join-Path $testRoot 'certificates'
$missingOutput = Join-Path $testRoot 'missing-password'
$passwordVariable =
    'GODSWAR_BACKHAUL_CERTIFICATE_VALIDATION_PASSWORD'
$missingVariable =
    'GODSWAR_BACKHAUL_CERTIFICATE_VALIDATION_MISSING'
$originalPassword = [Environment]::GetEnvironmentVariable(
    $passwordVariable,
    [EnvironmentVariableTarget]::Process)
$originalMissing = [Environment]::GetEnvironmentVariable(
    $missingVariable,
    [EnvironmentVariableTarget]::Process)
$password = 'bounded-development-validation-only'

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Import-PfxCollection {
    param([string]$Path, [string]$Password)
    $collection =
        New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
    $collection.Import(
        $Path,
        $Password,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    return ,$collection
}

function Assert-RoleCertificateLoad {
    param([string]$Path, [string]$Password)
    $flags = if ($env:OS -ceq 'Windows_NT') {
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet
    } else {
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    }
    $certificate =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $Path,
            $Password,
            $flags)
    try {
        Assert-Condition $certificate.HasPrivateKey `
            "The role PFX did not load its private key: $Path"
    }
    finally {
        $certificate.Dispose()
    }
}

function Assert-Pfx {
    param(
        [string]$PfxPath,
        [string]$CerPath,
        [string]$Password,
        [string]$ExpectedEku,
        [string]$ExpectedPin,
        [string]$RootThumbprint
    )

    $collection = Import-PfxCollection $PfxPath $Password
    $publicCertificate = $null
    try {
        Assert-Condition ($collection.Count -eq 2) `
            "Leaf PFX must contain exactly a leaf and public root: $PfxPath"
        $privateCertificates = @(
            $collection | Where-Object { $_.HasPrivateKey })
        Assert-Condition ($privateCertificates.Count -eq 1) `
            "Leaf PFX must carry exactly one private key: $PfxPath"
        $leaf = $privateCertificates[0]
        $roots = @(
            $collection | Where-Object {
                -not $_.HasPrivateKey -and
                $_.Thumbprint -ceq $RootThumbprint
            })
        Assert-Condition ($roots.Count -eq 1) `
            "Leaf PFX must contain only the matching public root: $PfxPath"

        $constraints = @(
            $leaf.Extensions |
                Where-Object {
                    $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]
                })
        Assert-Condition (
            $constraints.Count -eq 1 -and
            -not $constraints[0].CertificateAuthority
        ) "Leaf basic constraints are invalid: $PfxPath"
        $ekus = @(
            $leaf.Extensions |
                Where-Object {
                    $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
                })
        Assert-Condition ($ekus.Count -eq 1) `
            "Leaf must have exactly one EKU extension: $PfxPath"
        $ekuOids = @(
            $ekus[0].EnhancedKeyUsages |
                ForEach-Object { $_.Value })
        Assert-Condition (
            $ekuOids.Count -eq 1 -and
            $ekuOids[0] -ceq $ExpectedEku
        ) "Leaf EKU is incorrect: $PfxPath"
        $pin = $leaf.GetCertHashString(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        Assert-Condition ($pin -ceq $ExpectedPin) `
            "Leaf SHA-256 pin is incorrect: $PfxPath"
        Assert-Condition ($pin -cmatch '^[0-9A-F]{64}$') `
            "Leaf SHA-256 pin is not canonical uppercase hex: $PfxPath"
        $now = [DateTime]::UtcNow
        Assert-Condition (
            $leaf.NotAfter.ToUniversalTime() -gt $now -and
            $leaf.NotAfter.ToUniversalTime() -le $now.AddDays(3)
        ) "Leaf validity is outside the bounded test window: $PfxPath"

        $publicCertificate =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $CerPath)
        Assert-Condition (-not $publicCertificate.HasPrivateKey) `
            "Public CER unexpectedly contains a private key: $CerPath"
        Assert-Condition (
            $publicCertificate.Thumbprint -ceq $leaf.Thumbprint
        ) "Public CER does not match the PFX leaf: $CerPath"
        return $pin
    }
    finally {
        if ($publicCertificate) {
            $publicCertificate.Dispose()
        }
        foreach ($certificate in $collection) {
            $certificate.Dispose()
        }
    }
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    [Environment]::SetEnvironmentVariable(
        $missingVariable,
        $null,
        [EnvironmentVariableTarget]::Process)
    $missingRejected = $false
    try {
        & $generator `
            -OutputDirectory $missingOutput `
            -PasswordEnvironmentVariable $missingVariable | Out-Null
    }
    catch {
        $missingRejected = $true
    }
    Assert-Condition $missingRejected `
        'The generator accepted a missing password.'
    Assert-Condition (
        -not (Test-Path -LiteralPath $missingOutput)
    ) 'Missing-password rejection created an output directory.'

    [Environment]::SetEnvironmentVariable(
        $passwordVariable,
        $password,
        [EnvironmentVariableTarget]::Process)
    $result = & $generator `
        -OutputDirectory $output `
        -PasswordEnvironmentVariable $passwordVariable `
        -ValidDays 2
    Assert-Condition (@($result).Count -eq 1) `
        'The generator emitted unexpected pipeline output.'
    Assert-Condition (
        $result.TrustStoreInstalled -eq $false
    ) 'The generator did not report trust-store isolation.'

    $expectedFiles = @(
        $result.RootPfx,
        $result.RootCer,
        $result.GatewayPfx,
        $result.GatewayCer,
        $result.WorkerAPfx,
        $result.WorkerACer,
        $result.WorkerBPfx,
        $result.WorkerBCer,
        $result.Manifest
    )
    foreach ($path in $expectedFiles) {
        Assert-Condition (
            Test-Path -LiteralPath $path -PathType Leaf
        ) "Expected certificate artifact is missing: $path"
    }

    $rootCollection = Import-PfxCollection $result.RootPfx $password
    $rootPublic = $null
    try {
        Assert-Condition ($rootCollection.Count -eq 1) `
            'Root PFX must contain exactly one certificate.'
        Assert-Condition ($rootCollection[0].HasPrivateKey) `
            'Root PFX does not contain its private key.'
        $rootConstraints = @(
            $rootCollection[0].Extensions |
                Where-Object {
                    $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]
                })
        Assert-Condition (
            $rootConstraints.Count -eq 1 -and
            $rootConstraints[0].CertificateAuthority
        ) 'Root PFX does not contain a CA certificate.'
        $now = [DateTime]::UtcNow
        Assert-Condition (
            $rootCollection[0].NotAfter.ToUniversalTime() -gt $now -and
            $rootCollection[0].NotAfter.ToUniversalTime() -le
                $now.AddDays(4)
        ) 'Root validity is outside the bounded test window.'
        $rootThumbprint = $rootCollection[0].Thumbprint
        $rootPublic =
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $result.RootCer)
        Assert-Condition (-not $rootPublic.HasPrivateKey) `
            'Root CER unexpectedly contains a private key.'
        Assert-Condition (
            $rootPublic.Thumbprint -ceq $rootThumbprint
        ) 'Root CER does not match the root PFX.'
    }
    finally {
        if ($rootPublic) {
            $rootPublic.Dispose()
        }
        foreach ($certificate in $rootCollection) {
            $certificate.Dispose()
        }
    }

    $gatewayPin = Assert-Pfx `
        $result.GatewayPfx $result.GatewayCer $password `
        '1.3.6.1.5.5.7.3.2' $result.GatewayLeafSha256 `
        $rootThumbprint
    $workerAPin = Assert-Pfx `
        $result.WorkerAPfx $result.WorkerACer $password `
        '1.3.6.1.5.5.7.3.1' $result.WorkerALeafSha256 `
        $rootThumbprint
    $workerBPin = Assert-Pfx `
        $result.WorkerBPfx $result.WorkerBCer $password `
        '1.3.6.1.5.5.7.3.1' $result.WorkerBLeafSha256 `
        $rootThumbprint
    Assert-RoleCertificateLoad $result.GatewayPfx $password
    Assert-RoleCertificateLoad $result.WorkerAPfx $password
    Assert-RoleCertificateLoad $result.WorkerBPfx $password
    Assert-Condition (
        $gatewayPin -cne $workerAPin -and
        $gatewayPin -cne $workerBPin -and
        $workerAPin -cne $workerBPin
    ) 'Generated backhaul leaves do not have unique pins.'

    $manifest = Get-Content -Raw -LiteralPath $result.Manifest |
        ConvertFrom-Json
    Assert-Condition (
        $manifest.gateway.leafSha256 -ceq $gatewayPin -and
        $manifest.workers[0].leafSha256 -ceq $workerAPin -and
        $manifest.workers[1].leafSha256 -ceq $workerBPin
    ) 'The manifest pins do not match the generated leaves.'
    Assert-Condition (
        $manifest.trustStoreInstalled -eq $false
    ) 'The manifest does not declare trust-store isolation.'

    $trustedRoot = @(
        Get-ChildItem Cert:\CurrentUser\Root |
            Where-Object { $_.Thumbprint -ceq $rootThumbprint })
    Assert-Condition ($trustedRoot.Count -eq 0) `
        'The development root was installed into CurrentUser trust.'

    $directoryAcl = Get-Acl -LiteralPath $output
    Assert-Condition $directoryAcl.AreAccessRulesProtected `
        'The output directory still inherits ACL entries.'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $unexpectedRules = @(
        $directoryAcl.Access |
            Where-Object {
                $_.IdentityReference.Value -cne $identity -or
                $_.AccessControlType -ne
                    [Security.AccessControl.AccessControlType]::Allow
            })
    Assert-Condition ($unexpectedRules.Count -eq 0) `
        'The output directory ACL grants an unexpected principal.'
    foreach ($path in $expectedFiles) {
        $unexpectedFileRules = @(
            (Get-Acl -LiteralPath $path).Access |
                Where-Object {
                    $_.IdentityReference.Value -cne $identity -or
                    $_.AccessControlType -ne
                        [Security.AccessControl.AccessControlType]::Allow
                })
        Assert-Condition ($unexpectedFileRules.Count -eq 0) `
            "A certificate artifact grants an unexpected principal: $path"
    }

    $hashesBefore = @{}
    foreach ($path in $expectedFiles) {
        $hashesBefore[$path] =
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $overwriteRejected = $false
    try {
        & $generator `
            -OutputDirectory $output `
            -PasswordEnvironmentVariable $passwordVariable | Out-Null
    }
    catch {
        $overwriteRejected = $true
    }
    Assert-Condition $overwriteRejected `
        'The generator accepted an existing output directory.'
    foreach ($path in $expectedFiles) {
        Assert-Condition (
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ceq
                $hashesBefore[$path]
        ) "Overwrite rejection changed an existing artifact: $path"
    }

    [pscustomobject]@{
        Result = 'Passed'
        ArtifactCount = $expectedFiles.Count
        PfxContentsVerified = $true
        PlatformKeyStorageLoadVerified = $true
        EnhancedKeyUsagesVerified = $true
        LeafPinsVerified = $true
        RestrictedAclVerified = $true
        TrustStoreUnchanged = $true
        MissingPasswordRejected = $true
        ExistingOutputRejected = $true
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        $passwordVariable,
        $originalPassword,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $missingVariable,
        $originalMissing,
        [EnvironmentVariableTarget]::Process)
    $password = $null
    if (Test-Path -LiteralPath $testRoot) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
