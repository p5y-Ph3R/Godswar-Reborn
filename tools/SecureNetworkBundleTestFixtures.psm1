Set-StrictMode -Version Latest

function Write-RebornTestUInt16Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt16]$Value)
    $Bytes[$Offset] = [byte](($Value -shr 8) -band 0xFF)
    $Bytes[$Offset + 1] = [byte]($Value -band 0xFF)
}

function Write-RebornTestUInt32Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt32]$Value)
    for ($index = 0; $index -lt 4; $index++) {
        $Bytes[$Offset + $index] =
            [byte](($Value -shr ((3 - $index) * 8)) -band 0xFF)
    }
}

function Write-RebornTestUInt64Be {
    param([byte[]]$Bytes, [int]$Offset, [UInt64]$Value)
    for ($index = 0; $index -lt 8; $index++) {
        $Bytes[$Offset + $index] =
            [byte](($Value -shr ((7 - $index) * 8)) -band 0xFF)
    }
}

function New-SignedManifestFixture {
    param(
        [string]$ManifestPath,
        [string]$TrustPath,
        [UInt64]$Sequence = 7,
        [DateTimeOffset]$ValidAt = [DateTimeOffset]::UtcNow
    )

    $key = [Security.Cryptography.CngKey]::Create(
        [Security.Cryptography.CngAlgorithm]::ECDsaP256)
    $ecdsa = [Security.Cryptography.ECDsaCng]::new($key)
    $ecdsa.HashAlgorithm =
        [Security.Cryptography.CngAlgorithm]::Sha256
    try {
        $public = $key.Export(
            [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        if ($public.Length -ne 72) {
            throw 'Unexpected ECDSA P-256 public blob size.'
        }
        $x = New-Object byte[] 32
        $y = New-Object byte[] 32
        [Array]::Copy($public, 8, $x, 0, 32)
        [Array]::Copy($public, 40, $y, 0, 32)

        $logical = [Text.Encoding]::ASCII.GetBytes(
            'login-route.reborn.test')
        $tls = [Text.Encoding]::ASCII.GetBytes('login.reborn.test')
        $suffix = [Text.Encoding]::ASCII.GetBytes('reborn.test')
        $audience = [Text.Encoding]::ASCII.GetBytes('reborn-game')
        $signedLength =
            72 + $logical.Length + $tls.Length +
            1 + $suffix.Length + 1 + $audience.Length + 4
        $signed = New-Object byte[] $signedLength
        [Text.Encoding]::ASCII.GetBytes('GWEM').CopyTo($signed, 0)
        Write-RebornTestUInt32Be $signed 4 (
            [UInt32]($signedLength + 64))
        Write-RebornTestUInt16Be $signed 8 72
        Write-RebornTestUInt16Be $signed 10 1
        Write-RebornTestUInt16Be $signed 12 0
        $signed[14] = 1
        $signed[15] = 0
        Write-RebornTestUInt16Be $signed 16 1
        Write-RebornTestUInt16Be $signed 18 0xD001
        Write-RebornTestUInt64Be $signed 24 $Sequence
        $now = $ValidAt.ToUnixTimeSeconds()
        Write-RebornTestUInt64Be $signed 32 ([UInt64]($now - 60))
        Write-RebornTestUInt64Be $signed 40 ([UInt64]($now + 3600))
        Write-RebornTestUInt16Be $signed 48 1
        Write-RebornTestUInt16Be $signed 50 0
        Write-RebornTestUInt16Be $signed 52 5999
        Write-RebornTestUInt16Be $signed 54 6599
        Write-RebornTestUInt16Be $signed 56 ([UInt16]$logical.Length)
        Write-RebornTestUInt16Be $signed 58 ([UInt16]$tls.Length)
        $signed[60] = 1
        $signed[61] = 1
        $signed[62] = 1
        Write-RebornTestUInt32Be $signed 64 ([UInt32]$signedLength)

        $cursor = 72
        $logical.CopyTo($signed, $cursor)
        $cursor += $logical.Length
        $tls.CopyTo($signed, $cursor)
        $cursor += $tls.Length
        $signed[$cursor++] = [byte]$suffix.Length
        $suffix.CopyTo($signed, $cursor)
        $cursor += $suffix.Length
        $signed[$cursor++] = [byte]$audience.Length
        $audience.CopyTo($signed, $cursor)
        $cursor += $audience.Length
        Write-RebornTestUInt32Be $signed $cursor 42

        $signature = $ecdsa.SignData($signed)
        if ($signature.Length -ne 64) {
            throw 'Unexpected ECDSA P-256 signature size.'
        }
        $manifest = New-Object byte[] ($signed.Length + 64)
        $signed.CopyTo($manifest, 0)
        $signature.CopyTo($manifest, $signed.Length)
        [IO.File]::WriteAllBytes($ManifestPath, $manifest)
        [IO.File]::WriteAllText(
            $TrustPath,
            ([ordered]@{
                schemaVersion = 1
                keyId = '53249'
                environment = '1'
                minimumSequence = '1'
                x = [Convert]::ToBase64String($x)
                y = [Convert]::ToBase64String($y)
            } | ConvertTo-Json),
            [Text.UTF8Encoding]::new($false))
    }
    finally {
        $ecdsa.Dispose()
        $key.Dispose()
    }
}

function Write-TestBytes {
    param([string]$Path, [int]$Length, [byte]$Seed)
    $bytes = New-Object byte[] $Length
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [byte](($Seed + $index) % 251)
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Get-TestManagedState {
    param([string]$ClientRoot, [string]$StatePath)

    $result = [ordered]@{}
    foreach ($name in @(
        'Net.dll',
        'NetLegacy.dll',
        'RebornNetwork.gwem'
    )) {
        $path = Join-Path $ClientRoot $name
        $result[$name] = if (Test-Path -LiteralPath $path -PathType Leaf) {
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        } else {
            $null
        }
    }
    $result['activation'] = if (
        Test-Path -LiteralPath $StatePath -PathType Leaf
    ) {
        (Get-FileHash -LiteralPath $StatePath -Algorithm SHA256).Hash
    } else {
        $null
    }
    return [pscustomobject]$result
}

function Test-TestManagedStateEqual {
    param([object]$Left, [object]$Right)

    foreach ($name in @(
        'Net.dll',
        'NetLegacy.dll',
        'RebornNetwork.gwem',
        'activation'
    )) {
        if ($Left.$name -cne $Right.$name) {
            return $false
        }
    }
    return $true
}

Export-ModuleMember -Function @(
    'New-SignedManifestFixture',
    'Write-TestBytes',
    'Assert-True',
    'Get-TestManagedState',
    'Test-TestManagedStateEqual'
)
