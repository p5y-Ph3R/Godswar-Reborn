[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Current', 'Next')]
    [string]$KeySlot = 'Current',

    [string]$CurrentKeyName =
        'Reborn-Network-Manifest-Development-Current-v1',

    [string]$NextKeyName =
        'Reborn-Network-Manifest-Development-Next-v1',

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [UInt64]$Sequence,

    [string]$OutputPath = (
        Join-Path $PSScriptRoot `
            '..\artifacts\secure-network\RebornNetwork.gwem'),

    [string]$TrustPath,

    [string]$LogicalLoginHost = '127.1.1.110',

    [ValidateRange(1, 65535)]
    [int]$LogicalLoginPort = 5998,

    [string]$TlsLoginHost = 'login.reborn.test',

    [ValidateRange(1, 65535)]
    [int]$TlsLoginPort = 6599,

    [string]$GameDnsSuffix = 'reborn.test',

    [string]$Audience = 'reborn-game',

    [ValidateRange(1, [uint32]::MaxValue)]
    [UInt32]$ServerId = 100,

    [ValidateRange(1, 30)]
    [int]$ValidityDays = 7
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$selectedKeyName = if ($KeySlot -eq 'Current') {
    $CurrentKeyName
} else {
    $NextKeyName
}
$selectedKeyId = if ($KeySlot -eq 'Current') {
    [UInt16]0xD001
} else {
    [UInt16]0xD002
}
if ([string]::IsNullOrWhiteSpace($TrustPath)) {
    $trustName = if ($KeySlot -eq 'Current') {
        'development-manifest-trust.json'
    } else {
        'development-manifest-next-trust.json'
    }
    $TrustPath = Join-Path (
        Join-Path $PSScriptRoot '..\artifacts\secure-network') $trustName
}

Import-Module (
    Join-Path $PSScriptRoot 'SecureEndpointManifestValidation.psm1'
) -Force

function Test-DnsName {
    param([string]$Value, [switch]$AllowIpv4)

    if ($Value.Length -lt 1 -or
        $Value.Length -gt 253 -or
        $Value -cne $Value.ToLowerInvariant() -or
        $Value.EndsWith('.')) {
        return $false
    }
    if ($AllowIpv4) {
        $address = $null
        if ([Net.IPAddress]::TryParse($Value, [ref]$address)) {
            return $address.AddressFamily -eq
                [Net.Sockets.AddressFamily]::InterNetwork -and
                $address.ToString() -ceq $Value
        }
    }
    foreach ($label in $Value.Split('.')) {
        if ($label.Length -lt 1 -or
            $label.Length -gt 63 -or
            $label -notmatch
                '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$') {
            return $false
        }
    }
    return $true
}

function Write-UInt16Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt16]$Value)
    $Bytes[$Offset] = [byte](($Value -shr 8) -band 0xFF)
    $Bytes[$Offset + 1] = [byte]($Value -band 0xFF)
}

function Write-UInt32Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt32]$Value)
    for ($index = 0; $index -lt 4; $index++) {
        $Bytes[$Offset + $index] =
            [byte](($Value -shr ((3 - $index) * 8)) -band 0xFF)
    }
}

function Write-UInt64Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt64]$Value)
    for ($index = 0; $index -lt 8; $index++) {
        $Bytes[$Offset + $index] =
            [byte](($Value -shr ((7 - $index) * 8)) -band 0xFF)
    }
}

function Get-PublicCoordinates {
    param([Security.Cryptography.CngKey]$Key)

    $blob = $Key.Export(
        [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    try {
        if ($blob.Length -ne 72 -or
            [Text.Encoding]::ASCII.GetString($blob, 0, 4) -cne 'ECS1') {
            throw 'The selected CNG key is not ECDSA P-256.'
        }
        $x = New-Object byte[] 32
        $y = New-Object byte[] 32
        [Array]::Copy($blob, 8, $x, 0, 32)
        [Array]::Copy($blob, 40, $y, 0, 32)
        return [pscustomobject]@{ X = $x; Y = $y }
    }
    finally {
        [Array]::Clear($blob, 0, $blob.Length)
    }
}

foreach ($value in @(
    @($LogicalLoginHost, $true, 'logical login host'),
    @($TlsLoginHost, $false, 'TLS login host'),
    @($GameDnsSuffix, $false, 'game DNS suffix')
)) {
    if (-not (Test-DnsName ([string]$value[0]) -AllowIpv4:$value[1])) {
        throw "Invalid canonical $($value[2]): $($value[0])"
    }
}
if ($Audience.Length -lt 1 -or
    $Audience.Length -gt 64 -or
    $Audience -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Audience must be a 1..64 byte protocol token.'
}
if ($LogicalLoginPort -eq $TlsLoginPort) {
    throw 'Logical and TLS login ports must differ.'
}

$provider =
    [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
if (-not [Security.Cryptography.CngKey]::Exists(
        $selectedKeyName,
        $provider,
        [Security.Cryptography.CngKeyOpenOptions]::None)) {
    throw (
        "Development signing key not found: $selectedKeyName. " +
        'Run ManageDevelopmentEndpointManifestKeys.ps1 -Mode Create.'
    )
}
$resolvedTrust = [IO.Path]::GetFullPath($TrustPath)
if (-not (Test-Path -LiteralPath $resolvedTrust -PathType Leaf)) {
    throw "Development trust descriptor not found: $resolvedTrust"
}
$trust = Get-Content -LiteralPath $resolvedTrust -Raw |
    ConvertFrom-Json
if ($trust.schemaVersion -ne 1 -or
    [string]$trust.keyId -ne $selectedKeyId.ToString() -or
    [string]$trust.environment -ne '1') {
    throw 'Development trust descriptor identity is invalid.'
}

$key = [Security.Cryptography.CngKey]::Open(
    $selectedKeyName,
    $provider,
    [Security.Cryptography.CngKeyOpenOptions]::None)
$ecdsa = [Security.Cryptography.ECDsaCng]::new($key)
$ecdsa.HashAlgorithm =
    [Security.Cryptography.CngAlgorithm]::Sha256
try {
    $public = Get-PublicCoordinates $key
    if ([Convert]::ToBase64String($public.X) -cne [string]$trust.x -or
        [Convert]::ToBase64String($public.Y) -cne [string]$trust.y) {
        throw 'CNG signing key does not match the trust descriptor.'
    }

    $logical = [Text.Encoding]::ASCII.GetBytes($LogicalLoginHost)
    $tls = [Text.Encoding]::ASCII.GetBytes($TlsLoginHost)
    $suffix = [Text.Encoding]::ASCII.GetBytes($GameDnsSuffix)
    $audienceBytes = [Text.Encoding]::ASCII.GetBytes($Audience)
    $signedLength =
        72 + $logical.Length + $tls.Length +
        1 + $suffix.Length + 1 + $audienceBytes.Length + 4
    $signed = New-Object byte[] $signedLength
    [Text.Encoding]::ASCII.GetBytes('GWEM').CopyTo($signed, 0)
    Write-UInt32Be $signed 4 ([UInt32]($signedLength + 64))
    Write-UInt16Be $signed 8 72
    Write-UInt16Be $signed 10 1
    Write-UInt16Be $signed 12 0
    $signed[14] = 1
    $signed[15] = 0
    Write-UInt16Be $signed 16 1
    Write-UInt16Be $signed 18 $selectedKeyId
    Write-UInt64Be $signed 24 $Sequence
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    Write-UInt64Be $signed 32 ([UInt64]($now - 300))
    Write-UInt64Be $signed 40 (
        [UInt64]($now + $ValidityDays * 24 * 60 * 60))
    Write-UInt16Be $signed 48 1
    Write-UInt16Be $signed 50 0
    Write-UInt16Be $signed 52 ([UInt16]$LogicalLoginPort)
    Write-UInt16Be $signed 54 ([UInt16]$TlsLoginPort)
    Write-UInt16Be $signed 56 ([UInt16]$logical.Length)
    Write-UInt16Be $signed 58 ([UInt16]$tls.Length)
    $signed[60] = 1
    $signed[61] = 1
    $signed[62] = 1
    Write-UInt32Be $signed 64 ([UInt32]$signedLength)

    $cursor = 72
    $logical.CopyTo($signed, $cursor)
    $cursor += $logical.Length
    $tls.CopyTo($signed, $cursor)
    $cursor += $tls.Length
    $signed[$cursor++] = [byte]$suffix.Length
    $suffix.CopyTo($signed, $cursor)
    $cursor += $suffix.Length
    $signed[$cursor++] = [byte]$audienceBytes.Length
    $audienceBytes.CopyTo($signed, $cursor)
    $cursor += $audienceBytes.Length
    Write-UInt32Be $signed $cursor $ServerId

    $signature = $ecdsa.SignData($signed)
    if ($signature.Length -ne 64) {
        throw 'The CNG provider did not return an IEEE P1363 signature.'
    }
    $manifest = New-Object byte[] ($signed.Length + 64)
    $signed.CopyTo($manifest, 0)
    $signature.CopyTo($manifest, $signed.Length)
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    if (-not $PSCmdlet.ShouldProcess(
            $resolvedOutput,
            "Write signed development endpoint manifest sequence $Sequence")) {
        return
    }
    $parent = Split-Path -Parent $resolvedOutput
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporary =
        "$resolvedOutput.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $manifest)
        if (Test-Path -LiteralPath $resolvedOutput -PathType Leaf) {
            $old =
                "$resolvedOutput.$([Guid]::NewGuid().ToString('N')).old"
            try {
                [IO.File]::Replace(
                    $temporary,
                    $resolvedOutput,
                    $old,
                    $true)
            }
            finally {
                if (Test-Path -LiteralPath $old -PathType Leaf) {
                    Remove-Item -LiteralPath $old -Force
                }
            }
        } else {
            [IO.File]::Move($temporary, $resolvedOutput)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }

    $verified = Read-RebornSecureEndpointManifest `
        -ManifestPath $resolvedOutput `
        -TrustPath $resolvedTrust `
        -InstalledSequenceFloor $Sequence
    [pscustomobject]@{
        Path = $resolvedOutput
        Sha256 = (
            Get-FileHash $resolvedOutput -Algorithm SHA256).Hash
        Sequence = $verified.Sequence
        KeySlot = $KeySlot
        KeyId = $verified.KeyId
        LogicalLogin = (
            "$($verified.LogicalLoginHost):" +
            "$($verified.LogicalLoginPort)")
        TlsLogin = (
            "$($verified.TlsLoginHost):" +
            "$($verified.TlsLoginPort)")
        PrivateKeyExported = $false
    }
}
finally {
    $ecdsa.Dispose()
    $key.Dispose()
}
