Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkBundleFiles.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

function Resolve-RebornBundleReceiptDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($resolved).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($resolved) -or
        $resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "ApplyBackupPath is not a safe existing directory: $resolved"
    }
    return Assert-RebornDirectoryPath $resolved 'ApplyBackupPath'
}

function Assert-RebornReceiptPolicy {
    param(
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][object]$VerifiedManifest
    )

    $pairedOrigin =
        [string]$Policy.CandidateOriginSha256 -cne
        [string]$Policy.OriginSha256
    $expectedSchema = if ($pairedOrigin) { 4 } else { 3 }
    if ($Receipt.schemaVersion -ne $expectedSchema) {
        throw 'Secure-bundle receipt schema does not match its policy.'
    }
    if ([string]$VerifiedManifest.ManifestSha256 -cne
            [string]$Policy.ManifestSha256 -or
        [string]$VerifiedManifest.TrustSha256 -cne
            [string]$Policy.ManifestTrustSha256) {
        throw 'Receipt manifest is not bound to the reviewed bundle policy.'
    }
    $policyNames = @(
        'OriginSha256',
        'LegacyNetSha256',
        'CandidateNetSha256',
        'ManifestSha256',
        'ManifestTrustSha256'
    )
    if ($pairedOrigin) {
        $policyNames += 'CandidateOriginSha256'
    }
    foreach ($name in $policyNames) {
        if ([string]$Receipt.policy.$name -cne [string]$Policy.$name) {
            throw "Secure-bundle receipt policy mismatch: $name"
        }
    }

    $recoverySpecifications =
        @(Get-RebornRecoveryInputSpecifications $Policy)
    $recoveryEntries = @($Receipt.recoveryInputs)
    if ($recoveryEntries.Count -ne $recoverySpecifications.Count) {
        throw 'Secure-bundle receipt recovery input count is invalid.'
    }
    foreach ($specification in $recoverySpecifications) {
        $matches = @($recoveryEntries | Where-Object {
            $_.role -is [string] -and
            $_.role -ceq $specification.Role
        })
        $expectedHash =
            [string]$Policy.($specification.PolicyHash)
        if ($matches.Count -ne 1 -or
            $matches[0].path -cne $specification.FileName -or
            $matches[0].sha256 -cne $expectedHash) {
            throw (
                'Secure-bundle receipt recovery input violates policy: ' +
                $specification.Role)
        }
    }

    $fileSpecifications = [Collections.Generic.List[object]]::new()
    if ($pairedOrigin) {
        $fileSpecifications.Add(
            @('Origin.exe', $true, 'Origin.exe', $Policy.OriginSha256))
    }
    $fileSpecifications.Add(
        @('Net.dll', $true, 'Net.dll', $Policy.LegacyNetSha256))
    $fileSpecifications.Add(
        @('NetLegacy.dll', $false, $null, $null))
    $fileSpecifications.Add(
        @('RebornNetwork.gwem', $false, $null, $null))
    $entries = @($Receipt.files)
    if ($entries.Count -ne $fileSpecifications.Count) {
        throw 'Secure-bundle receipt file-entry count is invalid.'
    }
    foreach ($specification in $fileSpecifications) {
        $matches = @($entries | Where-Object {
            $_.path -is [string] -and
            $_.path -ceq $specification[0]
        })
        if ($matches.Count -ne 1) {
            throw (
                'Secure-bundle receipt entry is missing or duplicated: ' +
                $specification[0])
        }
        $entry = $matches[0]
        if ($entry.existed -isnot [bool] -or
            $entry.existed -ne $specification[1] -or
            $entry.backup -cne $specification[2] -or
            $entry.sha256 -cne $specification[3]) {
            throw (
                'Secure-bundle receipt entry violates policy: ' +
                $specification[0])
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
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$ExpectedClient,
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][object]$VerifiedManifest
    )

    $resolved = Resolve-RebornBundleReceiptDirectory $Directory
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
    if ($expected -notmatch '^[0-9A-F]{64}$' -or
        (Get-RebornBundleFileSha256 $receiptPath) -cne $expected) {
        throw 'Secure-bundle backup receipt checksum failed.'
    }

    $receipt = Get-Content -LiteralPath $receiptPath -Raw |
        ConvertFrom-Json
    if ($receipt.mode -cne 'Apply' -or
        -not ([IO.Path]::GetFullPath([string]$receipt.clientRoot)).Equals(
            $ExpectedClient,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Secure-bundle backup receipt is not applicable.'
    }
    Assert-RebornReceiptPolicy $receipt $Policy $VerifiedManifest

    $predecessors = [Collections.Generic.List[object]]::new()
    $predecessors.Add(
        @('Net.dll', $Policy.LegacyNetSha256))
    if ([string]$Policy.CandidateOriginSha256 -cne
        [string]$Policy.OriginSha256) {
        $predecessors.Add(
            @('Origin.exe', $Policy.OriginSha256))
    }
    foreach ($predecessor in $predecessors) {
        $path = Join-Path $resolved $predecessor[0]
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'Secure-bundle predecessor backup is missing.'
        }
        Assert-RebornRegularFilePath `
            $path 'secure-bundle predecessor backup' | Out-Null
        if ((Get-RebornBundleFileSha256 $path) -cne $predecessor[1]) {
            throw 'Secure-bundle predecessor backup failed validation.'
        }
    }
    [pscustomobject]@{
        Directory = $resolved
        Receipt = $receipt
    }
}

Export-ModuleMember -Function 'Read-RebornBackupReceipt'
