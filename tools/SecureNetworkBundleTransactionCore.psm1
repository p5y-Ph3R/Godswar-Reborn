Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkActivationState.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkBundleFiles.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

function Get-RebornSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-RebornHash {
    param([string]$Value)
    return $Value -match '^[0-9A-F]{64}$'
}

function New-RebornSecureBundlePolicyCore {
    param(
        [Parameter(Mandatory)][string]$OriginSha256,
        [Parameter(Mandatory)][string]$LegacyNetSha256,
        [Parameter(Mandatory)][string]$CandidateNetSha256,
        [Parameter(Mandatory)][string]$ManifestSha256,
        [Parameter(Mandatory)][string]$ManifestTrustSha256
    )

    $values = @(
        $OriginSha256,
        $LegacyNetSha256,
        $CandidateNetSha256,
        $ManifestSha256,
        $ManifestTrustSha256
    ) | ForEach-Object { $_.Trim().ToUpperInvariant() }
    foreach ($value in $values) {
        if (-not (Test-RebornHash $value)) {
            throw 'Every bundle policy hash must be an uppercase SHA-256.'
        }
    }

    [pscustomobject]@{
        OriginSha256 = $values[0]
        LegacyNetSha256 = $values[1]
        CandidateNetSha256 = $values[2]
        ManifestSha256 = $values[3]
        ManifestTrustSha256 = $values[4]
    }
}

function Assert-RebornManifestPolicyBinding {
    param([object]$Manifest, [object]$Policy)

    if ([string]$Manifest.ManifestSha256 -cne
            [string]$Policy.ManifestSha256 -or
        [string]$Manifest.TrustSha256 -cne
            [string]$Policy.ManifestTrustSha256) {
        throw (
            'Verified manifest bytes or trust bytes do not match ' +
            'the reviewed bundle policy.')
    }
}

function Assert-RebornBundleInputs {
    param(
        [object]$Policy,
        [string]$ClientRoot,
        [string]$CandidatePath,
        [string]$ManifestPath,
        [string]$TrustPath
    )

    $client =
        Resolve-RebornNonRootLocalPath $ClientRoot 'ClientRoot' -MustExist
    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $manifest = [IO.Path]::GetFullPath($ManifestPath)
    $trust = [IO.Path]::GetFullPath($TrustPath)
    $origin = Join-Path $client 'Origin.exe'
    $net = Join-Path $client 'Net.dll'
    foreach ($path in @($origin, $net, $candidate, $manifest, $trust)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required secure-bundle file is missing: $path"
        }
        Assert-RebornRegularFilePath `
            $path 'secure-bundle input' | Out-Null
    }

    if ((Get-RebornSha256 $origin) -ne $Policy.OriginSha256) {
        throw 'Origin.exe is not the pinned supported predecessor.'
    }
    if ((Get-RebornSha256 $candidate) -ne $Policy.CandidateNetSha256) {
        throw 'Candidate Net.dll does not match its reviewed SHA-256.'
    }
    if ((Get-RebornSha256 $manifest) -ne $Policy.ManifestSha256) {
        throw 'Endpoint manifest does not match its reviewed SHA-256.'
    }
    if ((Get-RebornSha256 $trust) -ne $Policy.ManifestTrustSha256) {
        throw 'Manifest trust descriptor does not match its reviewed SHA-256.'
    }

    [pscustomobject]@{
        Client = $client
        Origin = $origin
        Net = $net
        Legacy = Join-Path $client 'NetLegacy.dll'
        InstalledManifest = Join-Path $client 'RebornNetwork.gwem'
        Candidate = $candidate
        Manifest = $manifest
        Trust = $trust
    }
}

function Get-RebornOptionalHash {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Assert-RebornRegularFilePath `
            $Path 'optional secure-bundle file' | Out-Null
        return Get-RebornSha256 $Path
    }
    return $null
}

function Test-RebornActivationStateExact {
    param(
        [object]$Current,
        [UInt64]$Mode,
        [UInt64]$Environment,
        [UInt64]$SequenceFloor,
        [bool]$Exists
    )

    $modeExists = if (
        $null -ne $Current.PSObject.Properties['ModeExists']
    ) {
        [bool]$Current.ModeExists
    } else {
        [bool]$Current.Exists
    }
    $environmentExists = if (
        $null -ne $Current.PSObject.Properties['EnvironmentExists']
    ) {
        [bool]$Current.EnvironmentExists
    } else {
        [bool]$Current.Exists
    }
    $floorExists = if (
        $null -ne $Current.PSObject.Properties['SequenceFloorExists']
    ) {
        [bool]$Current.SequenceFloorExists
    } else {
        [bool]$Current.Exists
    }
    return (
        $Current.Exists -eq $Exists -and
        $modeExists -eq $Exists -and
        $environmentExists -eq $Exists -and
        $floorExists -eq $Exists -and
        [UInt64]$Current.Mode -eq $Mode -and
        [UInt64]$Current.Environment -eq $Environment -and
        [UInt64]$Current.SequenceFloor -eq $SequenceFloor)
}

function Test-RebornReceiptBoundDisabledActivationTransition {
    param(
        [object]$Current,
        [object]$Receipt,
        [UInt64]$Environment,
        [UInt64]$SequenceFloor
    )

    if (-not $Current.Exists) {
        return $false
    }
    $modeExists = if (
        $null -ne $Current.PSObject.Properties['ModeExists']
    ) {
        [bool]$Current.ModeExists
    } else {
        $true
    }
    $environmentExists = if (
        $null -ne $Current.PSObject.Properties['EnvironmentExists']
    ) {
        [bool]$Current.EnvironmentExists
    } else {
        $true
    }
    $floorExists = if (
        $null -ne $Current.PSObject.Properties['SequenceFloorExists']
    ) {
        [bool]$Current.SequenceFloorExists
    } else {
        $true
    }

    $beforeExists = [bool]$Receipt.stateBefore.existed
    $beforeEnvironment = [UInt64]$Receipt.stateBefore.environment
    $beforeFloor = [UInt64]$Receipt.stateBefore.sequenceFloor
    $emptyInitialization = (
        -not $beforeExists -and
        -not $modeExists -and
        -not $environmentExists -and
        -not $floorExists)
    if ($emptyInitialization) {
        return $true
    }
    if (-not $modeExists -or [UInt64]$Current.Mode -ne 0) {
        return $false
    }

    $predecessorEnvironmentMatches = if ($beforeExists) {
        $environmentExists -and
        [UInt64]$Current.Environment -eq $beforeEnvironment
    } else {
        -not $environmentExists
    }
    $predecessorFloorMatches = if ($beforeExists) {
        $floorExists -and
        [UInt64]$Current.SequenceFloor -eq $beforeFloor
    } else {
        -not $floorExists
    }
    $targetEnvironmentMatches = (
        $environmentExists -and
        [UInt64]$Current.Environment -eq $Environment)
    $targetFloorMatches = (
        $floorExists -and
        [UInt64]$Current.SequenceFloor -eq $SequenceFloor)

    return (
        ($predecessorEnvironmentMatches -and
            $predecessorFloorMatches) -or
        ($predecessorEnvironmentMatches -and $targetFloorMatches) -or
        ($targetEnvironmentMatches -and $targetFloorMatches))
}

function Assert-RebornRestoreSourceState {
    param(
        [object]$Paths,
        [object]$Policy,
        [object]$VerifiedManifest,
        [object]$Receipt,
        [object]$Current
    )

    $netHash = Get-RebornSha256 $Paths.Net
    $legacyHash = Get-RebornOptionalHash $Paths.Legacy
    $manifestHash = Get-RebornOptionalHash $Paths.InstalledManifest
    $stockFiles = (
        $netHash -ceq $Policy.LegacyNetSha256 -and
        $null -eq $legacyHash -and
        $null -eq $manifestHash)
    $manifestFiles = (
        $netHash -ceq $Policy.LegacyNetSha256 -and
        $null -eq $legacyHash -and
        $manifestHash -ceq $Policy.ManifestSha256)
    $legacyFiles = (
        $netHash -ceq $Policy.LegacyNetSha256 -and
        $legacyHash -ceq $Policy.LegacyNetSha256 -and
        $manifestHash -ceq $Policy.ManifestSha256)
    $candidateFiles = (
        $netHash -ceq $Policy.CandidateNetSha256 -and
        $legacyHash -ceq $Policy.LegacyNetSha256 -and
        $manifestHash -ceq $Policy.ManifestSha256)

    $sequence = [UInt64]$VerifiedManifest.Sequence
    $environment = [UInt64]$VerifiedManifest.Environment
    $stagedState = Test-RebornActivationStateExact `
        $Current 0 $environment $sequence $true
    $installedState = Test-RebornActivationStateExact `
        $Current 1 $environment $sequence $true
    $restoredDisabledState = Test-RebornActivationStateExact `
        $Current 0 $environment $sequence $true
    $predecessorState = Test-RebornActivationStateExact `
        $Current `
        ([UInt64]$Receipt.stateBefore.activationMode) `
        ([UInt64]$Receipt.stateBefore.environment) `
        ([UInt64]$Receipt.stateBefore.sequenceFloor) `
        ([bool]$Receipt.stateBefore.existed)
    $interruptedDisabledState =
        Test-RebornReceiptBoundDisabledActivationTransition `
            $Current $Receipt $environment $sequence

    $accepted = if (
        $stockFiles -and ($stagedState -or $restoredDisabledState)
    ) {
        'StockDisabled'
    } elseif ($stockFiles -and $predecessorState) {
        'StockPreApply'
    } elseif ($stockFiles -and $interruptedDisabledState) {
        'StockActivationInterrupted'
    } elseif ($manifestFiles -and $stagedState) {
        'AfterManifest'
    } elseif ($manifestFiles -and $interruptedDisabledState) {
        'AfterManifestActivationInterrupted'
    } elseif ($legacyFiles -and $stagedState) {
        'AfterLegacy'
    } elseif ($legacyFiles -and $interruptedDisabledState) {
        'AfterLegacyActivationInterrupted'
    } elseif ($candidateFiles -and $stagedState) {
        'AfterCandidate'
    } elseif ($candidateFiles -and $interruptedDisabledState) {
        'AfterCandidateActivationInterrupted'
    } elseif ($candidateFiles -and $installedState) {
        'InstalledExact'
    } else {
        $null
    }

    if ($null -eq $accepted) {
        throw (
            'Secure bundle Restore refused managed files or activation ' +
            'state not produced by this pinned Apply receipt. No mutation ' +
            'was performed.')
    }
    return $accepted
}

function Set-RebornSafeDisabledState {
    param(
        [object]$Current,
        [object]$VerifiedManifest,
        [object]$Policy,
        [string]$StateProvider,
        [string]$StatePath,
        [switch]$AllowHklmWrite
    )

    Assert-RebornManifestPolicyBinding $VerifiedManifest $Policy
    $floor = [Math]::Max(
        [UInt64]$Current.SequenceFloor,
        [UInt64]$VerifiedManifest.Sequence)
    $disabled = New-RebornActivationState `
        -Mode 0 `
        -Environment ([UInt64]$VerifiedManifest.Environment) `
        -SequenceFloor $floor
    Write-RebornActivationState `
        -Provider $StateProvider `
        -Path $StatePath `
        -State $disabled `
        -AllowHklmWrite:$AllowHklmWrite
}

function Restore-RebornBackupFiles {
    param([object]$Backup, [object]$Paths)

    $targets = @{
        'Net.dll' = $Paths.Net
        'NetLegacy.dll' = $Paths.Legacy
        'RebornNetwork.gwem' = $Paths.InstalledManifest
    }
    foreach ($entry in @($Backup.Receipt.files)) {
        $name = [string]$entry.path
        if (-not $targets.ContainsKey($name)) {
            throw "Unsupported secure-bundle restore target: $name"
        }
        $target = $targets[$name]
        if ($entry.existed) {
            Copy-RebornFileAtomic `
                (Join-Path $Backup.Directory ([string]$entry.backup)) `
                $target `
                ([string]$entry.sha256)
        } elseif (Test-Path -LiteralPath $target) {
            Assert-RebornRegularFilePath `
                $target 'secure-bundle restore target' | Out-Null
            [IO.File]::Delete($target)
        }
    }
}

function Open-RebornOriginMutationLock {
    param([string]$OriginPath)

    try {
        $stream = [IO.File]::Open(
            $OriginPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::None)
        if (Get-Process -Name Origin -ErrorAction SilentlyContinue) {
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

Export-ModuleMember -Function @(
    'New-RebornSecureBundlePolicyCore',
    'Assert-RebornManifestPolicyBinding',
    'Assert-RebornBundleInputs',
    'Get-RebornSha256',
    'Get-RebornOptionalHash',
    'Assert-RebornRestoreSourceState',
    'Set-RebornSafeDisabledState',
    'Restore-RebornBackupFiles',
    'Open-RebornOriginMutationLock'
)
