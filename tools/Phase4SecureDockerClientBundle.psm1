Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

$script:ApplyBackupPrefix = 'client-secure-bundle-v2-Apply-'

function Get-RebornPhase4PinText {
    param(
        [Parameter(Mandatory)][object]$Pins,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Pins.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return ''
    }
    return [string]$property.Value
}

function Assert-RebornPhase4PinSha256 {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Name
    )

    if ($Value -cnotmatch '^[0-9A-F]{64}$') {
        throw "Phase 4 pin is not an uppercase SHA-256: $Name"
    }
}

function Get-RebornPhase4OriginPair {
    param([Parameter(Mandatory)][object]$Pins)

    $stockHash = Get-RebornPhase4PinText $Pins 'OriginSha256'
    Assert-RebornPhase4PinSha256 $stockHash 'OriginSha256'
    $candidatePath =
        Get-RebornPhase4PinText $Pins 'CandidateOriginPath'
    $candidateHash =
        Get-RebornPhase4PinText $Pins 'CandidateOriginSha256'
    $hasPath = -not [string]::IsNullOrWhiteSpace($candidatePath)
    $hasHash = -not [string]::IsNullOrWhiteSpace($candidateHash)
    if ($hasPath -ne $hasHash) {
        throw (
            'Phase 4 candidate Origin path and SHA-256 pins must be ' +
            'present together.')
    }
    if ($hasHash) {
        Assert-RebornPhase4PinSha256 `
            $candidateHash 'CandidateOriginSha256'
        if ($candidateHash -ceq $stockHash) {
            throw 'Phase 4 candidate Origin must differ from its predecessor.'
        }
    }

    return [pscustomobject]@{
        Paired = $hasPath
        StockSha256 = $stockHash
        CandidatePath = $candidatePath
        CandidateSha256 = $candidateHash
    }
}

function Resolve-RebornPhase4ManagerCampaignRoot {
    param(
        [AllowNull()][AllowEmptyString()][string]$RequestedRoot,
        [Parameter(Mandatory)][object]$Pins
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        return $RequestedRoot
    }
    $active = Get-RebornPhase4PinText $Pins 'CampaignRoot'
    if ([string]::IsNullOrWhiteSpace($active)) {
        throw 'Active Phase 4 pins do not declare a campaign root.'
    }
    return $active
}

function Get-RebornPhase4BundleArguments {
    param(
        [Parameter(Mandatory)][object]$Pins,
        [Parameter(Mandatory)][string]$BackupRoot
    )

    $origin = Get-RebornPhase4OriginPair $Pins
    $arguments = @{
        ClientRoot = $Pins.ClientRoot
        CandidatePath = $Pins.CandidatePath
        ManifestPath = $Pins.ManifestPath
        TrustPath = $Pins.ManifestTrustPath
        ExpectedOriginSha256 = $origin.StockSha256
        ExpectedCandidateSha256 = $Pins.CandidateSha256
        ExpectedChecksSha256 = $Pins.NativeChecksSha256
        ExpectedManifestSha256 = $Pins.ManifestSha256
        ExpectedTrustSha256 = $Pins.ManifestTrustSha256
        ClientInventoryReceiptPath = $Pins.InventoryReceiptPath
        ExpectedClientInventoryReceiptSha256 =
            $Pins.InventoryReceiptSha256
        BackupRoot = $BackupRoot
    }
    if ($origin.Paired) {
        $arguments.CandidateOriginPath = $origin.CandidatePath
        $arguments.ExpectedCandidateOriginSha256 =
            $origin.CandidateSha256
    }
    return $arguments
}

function Get-RebornPhase4BundleBackupBaselineNames {
    param([Parameter(Mandatory)][string]$BackupRoot)

    if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) {
        return @()
    }
    return @(
        Get-ChildItem -LiteralPath $BackupRoot -Directory |
            Where-Object {
                $_.Name.StartsWith(
                    $script:ApplyBackupPrefix,
                    [StringComparison]::Ordinal)
            } |
            Select-Object -ExpandProperty Name |
            Sort-Object)
}

function Get-RebornPhase4FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-RebornPhase4LiveBundleRecoveryState {
    param(
        [Parameter(Mandatory)][object]$Pins,
        [string]$ClientRoot = $Pins.ClientRoot
    )

    $originPair = Get-RebornPhase4OriginPair $Pins
    $client =
        Resolve-RebornNonRootLocalPath $ClientRoot 'ClientRoot' -MustExist
    $origin = Join-Path $client 'Origin.exe'
    $net = Join-Path $client 'Net.dll'
    foreach ($required in @($origin, $net)) {
        Assert-RebornRegularFilePath `
            $required 'live recovery bundle file' | Out-Null
    }
    $originHash = Get-RebornPhase4FileSha256 $origin
    $netHash = Get-RebornPhase4FileSha256 $net
    $legacy = Join-Path $client 'NetLegacy.dll'
    $manifest = Join-Path $client 'RebornNetwork.gwem'
    $legacyHash = if (Test-Path -LiteralPath $legacy -PathType Leaf) {
        Assert-RebornRegularFilePath `
            $legacy 'live recovery bundle file' | Out-Null
        Get-RebornPhase4FileSha256 $legacy
    }
    else {
        $null
    }
    $manifestHash = if (
        Test-Path -LiteralPath $manifest -PathType Leaf) {
        Assert-RebornRegularFilePath `
            $manifest 'live recovery bundle file' | Out-Null
        Get-RebornPhase4FileSha256 $manifest
    }
    else {
        $null
    }

    $installedOriginHash = if ($originPair.Paired) {
        $originPair.CandidateSha256
    }
    else {
        $originPair.StockSha256
    }
    if ($originHash -ceq $originPair.StockSha256 -and
        $netHash -ceq [string]$Pins.StockNetSha256 -and
        $null -eq $legacyHash -and
        $null -eq $manifestHash) {
        return 'Stock'
    }
    if ($originHash -ceq $installedOriginHash -and
        $netHash -ceq [string]$Pins.CandidateSha256 -and
        $legacyHash -ceq [string]$Pins.StockNetSha256 -and
        $manifestHash -ceq [string]$Pins.ManifestSha256) {
        return 'InstalledExact'
    }
    if ($originHash -notin @(
            $originPair.StockSha256,
            $installedOriginHash
        ) -or
        $netHash -notin @(
            [string]$Pins.StockNetSha256,
            [string]$Pins.CandidateSha256
        ) -or
        ($null -ne $legacyHash -and
            $legacyHash -cne [string]$Pins.StockNetSha256) -or
        ($null -ne $manifestHash -and
            $manifestHash -cne [string]$Pins.ManifestSha256)) {
        throw 'Live bundle is outside its pinned recovery state space.'
    }
    return 'RecoverablePartial'
}

function Assert-RebornPhase4BackupFile {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][AllowEmptyString()][string]$ExpectedSha256
    )

    $path = Join-Path $Directory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Discovered bundle backup is incomplete: $Name"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        (Get-RebornPhase4FileSha256 $path) -cne $ExpectedSha256) {
        throw "Discovered bundle backup hash mismatch: $Name"
    }
    return $path
}

function Assert-RebornPhase4Schema4Backup {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][object]$Pins
    )

    $origin = Get-RebornPhase4OriginPair $Pins
    if (-not $origin.Paired) {
        throw 'Schema-4 backup validation requires paired Origin pins.'
    }

    $receiptPath = Assert-RebornPhase4BackupFile `
        $Directory 'receipt.json' ''
    $checksumPath = Assert-RebornPhase4BackupFile `
        $Directory 'receipt.sha256' ''
    $expectedReceiptHash =
        (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    if ($expectedReceiptHash -cnotmatch '^[0-9A-F]{64}$' -or
        (Get-RebornPhase4FileSha256 $receiptPath) -cne
            $expectedReceiptHash) {
        throw 'Discovered schema-4 bundle receipt checksum is invalid.'
    }

    try {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw |
            ConvertFrom-Json
    }
    catch {
        throw 'Discovered schema-4 bundle receipt is invalid JSON.'
    }
    if ($receipt.schemaVersion -ne 4 -or
        $receipt.mode -cne 'Apply') {
        throw 'Discovered paired bundle backup is not schema 4 Apply.'
    }

    $policyPins = @(
        @('OriginSha256', $origin.StockSha256),
        @('CandidateOriginSha256', $origin.CandidateSha256),
        @('LegacyNetSha256', [string]$Pins.StockNetSha256),
        @('CandidateNetSha256', [string]$Pins.CandidateSha256),
        @('ManifestSha256', [string]$Pins.ManifestSha256),
        @('ManifestTrustSha256', [string]$Pins.ManifestTrustSha256)
    )
    foreach ($binding in $policyPins) {
        if ([string]$receipt.policy.($binding[0]) -cne $binding[1]) {
            throw (
                'Discovered schema-4 bundle policy mismatch: ' +
                $binding[0])
        }
    }

    $files = @(
        @('Origin.exe', $origin.StockSha256),
        @('Net.dll', [string]$Pins.StockNetSha256),
        @('candidate-Origin.exe', $origin.CandidateSha256),
        @('candidate-Net.dll', [string]$Pins.CandidateSha256),
        @('endpoint-manifest.gwem', [string]$Pins.ManifestSha256),
        @('manifest-trust.json', [string]$Pins.ManifestTrustSha256)
    )
    foreach ($file in $files) {
        Assert-RebornPhase4BackupFile `
            $Directory $file[0] $file[1] | Out-Null
    }

    $originEntries = @($receipt.files | Where-Object {
        $_.path -is [string] -and $_.path -ceq 'Origin.exe'
    })
    if ($originEntries.Count -ne 1 -or
        $originEntries[0].existed -isnot [bool] -or
        -not $originEntries[0].existed -or
        $originEntries[0].backup -cne 'Origin.exe' -or
        $originEntries[0].sha256 -cne $origin.StockSha256) {
        throw 'Discovered schema-4 Origin predecessor entry is invalid.'
    }

    $recoverySpecifications = @(
        @('Candidate', 'candidate-Net.dll', [string]$Pins.CandidateSha256),
        @('Manifest', 'endpoint-manifest.gwem', [string]$Pins.ManifestSha256),
        @('Trust', 'manifest-trust.json', [string]$Pins.ManifestTrustSha256),
        @(
            'OriginCandidate',
            'candidate-Origin.exe',
            $origin.CandidateSha256
        )
    )
    $recovery = @($receipt.recoveryInputs)
    if ($recovery.Count -ne $recoverySpecifications.Count) {
        throw 'Discovered schema-4 recovery-input count is invalid.'
    }
    foreach ($specification in $recoverySpecifications) {
        $matches = @($recovery | Where-Object {
            $_.role -is [string] -and
            $_.role -ceq $specification[0]
        })
        if ($matches.Count -ne 1 -or
            $matches[0].path -cne $specification[1] -or
            $matches[0].sha256 -cne $specification[2]) {
            throw (
                'Discovered schema-4 recovery input is invalid: ' +
                $specification[0])
        }
    }
}

function Resolve-RebornPhase4BundleBackupPath {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$BackupRoot,
        [Parameter(Mandatory)][object]$Pins
    )

    $origin = Get-RebornPhase4OriginPair $Pins
    $backupRootPath = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
    $recorded = [string]$Record.bundleBackupPath
    if (-not [string]::IsNullOrWhiteSpace($recorded)) {
        $candidate = [IO.Path]::GetFullPath($recorded).TrimEnd('\')
    }
    else {
        if (-not (Test-Path -LiteralPath $backupRootPath `
                -PathType Container)) {
            throw 'Bundle backup root is absent during recovery.'
        }
        $baseline = @($Record.bundleBackupBaselineNames)
        $candidates = @(
            Get-ChildItem -LiteralPath $backupRootPath -Directory |
                Where-Object {
                    $_.Name.StartsWith(
                        $script:ApplyBackupPrefix,
                        [StringComparison]::Ordinal) -and
                    $_.Name -notin $baseline
                })
        if ($candidates.Count -ne 1) {
            throw 'Bundle recovery could not identify one new Apply backup.'
        }
        $candidate = $candidates[0].FullName
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Container) -or
        -not (Split-Path -Parent $candidate).Equals(
            $backupRootPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($candidate)).StartsWith(
            $script:ApplyBackupPrefix,
            [StringComparison]::Ordinal)) {
        throw 'Bundle recovery backup is outside its issued backup root.'
    }

    if ($origin.Paired) {
        Assert-RebornPhase4Schema4Backup $candidate $Pins
    }
    elseif ([string]::IsNullOrWhiteSpace($recorded)) {
        foreach ($name in 'receipt.json', 'receipt.sha256', 'Net.dll') {
            Assert-RebornPhase4BackupFile $candidate $name '' |
                Out-Null
        }
    }
    return $candidate
}

Export-ModuleMember -Function @(
    'Resolve-RebornPhase4ManagerCampaignRoot',
    'Get-RebornPhase4BundleArguments',
    'Get-RebornPhase4BundleBackupBaselineNames',
    'Get-RebornPhase4LiveBundleRecoveryState',
    'Resolve-RebornPhase4BundleBackupPath'
)
