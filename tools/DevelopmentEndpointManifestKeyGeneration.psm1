Set-StrictMode -Version Latest

function Format-RebornManifestKeyBytes {
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

function Write-RebornManifestKeyTextAtomic {
    param([string]$Path, [string]$Text)

    $resolved = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $resolved)) |
        Out-Null
    $temporary = "$resolved.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporary,
            $Text,
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            $previous = "$resolved.previous"
            [IO.File]::Replace($temporary, $resolved, $previous, $true)
            if (Test-Path -LiteralPath $previous -PathType Leaf) {
                [IO.File]::Delete($previous)
            }
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            [IO.File]::Delete($temporary)
        }
    }
}

function Get-RebornManifestKeyArtifactSnapshot {
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

function Restore-RebornManifestKeyArtifactSnapshot {
    param([object]$Snapshot)

    if ($Snapshot.Existed) {
        Write-RebornManifestKeyTextAtomic `
            $Snapshot.Path $Snapshot.Text
    } elseif (Test-Path -LiteralPath $Snapshot.Path -PathType Leaf) {
        [IO.File]::Delete($Snapshot.Path)
    }
}

function Write-RebornManifestKeyPublicArtifacts {
    param(
        [object]$Current,
        [object]$Next,
        [string]$CurrentKeyName,
        [string]$NextKeyName,
        [string]$HeaderPath,
        [string]$TrustPath,
        [string]$NextTrustPath
    )

    $header = @"
#pragma once

#include <cstdint>

namespace godswar::network::development_manifest_keys {

// Generated public verification keys. Matching private keys are non-exportable
// CurrentUser CNG keys and must never be committed or copied into this tree.
inline constexpr std::uint8_t CurrentX[32] = {
$(Format-RebornManifestKeyBytes $Current.X)
};
inline constexpr std::uint8_t CurrentY[32] = {
$(Format-RebornManifestKeyBytes $Current.Y)
};
inline constexpr std::uint8_t NextX[32] = {
$(Format-RebornManifestKeyBytes $Next.X)
};
inline constexpr std::uint8_t NextY[32] = {
$(Format-RebornManifestKeyBytes $Next.Y)
};

} // namespace godswar::network::development_manifest_keys
"@
    Write-RebornManifestKeyTextAtomic $HeaderPath $header

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
    Write-RebornManifestKeyTextAtomic $TrustPath $currentTrust

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
    Write-RebornManifestKeyTextAtomic $NextTrustPath $nextTrust
}

Export-ModuleMember -Function @(
    'Get-RebornManifestKeyArtifactSnapshot',
    'Restore-RebornManifestKeyArtifactSnapshot',
    'Write-RebornManifestKeyPublicArtifacts'
)
