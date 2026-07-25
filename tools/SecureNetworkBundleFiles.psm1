Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Get-RebornBundleFileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-RebornBundleSha256 {
    param([object]$Value)
    return $Value -is [string] -and
        $Value -cmatch '^[0-9A-F]{64}$'
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
        [IO.File]::WriteAllText(
            $temporary,
            $json,
            [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Assert-RebornRegularFilePath `
                $temporary 'temporary receipt' | Out-Null
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
    $old = "$Destination.$([Guid]::NewGuid().ToString('N')).old"
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

        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [IO.File]::Replace($temporary, $Destination, $old, $true)
        } else {
            [IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        foreach ($path in @($temporary, $old)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Assert-RebornRegularFilePath `
                    $path 'copy cleanup file' | Out-Null
                [IO.File]::Delete($path)
            }
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
    [IO.File]::WriteAllText(
        (Join-Path $Directory 'receipt.sha256'),
        (Get-RebornBundleFileSha256 $receiptPath),
        [Text.UTF8Encoding]::new($false))
}

function Assert-RebornReceiptPolicy {
    param(
        [object]$Receipt,
        [object]$Policy,
        [object]$VerifiedManifest
    )

    if ([string]$VerifiedManifest.ManifestSha256 -cne
            [string]$Policy.ManifestSha256 -or
        [string]$VerifiedManifest.TrustSha256 -cne
            [string]$Policy.ManifestTrustSha256) {
        throw 'Receipt manifest is not bound to the reviewed bundle policy.'
    }
    foreach ($name in @(
        'OriginSha256',
        'LegacyNetSha256',
        'CandidateNetSha256',
        'ManifestSha256',
        'ManifestTrustSha256'
    )) {
        if ([string]$Receipt.policy.$name -cne [string]$Policy.$name) {
            throw "Secure-bundle receipt policy mismatch: $name"
        }
    }

    $entries = @($Receipt.files)
    if ($entries.Count -ne 3) {
        throw 'Secure-bundle receipt must contain exactly three file entries.'
    }
    $specifications = @(
        @('Net.dll', $true, 'Net.dll', $Policy.LegacyNetSha256),
        @('NetLegacy.dll', $false, $null, $null),
        @('RebornNetwork.gwem', $false, $null, $null)
    )
    foreach ($specification in $specifications) {
        $matches = @($entries | Where-Object {
            $_.path -is [string] -and
            $_.path -ceq $specification[0]
        })
        if ($matches.Count -ne 1) {
            throw "Secure-bundle receipt entry is missing or duplicated: $($specification[0])"
        }
        $entry = $matches[0]
        if ($entry.existed -isnot [bool] -or
            $entry.existed -ne $specification[1] -or
            $entry.backup -cne $specification[2] -or
            $entry.sha256 -cne $specification[3]) {
            throw "Secure-bundle receipt entry violates policy: $($specification[0])"
        }
    }

    if ([string]$Receipt.manifest.sha256 -cne
            [string]$VerifiedManifest.ManifestSha256 -or
        [string]$Receipt.manifest.trustSha256 -cne
            [string]$VerifiedManifest.TrustSha256 -or
        [string]$Receipt.manifest.environment -cne
            $VerifiedManifest.Environment.ToString() -or
        [string]$Receipt.manifest.sequence -cne
            $VerifiedManifest.Sequence.ToString()) {
        throw 'Secure-bundle receipt manifest metadata is not authoritative.'
    }
    $beforeMode = [UInt64]0
    $beforeEnvironment = [UInt64]0
    $beforeFloor = [UInt64]0
    if ($Receipt.stateBefore.existed -isnot [bool] -or
        -not [UInt64]::TryParse(
            [string]$Receipt.stateBefore.activationMode,
            [ref]$beforeMode) -or
        -not [UInt64]::TryParse(
            [string]$Receipt.stateBefore.environment,
            [ref]$beforeEnvironment) -or
        -not [UInt64]::TryParse(
            [string]$Receipt.stateBefore.sequenceFloor,
            [ref]$beforeFloor) -or
        $beforeMode -ne 0 -or
        $beforeEnvironment -gt 3 -or
        $beforeFloor -gt $VerifiedManifest.Sequence) {
        throw 'Secure-bundle receipt predecessor state is invalid.'
    }
}

function Read-RebornBackupReceipt {
    param(
        [string]$Directory,
        [string]$ExpectedClient,
        [object]$Policy,
        [object]$VerifiedManifest
    )

    $resolved = Resolve-RebornReceiptDirectory $Directory
    $receiptPath = Join-Path $resolved 'receipt.json'
    $checksumPath = Join-Path $resolved 'receipt.sha256'
    foreach ($path in @($receiptPath, $checksumPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Secure-bundle backup is incomplete: $path"
        }
        Assert-RebornRegularFilePath `
            $path 'secure-bundle receipt file' | Out-Null
    }
    $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    if (-not (Test-RebornBundleSha256 $expected) -or
        (Get-RebornBundleFileSha256 $receiptPath) -cne $expected) {
        throw 'Secure-bundle backup receipt checksum failed.'
    }

    $receipt = Get-Content -LiteralPath $receiptPath -Raw |
        ConvertFrom-Json
    if ($receipt.schemaVersion -ne 2 -or
        $receipt.mode -cne 'Apply' -or
        -not ([IO.Path]::GetFullPath([string]$receipt.clientRoot)).Equals(
            $ExpectedClient,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Secure-bundle backup receipt is not applicable.'
    }
    Assert-RebornReceiptPolicy $receipt $Policy $VerifiedManifest

    $net = Join-Path $resolved 'Net.dll'
    if (-not (Test-Path -LiteralPath $net -PathType Leaf)) {
        throw 'Secure-bundle predecessor backup is missing.'
    }
    Assert-RebornRegularFilePath `
        $net 'secure-bundle predecessor backup' | Out-Null
    if ((Get-RebornBundleFileSha256 $net) -cne
            $Policy.LegacyNetSha256) {
        throw 'Secure-bundle predecessor backup failed validation.'
    }
    [pscustomobject]@{
        Directory = $resolved
        Receipt = $receipt
    }
}

Export-ModuleMember -Function @(
    'Copy-RebornFileAtomic',
    'New-RebornFileBackupEntry',
    'Write-RebornBackupReceipt',
    'Read-RebornBackupReceipt'
)
