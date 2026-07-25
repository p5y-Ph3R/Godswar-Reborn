[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Create', 'Remove')]
    [string]$Mode = 'Status',

    [string]$CurrentKeyName =
        'Reborn-Network-Manifest-Development-Current-v1',

    [string]$NextKeyName =
        'Reborn-Network-Manifest-Development-Next-v1',

    [string]$HeaderPath = (
        Join-Path $PSScriptRoot `
            '..\client\network-shim\src\SecureClientManifestDevelopmentKeys.generated.h'),

    [string]$TrustPath = (
        Join-Path $PSScriptRoot `
            '..\artifacts\secure-network\development-manifest-trust.json'),

    [string]$NextTrustPath = (
        Join-Path $PSScriptRoot `
            '..\artifacts\secure-network\development-manifest-next-trust.json'),

    [switch]$AllowKeyRemoval
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$provider =
    [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$openOptions = [Security.Cryptography.CngKeyOpenOptions]::None

function Test-KeyExists {
    param([string]$Name)
    return [Security.Cryptography.CngKey]::Exists(
        $Name,
        $provider,
        $openOptions)
}

function Open-Key {
    param([string]$Name)
    return [Security.Cryptography.CngKey]::Open(
        $Name,
        $provider,
        $openOptions)
}

function Get-KeyStatus {
    param([string]$Name)

    if (-not (Test-KeyExists $Name)) {
        return [pscustomobject]@{
            Exists = $false
            Valid = $false
            Exportable = $false
        }
    }
    $key = Open-Key $Name
    try {
        $exportable =
            $key.ExportPolicy -ne
                [Security.Cryptography.CngExportPolicies]::None
        return [pscustomobject]@{
            Exists = $true
            Valid = (
                $key.Algorithm.Algorithm -ceq 'ECDSA_P256' -and
                ($key.KeyUsage -band
                    [Security.Cryptography.CngKeyUsages]::Signing) -ne 0 -and
                -not $exportable)
            Exportable = $exportable
        }
    }
    finally {
        $key.Dispose()
    }
}

function New-SigningKey {
    param([string]$Name)

    $parameters =
        [Security.Cryptography.CngKeyCreationParameters]::new()
    $parameters.Provider = $provider
    $parameters.ExportPolicy =
        [Security.Cryptography.CngExportPolicies]::None
    $parameters.KeyUsage =
        [Security.Cryptography.CngKeyUsages]::Signing
    $parameters.KeyCreationOptions =
        [Security.Cryptography.CngKeyCreationOptions]::None
    return [Security.Cryptography.CngKey]::Create(
        [Security.Cryptography.CngAlgorithm]::ECDsaP256,
        $Name,
        $parameters)
}

function Get-PublicCoordinates {
    param([Security.Cryptography.CngKey]$Key)

    $blob = $Key.Export(
        [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    try {
        if ($blob.Length -ne 72 -or
            [Text.Encoding]::ASCII.GetString($blob, 0, 4) -cne 'ECS1') {
            throw 'The manifest key is not an ECDSA P-256 public key.'
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

function Format-ByteArray {
    param([byte[]]$Bytes)

    $lines = @()
    for ($offset = 0; $offset -lt $Bytes.Length; $offset += 8) {
        $values = for (
            $index = $offset;
            $index -lt [Math]::Min($offset + 8, $Bytes.Length);
            $index++
        ) {
            '0x{0:X2}' -f $Bytes[$index]
        }
        $lines += '    ' + ($values -join ', ') + ','
    }
    return $lines -join "`r`n"
}

function Write-TextAtomic {
    param([string]$Path, [string]$Text)

    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $resolved
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporary = "$resolved.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporary,
            $Text,
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            $old = "$resolved.$([Guid]::NewGuid().ToString('N')).old"
            try {
                [IO.File]::Replace($temporary, $resolved, $old, $true)
            }
            finally {
                if (Test-Path -LiteralPath $old -PathType Leaf) {
                    Remove-Item -LiteralPath $old -Force
                }
            }
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-TextArtifactSnapshot {
    param([string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    $exists = Test-Path -LiteralPath $resolved -PathType Leaf
    [pscustomobject]@{
        Path = $resolved
        Existed = $exists
        Text = if ($exists) {
            [IO.File]::ReadAllText(
                $resolved,
                [Text.UTF8Encoding]::new($false, $true))
        } else {
            $null
        }
    }
}

function Restore-TextArtifactSnapshot {
    param([object]$Snapshot)

    if ($Snapshot.Existed) {
        Write-TextAtomic $Snapshot.Path $Snapshot.Text
    } elseif (Test-Path -LiteralPath $Snapshot.Path -PathType Leaf) {
        Remove-Item -LiteralPath $Snapshot.Path -Force
    }
}

function Write-PublicArtifacts {
    param(
        [object]$Current,
        [object]$Next
    )

    $header = @"
#pragma once

#include <cstdint>

namespace godswar::network::development_manifest_keys {

// Generated public verification keys. Matching private keys are non-exportable
// CurrentUser CNG keys and must never be committed or copied into this tree.
inline constexpr std::uint8_t CurrentX[32] = {
$(Format-ByteArray $Current.X)
};
inline constexpr std::uint8_t CurrentY[32] = {
$(Format-ByteArray $Current.Y)
};
inline constexpr std::uint8_t NextX[32] = {
$(Format-ByteArray $Next.X)
};
inline constexpr std::uint8_t NextY[32] = {
$(Format-ByteArray $Next.Y)
};

} // namespace godswar::network::development_manifest_keys
"@
    Write-TextAtomic $HeaderPath $header

    $currentTrust = [ordered]@{
        schemaVersion = 1
        keyId = '53249'
        environment = '1'
        minimumSequence = '1'
        x = [Convert]::ToBase64String($Current.X)
        y = [Convert]::ToBase64String($Current.Y)
        cngKeyName = $CurrentKeyName
        purpose = 'development-only endpoint manifest verification'
    } | ConvertTo-Json
    Write-TextAtomic $TrustPath $currentTrust

    $nextTrust = [ordered]@{
        schemaVersion = 1
        keyId = '53250'
        environment = '1'
        minimumSequence = '1'
        x = [Convert]::ToBase64String($Next.X)
        y = [Convert]::ToBase64String($Next.Y)
        cngKeyName = $NextKeyName
        purpose = 'development-only next endpoint manifest verification'
    } | ConvertTo-Json
    Write-TextAtomic $NextTrustPath $nextTrust
}

$currentExists = Test-KeyExists $CurrentKeyName
$nextExists = Test-KeyExists $NextKeyName
if ($Mode -eq 'Status') {
    $currentStatus = Get-KeyStatus $CurrentKeyName
    $nextStatus = Get-KeyStatus $NextKeyName
    [pscustomobject]@{
        CurrentKeyName = $CurrentKeyName
        CurrentExists = $currentStatus.Exists
        CurrentValid = $currentStatus.Valid
        NextKeyName = $NextKeyName
        NextExists = $nextStatus.Exists
        NextValid = $nextStatus.Valid
        HeaderPath = [IO.Path]::GetFullPath($HeaderPath)
        TrustPath = [IO.Path]::GetFullPath($TrustPath)
        NextTrustPath = [IO.Path]::GetFullPath($NextTrustPath)
        PrivateKeysExportable = (
            $currentStatus.Exportable -or
            $nextStatus.Exportable)
    }
    return
}

if ($Mode -eq 'Remove') {
    if (-not $AllowKeyRemoval) {
        throw 'Remove requires explicit -AllowKeyRemoval.'
    }
    if (-not $PSCmdlet.ShouldProcess(
            'CurrentUser CNG key store',
            "Delete development keys $CurrentKeyName and $NextKeyName")) {
        return
    }
    foreach ($name in @($CurrentKeyName, $NextKeyName)) {
        if (Test-KeyExists $name) {
            $key = Open-Key $name
            try {
                $key.Delete()
            }
            finally {
                $key.Dispose()
            }
        }
    }
    return
}

if ($currentExists -or $nextExists) {
    throw 'Create refuses to overwrite either existing development key.'
}
if (-not $PSCmdlet.ShouldProcess(
        'CurrentUser CNG key store',
        'Create two non-exportable ECDSA P-256 development signing keys')) {
    return
}

$currentKey = $null
$nextKey = $null
$artifactSnapshots = @(
    Get-TextArtifactSnapshot $HeaderPath
    Get-TextArtifactSnapshot $TrustPath
    Get-TextArtifactSnapshot $NextTrustPath
)
try {
    $currentKey = New-SigningKey $CurrentKeyName
    $nextKey = New-SigningKey $NextKeyName
    Write-PublicArtifacts `
        (Get-PublicCoordinates $currentKey) `
        (Get-PublicCoordinates $nextKey)
}
catch {
    foreach ($key in @($nextKey, $currentKey)) {
        if ($null -ne $key) {
            try { $key.Delete() } catch {}
        }
    }
    foreach ($snapshot in $artifactSnapshots) {
        try { Restore-TextArtifactSnapshot $snapshot } catch {}
    }
    throw
}
finally {
    if ($null -ne $nextKey) { $nextKey.Dispose() }
    if ($null -ne $currentKey) { $currentKey.Dispose() }
}

[pscustomobject]@{
    Result = 'Created'
    CurrentKeyName = $CurrentKeyName
    NextKeyName = $NextKeyName
    HeaderPath = [IO.Path]::GetFullPath($HeaderPath)
    TrustPath = [IO.Path]::GetFullPath($TrustPath)
    NextTrustPath = [IO.Path]::GetFullPath($NextTrustPath)
    PrivateKeysExportable = $false
}
