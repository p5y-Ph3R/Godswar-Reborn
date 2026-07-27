Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

if (-not ('RebornSecureBundleAtomicNativeV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class RebornSecureBundleAtomicNativeV1
{
    private const uint MoveFileWriteThrough = 0x00000008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool ReplaceFileW(
        string replacedFileName,
        string replacementFileName,
        string backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string newFileName,
        uint flags);

    public static void ReplaceExisting(string destination, string staged)
    {
        if (!ReplaceFileW(
            destination, staged, null, 0, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Atomic ReplaceFileW failed.");
        }
    }

    public static void PublishNew(string staged, string destination)
    {
        if (!MoveFileExW(staged, destination, MoveFileWriteThrough))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Atomic MoveFileExW publication failed.");
        }
    }
}
'@
}

function Get-RebornRecoveryInputSpecifications {
    param([Parameter(Mandatory)][object]$Policy)

    $specifications = [Collections.Generic.List[object]]::new()
    $specifications.Add([pscustomobject]@{
        Role = 'Candidate'
        FileName = 'candidate-Net.dll'
        PolicyHash = 'CandidateNetSha256'
    })
    $specifications.Add([pscustomobject]@{
        Role = 'Manifest'
        FileName = 'endpoint-manifest.gwem'
        PolicyHash = 'ManifestSha256'
    })
    $specifications.Add([pscustomobject]@{
        Role = 'Trust'
        FileName = 'manifest-trust.json'
        PolicyHash = 'ManifestTrustSha256'
    })
    if ([string]$Policy.CandidateOriginSha256 -cne
        [string]$Policy.OriginSha256) {
        $specifications.Add([pscustomobject]@{
            Role = 'OriginCandidate'
            FileName = 'candidate-Origin.exe'
            PolicyHash = 'CandidateOriginSha256'
        })
    }
    return $specifications.ToArray()
}

function Get-RebornBundleFileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-RebornBundleSha256 {
    param([object]$Value)
    return $Value -is [string] -and
        $Value -cmatch '^[0-9A-F]{64}$'
}

function Move-RebornStagedFileAtomic {
    param(
        [Parameter(Mandatory)][string]$StagedPath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedSha256
    )

    $staged = Assert-RebornSingleLinkRegularFilePath `
        $StagedPath 'staged atomic file'
    $destination =
        Resolve-RebornCanonicalLocalPath `
            $DestinationPath 'atomic destination'
    $destinationParent =
        Assert-RebornDirectoryPath `
            (Split-Path -Parent $destination) `
            'atomic destination parent'
    if (-not (Split-Path -Parent $staged).Equals(
            $destinationParent,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetPathRoot($staged)).Equals(
            [IO.Path]::GetPathRoot($destination),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Atomic publication requires same-directory staging.'
    }
    $expected = $ExpectedSha256.ToUpperInvariant()
    if ((Get-RebornBundleFileSha256 $staged) -cne $expected) {
        throw 'The staged atomic file does not match its hash pin.'
    }

    $existing = Test-Path -LiteralPath $destination -PathType Leaf
    $beforeAcl = $null
    $beforeSddl = $null
    if ($existing) {
        Assert-RebornSingleLinkRegularFilePath `
            $destination 'existing atomic destination' | Out-Null
        $beforeAcl = Get-Acl -LiteralPath $destination
        $beforeSddl = $beforeAcl.Sddl
        [RebornSecureBundleAtomicNativeV1]::ReplaceExisting(
            $destination,
            $staged)
    } else {
        if (Test-Path -LiteralPath $destination) {
            throw 'Atomic destination exists but is not a regular file.'
        }
        [RebornSecureBundleAtomicNativeV1]::PublishNew(
            $staged,
            $destination)
    }

    Assert-RebornSingleLinkRegularFilePath `
        $destination 'published atomic destination' | Out-Null
    if ((Get-RebornBundleFileSha256 $destination) -cne $expected) {
        throw 'Atomic publication post-hash verification failed.'
    }
    if ($existing) {
        $afterSddl = (Get-Acl -LiteralPath $destination).Sddl
        if ($afterSddl -cne $beforeSddl) {
            # ReplaceFileW can apply the protected parent's inherited ACL to
            # the replacement file. The parent remains protected throughout;
            # restore the exact destination descriptor before returning.
            Set-Acl -LiteralPath $destination -AclObject $beforeAcl
            $afterSddl = (Get-Acl -LiteralPath $destination).Sddl
        }
        if ($afterSddl -cne $beforeSddl) {
            throw 'Atomic replacement could not restore the destination ACL.'
        }
    }
    return $destination
}

function Resolve-RebornReceiptDirectory {
    param([string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($resolved).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($resolved) -or
        $resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "ApplyBackupPath is not a safe existing directory: $resolved"
    }
    return Assert-RebornDirectoryPath $resolved 'ApplyBackupPath'
}

function Write-RebornJsonAtomic {
    param([object]$Value, [string]$Path)

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth 10
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        try {
            $stream = [IO.File]::Open(
                $temporary,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        $expected =
            Get-RebornBundleFileSha256 $temporary
        Move-RebornStagedFileAtomic `
            $temporary $Path $expected | Out-Null
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Assert-RebornRegularFilePath `
                $temporary 'temporary receipt' | Out-Null
            [IO.File]::Delete($temporary)
        }
    }
}

function Write-RebornTextDurableAtomic {
    param([Parameter(Mandatory)][string]$Value, [string]$Path)

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
        try {
            $stream = [IO.File]::Open(
                $temporary,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        $expected =
            Get-RebornBundleFileSha256 $temporary
        Move-RebornStagedFileAtomic `
            $temporary $Path $expected | Out-Null
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Assert-RebornRegularFilePath `
                $temporary 'temporary durable text file' | Out-Null
            [IO.File]::Delete($temporary)
        }
    }
}

function Copy-RebornFileAtomic {
    param([string]$Source, [string]$Destination, [string]$ExpectedHash)

    $Source = Assert-RebornRegularFilePath $Source 'copy source'
    $Destination =
        Resolve-RebornCanonicalLocalPath $Destination 'copy destination'
    Assert-RebornDirectoryPath (
        Split-Path -Parent $Destination
    ) 'copy destination parent' | Out-Null
    if (Test-Path -LiteralPath $Destination) {
        Assert-RebornRegularFilePath `
            $Destination 'copy destination' | Out-Null
    }
    $temporary =
        "$Destination.$([Guid]::NewGuid().ToString('N')).stage"
    try {
        $input = [IO.File]::Open(
            $Source,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        try {
            $output = [IO.FileStream]::new(
                $temporary,
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
        if ((Get-RebornBundleFileSha256 $temporary) -cne $ExpectedHash) {
            throw "Staged file hash mismatch: $Destination"
        }

        Move-RebornStagedFileAtomic `
            $temporary `
            $Destination `
            $ExpectedHash | Out-Null
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Assert-RebornRegularFilePath `
                $temporary 'copy cleanup file' | Out-Null
            [IO.File]::Delete($temporary)
        }
    }
}

function New-RebornRecoveryInputSet {
    param(
        [object]$Policy,
        [string]$Directory,
        [string]$CandidatePath,
        [string]$ManifestPath,
        [string]$TrustPath,
        [string]$CandidateOriginPath
    )

    $sources = @{
        Candidate = $CandidatePath
        Manifest = $ManifestPath
        Trust = $TrustPath
        OriginCandidate = $CandidateOriginPath
    }
    foreach ($specification in (
        Get-RebornRecoveryInputSpecifications $Policy
    )) {
        $expectedHash =
            [string]$Policy.($specification.PolicyHash)
        Copy-RebornFileAtomic `
            $sources[$specification.Role] `
            (Join-Path $Directory $specification.FileName) `
            $expectedHash
        [ordered]@{
            role = $specification.Role
            path = $specification.FileName
            sha256 = $expectedHash
        }
    }
}

function Get-RebornRecoveryInputSet {
    param(
        [string]$Directory,
        [object]$Policy
    )

    $paths = @{}
    foreach ($specification in (
        Get-RebornRecoveryInputSpecifications $Policy
    )) {
        $path = Join-Path $Directory $specification.FileName
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Secure-bundle recovery input is missing: $path"
        }
        $path = Assert-RebornRegularFilePath `
            $path 'secure-bundle recovery input'
        $expectedHash =
            [string]$Policy.($specification.PolicyHash)
        if ((Get-RebornBundleFileSha256 $path) -cne $expectedHash) {
            throw (
                "Secure-bundle recovery input failed validation: " +
                $specification.Role)
        }
        $paths[$specification.Role] = $path
    }

    [pscustomobject]@{
        Candidate = $paths.Candidate
        Manifest = $paths.Manifest
        Trust = $paths.Trust
        CandidateOrigin = if ($paths.ContainsKey('OriginCandidate')) {
            $paths.OriginCandidate
        } else {
            $null
        }
    }
}

function New-RebornFileBackupEntry {
    param([string]$Path, [string]$Name, [string]$BackupDirectory)

    $exists = Test-Path -LiteralPath $Path
    $hash = if ($exists) {
        Assert-RebornRegularFilePath `
            $Path 'secure-bundle backup source' | Out-Null
        Get-RebornBundleFileSha256 $Path
    } else {
        $null
    }
    if ($exists) {
        Copy-RebornFileAtomic `
            $Path `
            (Join-Path $BackupDirectory $Name) `
            $hash
    }
    return [ordered]@{
        path = [IO.Path]::GetFileName($Path)
        existed = $exists
        backup = if ($exists) { $Name } else { $null }
        sha256 = $hash
    }
}

function Write-RebornBackupReceipt {
    param([object]$Receipt, [string]$Directory)

    $receiptPath = Join-Path $Directory 'receipt.json'
    Write-RebornJsonAtomic $Receipt $receiptPath
    Write-RebornTextDurableAtomic `
        (Get-RebornBundleFileSha256 $receiptPath) `
        (Join-Path $Directory 'receipt.sha256')
}

Export-ModuleMember -Function @(
    'Get-RebornBundleFileSha256',
    'Get-RebornRecoveryInputSpecifications',
    'Move-RebornStagedFileAtomic',
    'Copy-RebornFileAtomic',
    'Write-RebornJsonAtomic',
    'Write-RebornTextDurableAtomic',
    'New-RebornRecoveryInputSet',
    'Get-RebornRecoveryInputSet',
    'New-RebornFileBackupEntry',
    'Write-RebornBackupReceipt'
)
