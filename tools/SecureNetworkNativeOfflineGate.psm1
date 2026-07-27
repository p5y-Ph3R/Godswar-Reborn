Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleFiles.psm1'
)
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
)

function Get-RebornSecureStreamSha256 {
    param([Parameter(Mandatory)][IO.FileStream]$Stream)

    if (-not $Stream.CanRead -or -not $Stream.CanSeek) {
        throw (
            'Secure staged-file verification requires a readable ' +
            'seekable stream.')
    }

    $originalPosition = $Stream.Position
    $algorithm = $null
    $hash = $null
    try {
        $Stream.Position = 0
        $algorithm = [Security.Cryptography.SHA256]::Create()
        $hash = $algorithm.ComputeHash($Stream)
        return ([BitConverter]::ToString($hash)).Replace('-', '')
    }
    finally {
        $Stream.Position = $originalPosition
        if ($null -ne $hash) {
            [Array]::Clear($hash, 0, $hash.Length)
        }
        if ($null -ne $algorithm) {
            $algorithm.Dispose()
        }
    }
}

function Invoke-RebornSecureBundleNativeOfflineGate {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$CandidateOrigin,
        [Parameter(Mandatory)][string]$StockNet,
        [Parameter(Mandatory)][string]$Manifest,
        [Parameter(Mandatory)][string]$ScratchRoot,
        [Parameter(Mandatory)][string]$ExpectedCandidate,
        [Parameter(Mandatory)][string]$ExpectedCandidateOrigin,
        [Parameter(Mandatory)][string]$ExpectedStockNet,
        [Parameter(Mandatory)][string]$ExpectedChecks,
        [Parameter(Mandatory)][string]$ExpectedManifest,
        [switch]$IncludeSockets
    )

    $candidateFile = [IO.Path]::GetFullPath($Candidate)
    $candidateOriginFile = [IO.Path]::GetFullPath($CandidateOrigin)
    $outputDirectory = Split-Path -Parent $candidateFile
    $checksSource =
        Join-Path $outputDirectory 'Godswar.NetShim.Checks.exe'

    $scratchBase = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\')
    $filesystemRoot =
        [IO.Path]::GetPathRoot($scratchBase).TrimEnd('\')
    if ($scratchBase.Equals(
            $filesystemRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ScratchRoot cannot be a filesystem root.'
    }
    $scratchBase = Initialize-RebornProtectedDirectoryPath `
        $scratchBase 'secure probe scratch root'
    $probe = Join-Path $scratchBase (
        'secure-bundle-offline-probe-' +
        [Guid]::NewGuid().ToString('N'))
    $probe = Initialize-RebornProtectedDirectoryPath `
        $probe 'secure probe directory'
    $stagedChecks = Join-Path $probe 'Godswar.NetShim.Checks.exe'
    $stagedCandidate = Join-Path $probe 'Net.dll'
    $stagedCandidateOrigin = Join-Path $probe 'Origin.exe'
    $stagedLegacy = Join-Path $probe 'NetLegacy.dll'
    $stagedManifest = Join-Path $probe 'RebornNetwork.gwem'
    $locks = @()
    try {
        Copy-RebornFileAtomic `
            $checksSource $stagedChecks $ExpectedChecks
        Copy-RebornFileAtomic `
            $candidateFile $stagedCandidate $ExpectedCandidate
        Copy-RebornFileAtomic `
            $candidateOriginFile `
            $stagedCandidateOrigin `
            $ExpectedCandidateOrigin
        Copy-RebornFileAtomic `
            $StockNet $stagedLegacy $ExpectedStockNet
        Copy-RebornFileAtomic `
            $Manifest $stagedManifest $ExpectedManifest

        $lockInputs = @(
            @($stagedChecks, $ExpectedChecks, 'verification executable'),
            @($stagedCandidate, $ExpectedCandidate, 'candidate Net.dll'),
            @(
                $stagedCandidateOrigin,
                $ExpectedCandidateOrigin,
                'candidate Origin.exe'),
            @($stagedLegacy, $ExpectedStockNet, 'stock Net.dll'),
            @($stagedManifest, $ExpectedManifest, 'endpoint manifest')
        )
        foreach ($input in $lockInputs) {
            $lock = $null
            try {
                $lock = [IO.File]::Open(
                    [string]$input[0],
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read)
                if ((Get-RebornSecureStreamSha256 $lock) -cne
                        [string]$input[1]) {
                    throw "Locked staged $($input[2]) SHA-256 mismatch."
                }
                $locks += $lock
                $lock = $null
            }
            finally {
                if ($null -ne $lock) {
                    $lock.Dispose()
                }
            }
        }

        if ($IncludeSockets) {
            & $stagedChecks
        } else {
            & $stagedChecks --offline
        }
        if ($LASTEXITCODE -ne 0) {
            throw (
                'Native candidate checks failed with exit code ' +
                "$LASTEXITCODE.")
        }

        & $stagedChecks `
            --offline-manifest-probe `
            $stagedCandidate `
            $stagedManifest
        if ($LASTEXITCODE -ne 0) {
            throw (
                'Manifest does not match the candidate build verification ' +
                "key; probe exit code $LASTEXITCODE.")
        }

        & $stagedChecks `
            --offline-origin-contract-probe `
            $stagedCandidate `
            $stagedCandidateOrigin
        if ($LASTEXITCODE -ne 0) {
            throw (
                'Candidate Origin does not match the candidate ' +
                'Net.dll build identity; probe exit code ' +
                "$LASTEXITCODE.")
        }

        & $stagedChecks --offline-probe $stagedCandidate
        if ($LASTEXITCODE -ne 0) {
            throw (
                'Offline stock-delegation probe failed with exit code ' +
                "$LASTEXITCODE.")
        }
    }
    finally {
        foreach ($lock in $locks) {
            $lock.Dispose()
        }
        Assert-RebornDirectChildDirectory `
            $probe $scratchBase 'secure probe cleanup directory' `
            -RequireProtected | Out-Null
        foreach ($staged in @(
            $stagedChecks,
            $stagedCandidate,
            $stagedCandidateOrigin,
            $stagedLegacy,
            $stagedManifest
        )) {
            if (Test-Path -LiteralPath $staged -PathType Leaf) {
                Assert-RebornRegularFilePath `
                    $staged 'secure probe cleanup file' | Out-Null
                [IO.File]::Delete($staged)
            }
        }
        [IO.Directory]::Delete($probe, $false)
    }
}

Export-ModuleMember -Function 'Invoke-RebornSecureBundleNativeOfflineGate'
