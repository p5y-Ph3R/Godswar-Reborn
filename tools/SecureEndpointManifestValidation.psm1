Set-StrictMode -Version Latest

$script:ManifestMinimumBytes = 146
$script:ManifestMaximumBytes = 3258
$script:ManifestHeaderBytes = 72
$script:ManifestSignatureBytes = 64
$script:ManifestMaximumValiditySeconds = 31 * 24 * 60 * 60
$script:ManifestTrustMaximumBytes = 4096

function Get-RebornByteSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($Bytes)
        ).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Read-RebornBoundedFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MinimumBytes,
        [int]$MaximumBytes,
        [string]$Label
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    try {
        $stream = [IO.File]::Open(
            $resolved,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
    }
    catch {
        throw "$Label could not be opened: $resolved"
    }
    try {
        if ($stream.Length -lt $MinimumBytes -or
            $stream.Length -gt $MaximumBytes) {
            throw "$Label size is outside its bounded range."
        }
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read(
                $bytes,
                $offset,
                $bytes.Length - $offset)
            if ($read -eq 0) {
                throw "$Label was truncated while being read."
            }
            $offset += $read
        }
        [pscustomobject]@{
            Path = $resolved
            Bytes = $bytes
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-RebornUInt16BigEndian {
    param([byte[]]$Bytes, [int]$Offset)
    return [UInt16](
        ([int]$Bytes[$Offset] -shl 8) -bor
        [int]$Bytes[$Offset + 1])
}

function Read-RebornUInt32BigEndian {
    param([byte[]]$Bytes, [int]$Offset)
    $value = [UInt64]0
    for ($index = 0; $index -lt 4; $index++) {
        $value = ($value -shl 8) -bor [UInt64]$Bytes[$Offset + $index]
    }
    return [UInt32]$value
}

function Read-RebornUInt64BigEndian {
    param([byte[]]$Bytes, [int]$Offset)
    $value = [UInt64]0
    for ($index = 0; $index -lt 8; $index++) {
        $value = ($value -shl 8) -bor [UInt64]$Bytes[$Offset + $index]
    }
    return $value
}

function Test-RebornAsciiDnsName {
    param(
        [Parameter(Mandatory)][string]$Value,
        [switch]$AllowIpv4
    )

    if ($Value.Length -lt 1 -or
        $Value.Length -gt 253 -or
        $Value -cne $Value.ToLowerInvariant() -or
        $Value.EndsWith('.')) {
        return $false
    }
    if ($AllowIpv4 -and $Value -match '^[0-9]+(?:\.[0-9]+){3}$') {
        $address = $null
        return [Net.IPAddress]::TryParse($Value, [ref]$address) -and
            $address.AddressFamily -eq
                [Net.Sockets.AddressFamily]::InterNetwork -and
            $address.ToString() -ceq $Value
    }

    foreach ($label in $Value.Split('.')) {
        if ($label.Length -lt 1 -or
            $label.Length -gt 63 -or
            $label -notmatch '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$') {
            return $false
        }
    }
    return $true
}

function Read-RebornAscii {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [int]$Length,
        [string]$Label
    )

    if ($Length -lt 1 -or $Offset -lt 0 -or
        $Offset + $Length -gt $Bytes.Length) {
        throw "$Label has an invalid manifest range."
    }
    for ($index = 0; $index -lt $Length; $index++) {
        if ($Bytes[$Offset + $index] -gt 0x7F -or
            $Bytes[$Offset + $index] -eq 0) {
            throw "$Label is not nonempty ASCII."
        }
    }
    return [Text.Encoding]::ASCII.GetString($Bytes, $Offset, $Length)
}

function Read-RebornManifestTrust {
    param([Parameter(Mandatory)][string]$Path)

    $file = Read-RebornBoundedFile `
        $Path 2 $script:ManifestTrustMaximumBytes `
        'Manifest trust descriptor'
    $bytes = $file.Bytes
    $offset = 0
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        $offset = 3
    }
    try {
        $json = [Text.UTF8Encoding]::new(
            $false,
            $true
        ).GetString($bytes, $offset, $bytes.Length - $offset)
        $trust = $json | ConvertFrom-Json
    }
    catch {
        throw 'Manifest trust descriptor is not strict UTF-8 JSON.'
    }
    if ($trust.schemaVersion -ne 1) {
        throw 'Unsupported manifest trust descriptor.'
    }

    $keyId = [UInt16]0
    $environment = [UInt64]0
    $minimumSequence = [UInt64]0
    if (-not [UInt16]::TryParse([string]$trust.keyId, [ref]$keyId) -or
        $keyId -eq 0) {
        throw 'Manifest trust keyId must be a nonzero UInt16.'
    }
    if (-not [UInt64]::TryParse(
            [string]$trust.environment,
            [ref]$environment) -or
        $environment -lt 1 -or $environment -gt 3) {
        throw 'Manifest trust environment must be 1, 2, or 3.'
    }
    if (-not [UInt64]::TryParse(
            [string]$trust.minimumSequence,
            [ref]$minimumSequence) -or
        $minimumSequence -eq 0 -or
        $minimumSequence -gt [Int64]::MaxValue) {
        throw 'Manifest trust minimumSequence must be 1..Int64.MaxValue.'
    }

    try {
        $x = [Convert]::FromBase64String([string]$trust.x)
        $y = [Convert]::FromBase64String([string]$trust.y)
    }
    catch {
        throw 'Manifest trust coordinates are not valid base64.'
    }
    if ($x.Length -ne 32 -or $y.Length -ne 32) {
        throw 'Manifest trust P-256 coordinates must each contain 32 bytes.'
    }

    [pscustomobject]@{
        Path = $file.Path
        Sha256 = Get-RebornByteSha256 $bytes
        KeyId = $keyId
        Environment = $environment
        MinimumSequence = $minimumSequence
        X = $x
        Y = $y
    }
}

function Test-RebornManifestSignature {
    param(
        [byte[]]$SignedBytes,
        [byte[]]$Signature,
        [byte[]]$X,
        [byte[]]$Y
    )

    $blob = New-Object byte[] 72
    [Text.Encoding]::ASCII.GetBytes('ECS1').CopyTo($blob, 0)
    [BitConverter]::GetBytes([UInt32]32).CopyTo($blob, 4)
    $X.CopyTo($blob, 8)
    $Y.CopyTo($blob, 40)
    $key = $null
    $ecdsa = $null
    try {
        $key = [Security.Cryptography.CngKey]::Import(
            $blob,
            [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        $ecdsa = New-Object Security.Cryptography.ECDsaCng($key)
        $ecdsa.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
        return $ecdsa.VerifyData($SignedBytes, $Signature)
    }
    finally {
        if ($null -ne $ecdsa) {
            $ecdsa.Dispose()
        }
        if ($null -ne $key) {
            $key.Dispose()
        }
        [Array]::Clear($blob, 0, $blob.Length)
    }
}

function Read-RebornSecureEndpointManifestCore {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$TrustPath,

        [UInt64]$InstalledSequenceFloor = 0,

        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow,

        [switch]$EnforceTimeValidity
    )

    $file = Read-RebornBoundedFile `
        $ManifestPath `
        $script:ManifestMinimumBytes `
        $script:ManifestMaximumBytes `
        'Endpoint manifest'
    $bytes = $file.Bytes
    $manifestSha256 = Get-RebornByteSha256 $bytes
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -cne 'GWEM') {
        throw 'Endpoint manifest magic is invalid.'
    }

    $totalBytes = Read-RebornUInt32BigEndian $bytes 4
    $headerBytes = Read-RebornUInt16BigEndian $bytes 8
    $major = Read-RebornUInt16BigEndian $bytes 10
    $minor = Read-RebornUInt16BigEndian $bytes 12
    $environment = [UInt64]$bytes[14]
    $flags = $bytes[15]
    $algorithm = Read-RebornUInt16BigEndian $bytes 16
    $keyId = Read-RebornUInt16BigEndian $bytes 18
    $sequence = Read-RebornUInt64BigEndian $bytes 24
    $notBefore = Read-RebornUInt64BigEndian $bytes 32
    $notAfter = Read-RebornUInt64BigEndian $bytes 40
    $protocolMajor = Read-RebornUInt16BigEndian $bytes 48
    $protocolMinor = Read-RebornUInt16BigEndian $bytes 50
    $logicalPort = Read-RebornUInt16BigEndian $bytes 52
    $tlsPort = Read-RebornUInt16BigEndian $bytes 54
    $logicalLength = Read-RebornUInt16BigEndian $bytes 56
    $tlsLength = Read-RebornUInt16BigEndian $bytes 58
    $suffixCount = [int]$bytes[60]
    $audienceCount = [int]$bytes[61]
    $serverCount = [int]$bytes[62]
    $signedBytes = Read-RebornUInt32BigEndian $bytes 64

    if ($totalBytes -ne $bytes.Length -or
        $headerBytes -ne $script:ManifestHeaderBytes -or
        $major -ne 1 -or $minor -ne 0 -or
        $environment -lt 1 -or $environment -gt 3 -or
        $flags -ne 0 -or $algorithm -ne 1 -or $keyId -eq 0 -or
        (Read-RebornUInt32BigEndian $bytes 20) -ne 0 -or
        $sequence -eq 0 -or
        $notAfter -le $notBefore -or
        $notAfter - $notBefore -gt $script:ManifestMaximumValiditySeconds -or
        $protocolMajor -ne 1 -or $protocolMinor -ne 0 -or
        $logicalPort -eq 0 -or $tlsPort -eq 0 -or
        $logicalPort -eq $tlsPort -or
        $logicalLength -lt 1 -or $logicalLength -gt 253 -or
        $tlsLength -lt 1 -or $tlsLength -gt 253 -or
        $suffixCount -lt 1 -or $suffixCount -gt 8 -or
        $audienceCount -lt 1 -or $audienceCount -gt 8 -or
        $serverCount -lt 1 -or $serverCount -gt 16 -or
        $bytes[63] -ne 0 -or
        $signedBytes + $script:ManifestSignatureBytes -ne $bytes.Length -or
        (Read-RebornUInt32BigEndian $bytes 68) -ne 0) {
        throw 'Endpoint manifest header violates the secure v1 policy.'
    }

    if ($EnforceTimeValidity) {
        $nowSeconds = [UInt64][Math]::Floor(
            ($Now.ToUniversalTime() -
                [DateTimeOffset]::new(
                    [DateTime]::SpecifyKind(
                        [DateTime]::new(1970, 1, 1),
                        [DateTimeKind]::Utc))).TotalSeconds)
        if ($nowSeconds -lt $notBefore -or $nowSeconds -gt $notAfter) {
            throw 'Endpoint manifest is outside its validity interval.'
        }
    }

    $trust = Read-RebornManifestTrust $TrustPath
    if ($keyId -ne $trust.KeyId -or
        $environment -ne $trust.Environment -or
        $sequence -lt $trust.MinimumSequence -or
        $sequence -lt $InstalledSequenceFloor) {
        throw 'Endpoint manifest key, environment, or sequence floor is not trusted.'
    }

    $cursor = $script:ManifestHeaderBytes
    $logicalHost = Read-RebornAscii `
        $bytes $cursor $logicalLength 'Logical login host'
    $cursor += $logicalLength
    $tlsHost = Read-RebornAscii $bytes $cursor $tlsLength 'TLS login host'
    $cursor += $tlsLength
    if (-not (Test-RebornAsciiDnsName $logicalHost -AllowIpv4) -or
        -not (Test-RebornAsciiDnsName $tlsHost)) {
        throw 'Endpoint manifest login host is not canonical.'
    }

    $suffixes = @()
    for ($index = 0; $index -lt $suffixCount; $index++) {
        if ($cursor -ge $signedBytes) {
            throw 'Endpoint manifest suffix list is truncated.'
        }
        $length = [int]$bytes[$cursor++]
        $value = Read-RebornAscii $bytes $cursor $length 'Game suffix'
        $cursor += $length
        if (-not (Test-RebornAsciiDnsName $value) -or
            $suffixes -ccontains $value) {
            throw 'Endpoint manifest game suffix is invalid or duplicated.'
        }
        $suffixes += $value
    }

    $audiences = @()
    for ($index = 0; $index -lt $audienceCount; $index++) {
        if ($cursor -ge $signedBytes) {
            throw 'Endpoint manifest audience list is truncated.'
        }
        $length = [int]$bytes[$cursor++]
        $value = Read-RebornAscii $bytes $cursor $length 'Audience'
        $cursor += $length
        if ($length -gt 64 -or
            $value -notmatch '^[A-Za-z0-9._-]+$' -or
            $audiences -ccontains $value) {
            throw 'Endpoint manifest audience is invalid or duplicated.'
        }
        $audiences += $value
    }

    $serverIds = @()
    for ($index = 0; $index -lt $serverCount; $index++) {
        if ($cursor + 4 -gt $signedBytes) {
            throw 'Endpoint manifest server list is truncated.'
        }
        $value = Read-RebornUInt32BigEndian $bytes $cursor
        $cursor += 4
        if ($value -eq 0 -or $serverIds -contains $value) {
            throw 'Endpoint manifest server ID is zero or duplicated.'
        }
        $serverIds += $value
    }
    if ($cursor -ne $signedBytes) {
        throw 'Endpoint manifest contains trailing signed body bytes.'
    }

    $signed = New-Object byte[] $signedBytes
    [Array]::Copy($bytes, 0, $signed, 0, $signed.Length)
    $signature = New-Object byte[] $script:ManifestSignatureBytes
    [Array]::Copy(
        $bytes,
        $signedBytes,
        $signature,
        0,
        $signature.Length)
    try {
        if (-not (Test-RebornManifestSignature `
            $signed $signature $trust.X $trust.Y)) {
            throw 'Endpoint manifest signature verification failed.'
        }
    }
    finally {
        [Array]::Clear($signed, 0, $signed.Length)
        [Array]::Clear($signature, 0, $signature.Length)
    }

    [pscustomobject]@{
        Path = $file.Path
        ManifestSha256 = $manifestSha256
        TrustSha256 = $trust.Sha256
        Environment = [UInt64]$environment
        Sequence = [UInt64]$sequence
        KeyId = [UInt16]$keyId
        LogicalLoginHost = $logicalHost
        LogicalLoginPort = [UInt16]$logicalPort
        TlsLoginHost = $tlsHost
        TlsLoginPort = [UInt16]$tlsPort
        GameSuffixes = $suffixes
        Audiences = $audiences
        ServerIds = $serverIds
        NotBefore = [UInt64]$notBefore
        NotAfter = [UInt64]$notAfter
    }
}

function Read-RebornSecureEndpointManifest {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$TrustPath,

        [UInt64]$InstalledSequenceFloor = 0,

        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
    )

    Read-RebornSecureEndpointManifestCore `
        -ManifestPath $ManifestPath `
        -TrustPath $TrustPath `
        -InstalledSequenceFloor $InstalledSequenceFloor `
        -Now $Now `
        -EnforceTimeValidity
}

function Read-RebornSecureEndpointManifestForRestore {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$TrustPath,

        [UInt64]$InstalledSequenceFloor = 0
    )

    # Restore deliberately ignores only the signed validity interval. The
    # shared core still enforces exact structure, hashes, trust identity,
    # environment, sequence floor, and signature.
    Read-RebornSecureEndpointManifestCore `
        -ManifestPath $ManifestPath `
        -TrustPath $TrustPath `
        -InstalledSequenceFloor $InstalledSequenceFloor
}

Export-ModuleMember -Function @(
    'Read-RebornSecureEndpointManifest',
    'Read-RebornSecureEndpointManifestForRestore'
)
