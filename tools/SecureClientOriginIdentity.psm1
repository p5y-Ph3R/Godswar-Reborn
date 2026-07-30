Set-StrictMode -Version Latest

$script:SealedOriginSha256 =
    'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'

function ConvertTo-RebornOriginIdentityBytes {
    param([Parameter(Mandatory)][string]$Sha256)

    $normalized = $Sha256.ToUpperInvariant()
    if ($normalized -notmatch '\A[0-9A-F]{64}\z') {
        throw 'Origin identity must be one SHA-256 hexadecimal value.'
    }

    $bytes = [byte[]]::new(32)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte(
            $normalized.Substring($index * 2, 2),
            16)
    }
    return ,$bytes
}

function Format-RebornOriginIdentityBytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if ($Bytes.Length -ne 32) {
        throw 'Origin identity must contain exactly 32 bytes.'
    }

    $lines = for ($offset = 0; $offset -lt 32; $offset += 8) {
        $values = for ($index = $offset; $index -lt $offset + 8; $index++) {
            '0x{0:X2}' -f $Bytes[$index]
        }
        '    ' + ($values -join ', ') + ','
    }
    return $lines -join "`r`n"
}

function New-RebornSecureClientOriginIdentityHeader {
    param([Parameter(Mandatory)][string]$Sha256)

    $formatted = Format-RebornOriginIdentityBytes (
        ConvertTo-RebornOriginIdentityBytes $Sha256)
    return @"
#pragma once

#include <cstdint>

namespace godswar::network::secure_client_origin_identity {

// Generated build-scoped Origin identity. The repository placeholder pins the
// sealed PreviewReadyV6 build; the offline workflow may replace it temporarily.
inline constexpr std::uint8_t Sha256[32] = {
$formatted
};

} // namespace godswar::network::secure_client_origin_identity
"@
}

function Write-RebornOriginIdentityBytesAtomic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $resolved)) |
        Out-Null
    $temporary = "$resolved.$([Guid]::NewGuid().ToString('N')).tmp"
    $backup = "$resolved.$([Guid]::NewGuid().ToString('N')).previous"
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            [IO.File]::Replace($temporary, $resolved, $backup, $true)
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        foreach ($artifact in @($temporary, $backup)) {
            if (Test-Path -LiteralPath $artifact -PathType Leaf) {
                [IO.File]::Delete($artifact)
            }
        }
    }
}

function Assert-RebornOriginIdentityRestored {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$Existed,
        [AllowNull()][byte[]]$Bytes
    )

    $existsNow = Test-Path -LiteralPath $Path -PathType Leaf
    if ($Existed -ne $existsNow) {
        throw 'Origin identity header existence was not restored exactly.'
    }
    if ($Existed) {
        $actual = [Convert]::ToBase64String(
            [IO.File]::ReadAllBytes($Path))
        $expected = [Convert]::ToBase64String($Bytes)
        if ($actual -cne $expected) {
            throw 'Origin identity header bytes were not restored exactly.'
        }
    }
}

function Invoke-WithRebornSecureClientOriginIdentity {
    param(
        [Parameter(Mandatory)][string]$HeaderPath,
        [AllowNull()][AllowEmptyString()][string]$CandidateOriginPath,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    $resolvedHeader = [IO.Path]::GetFullPath($HeaderPath)
    $candidate = if (
        [string]::IsNullOrWhiteSpace($CandidateOriginPath)) {
        ''
    } else {
        [IO.Path]::GetFullPath($CandidateOriginPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($candidate) -and
        -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Candidate Origin does not exist: $candidate"
    }

    $selectedSha256 = if (
        [string]::IsNullOrWhiteSpace($candidate)) {
        $script:SealedOriginSha256
    } else {
        (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
    }
    $existed = Test-Path -LiteralPath $resolvedHeader -PathType Leaf
    $snapshot = if ($existed) {
        [IO.File]::ReadAllBytes($resolvedHeader)
    } else {
        $null
    }

    try {
        $header = New-RebornSecureClientOriginIdentityHeader $selectedSha256
        $headerBytes = [Text.UTF8Encoding]::new($false).GetBytes($header)
        Write-RebornOriginIdentityBytesAtomic $resolvedHeader $headerBytes
        & $Action $selectedSha256
    }
    finally {
        if ($existed) {
            Write-RebornOriginIdentityBytesAtomic $resolvedHeader $snapshot
        } elseif (Test-Path -LiteralPath $resolvedHeader -PathType Leaf) {
            [IO.File]::Delete($resolvedHeader)
        }
        Assert-RebornOriginIdentityRestored `
            $resolvedHeader $existed $snapshot
    }
}

Export-ModuleMember -Function @(
    'Invoke-WithRebornSecureClientOriginIdentity',
    'New-RebornSecureClientOriginIdentityHeader'
)
