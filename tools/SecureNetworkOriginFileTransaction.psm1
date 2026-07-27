Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Get-RebornOriginFileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Open-RebornOriginMutationLock {
    param([Parameter(Mandatory)][string]$OriginPath)

    try {
        $stream = [IO.File]::Open(
            $OriginPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Delete)
        $running = @(
            Get-Process -Name Origin -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Path -and
                    $_.Path.Equals(
                        $OriginPath,
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($running.Count -gt 0) {
            $stream.Dispose()
            throw 'Origin.exe is already running.'
        }
        return $stream
    }
    catch {
        throw (
            'Origin.exe must remain closed for the entire bundle mutation. ' +
            $_.Exception.Message)
    }
}

function Get-RebornLockedFileSha256 {
    param([Parameter(Mandatory)][IO.FileStream]$Stream)

    if (-not $Stream.CanRead -or -not $Stream.CanSeek) {
        throw 'Locked file hash requires a readable seekable stream.'
    }
    $position = $Stream.Position
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $Stream.Position = 0
        return (
            [BitConverter]::ToString(
                $algorithm.ComputeHash($Stream))
        ).Replace('-', '')
    }
    finally {
        $Stream.Position = $position
        $algorithm.Dispose()
    }
}

function Switch-RebornOriginFileAtomic {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedSourceSha256,
        [Parameter(Mandatory)][IO.FileStream]$CurrentLock,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedCurrentSha256
    )

    $sourcePath =
        Assert-RebornRegularFilePath $Source 'Origin replacement source'
    $destinationPath =
        Assert-RebornSingleLinkRegularFilePath `
            $Destination 'Origin replacement destination'
    $expectedSource = $ExpectedSourceSha256.ToUpperInvariant()
    $expectedCurrent = $ExpectedCurrentSha256.ToUpperInvariant()
    if ((Get-RebornOriginFileSha256 $sourcePath) -cne $expectedSource -or
        (Get-RebornLockedFileSha256 $CurrentLock) -cne $expectedCurrent) {
        throw 'Origin replacement source or locked predecessor is not exact.'
    }

    $beforeAcl = Get-Acl -LiteralPath $destinationPath
    $beforeSddl = $beforeAcl.Sddl
    $stage = "$destinationPath.$([Guid]::NewGuid().ToString('N')).stage"
    $nextLock = $null
    $published = $false
    try {
        $input = [IO.File]::Open(
            $sourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        try {
            $output = [IO.FileStream]::new(
                $stage,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $input.CopyTo($output)
                $output.Flush($true)
            }
            finally {
                $output.Dispose()
            }
        }
        finally {
            $input.Dispose()
        }
        if ((Get-RebornOriginFileSha256 $stage) -cne $expectedSource) {
            throw 'Staged Origin.exe does not match its hash pin.'
        }

        [RebornSecureBundleAtomicNativeV1]::ReplaceExisting(
            $destinationPath,
            $stage)
        $published = $true
        $nextLock = Open-RebornOriginMutationLock $destinationPath

        if ((Get-RebornLockedFileSha256 $nextLock) -cne $expectedSource) {
            throw 'Published Origin.exe failed locked-stream verification.'
        }
        if ((Get-Acl -LiteralPath $destinationPath).Sddl -cne $beforeSddl) {
            Set-Acl -LiteralPath $destinationPath -AclObject $beforeAcl
        }
        if ((Get-Acl -LiteralPath $destinationPath).Sddl -cne $beforeSddl) {
            throw 'Origin.exe replacement could not restore its exact ACL.'
        }
        return [pscustomobject]@{
            CurrentLock = $nextLock
            PreviousLock = $CurrentLock
            Sha256 = $expectedSource
        }
    }
    catch {
        if ($null -ne $nextLock) {
            $nextLock.Dispose()
        }
        throw
    }
    finally {
        if (-not $published -and
            (Test-Path -LiteralPath $stage -PathType Leaf)) {
            Assert-RebornRegularFilePath `
                $stage 'Origin replacement stage' | Out-Null
            [IO.File]::Delete($stage)
        }
    }
}

Export-ModuleMember -Function @(
    'Open-RebornOriginMutationLock',
    'Get-RebornLockedFileSha256',
    'Switch-RebornOriginFileAtomic'
)
