function Assert-AvatarPreloadProcessClosed {
    param([string]$ResolvedClientExe)

    $processName = [IO.Path]::GetFileNameWithoutExtension($ResolvedClientExe)
    $running = @(
        Get-Process -Name $processName -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        $_.Path,
                        $ResolvedClientExe,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    # A protected/elevated matching process is still a
                    # potential file user.
                    $true
                }
            }
    )
    if ($running.Count -gt 0) {
        throw "$([IO.Path]::GetFileName($ResolvedClientExe)) is running. Close it before changing the executable."
    }
}

function Assert-AvatarPreloadNetworkStock {
    param(
        [string]$ResolvedClientExe,
        [string]$ExpectedStockHash
    )

    $clientDirectory = Split-Path -Parent $ResolvedClientExe
    $netPath = Join-Path $clientDirectory 'Net.dll'
    $legacyPath = Join-Path $clientDirectory 'NetLegacy.dll'
    if (-not (Test-Path -LiteralPath $netPath -PathType Leaf) -or
        (Test-Path -LiteralPath $legacyPath -PathType Leaf)) {
        throw 'Restore the installed network shim to exact stock state before applying or reverting the V4 Origin patch.'
    }

    $netHash = (
        Get-FileHash -LiteralPath $netPath -Algorithm SHA256
    ).Hash
    if ($netHash -ne $ExpectedStockHash) {
        throw 'Restore the installed network shim to exact stock state before applying or reverting the V4 Origin patch.'
    }
}
