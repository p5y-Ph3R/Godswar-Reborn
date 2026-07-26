Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkActivationState.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureEndpointManifestValidation.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkBundleFiles.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkBundleTransactionCore.psm1'
) -Force

function New-RebornSecureBundlePolicy {
    param(
        [Parameter(Mandatory)][string]$OriginSha256,
        [Parameter(Mandatory)][string]$LegacyNetSha256,
        [Parameter(Mandatory)][string]$CandidateNetSha256,
        [Parameter(Mandatory)][string]$ManifestSha256,
        [Parameter(Mandatory)][string]$ManifestTrustSha256
    )

    New-RebornSecureBundlePolicyCore `
        $OriginSha256 `
        $LegacyNetSha256 `
        $CandidateNetSha256 `
        $ManifestSha256 `
        $ManifestTrustSha256
}

function Get-RebornSecureBundleStatus {
    param(
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$TrustPath,
        [Parameter(Mandatory)]
        [ValidateSet('OfflineFile', 'Hklm')][string]$StateProvider,
        [string]$StatePath,
        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
    )

    $paths = Assert-RebornBundleInputs `
        $Policy $ClientRoot $CandidatePath $ManifestPath $TrustPath
    $state = Get-RebornActivationState `
        -Provider $StateProvider `
        -Path $StatePath
    $manifest = Read-RebornSecureEndpointManifest `
        -ManifestPath $paths.Manifest `
        -TrustPath $paths.Trust `
        -InstalledSequenceFloor $state.SequenceFloor `
        -Now $Now
    Assert-RebornManifestPolicyBinding $manifest $Policy
    $manifestHash = $manifest.ManifestSha256
    $netHash = Get-RebornSha256 $paths.Net
    $legacyHash = Get-RebornOptionalHash $paths.Legacy
    $installedManifestHash =
        Get-RebornOptionalHash $paths.InstalledManifest
    $stateComplete = if (
        $null -ne $state.PSObject.Properties['Complete']
    ) {
        [bool]$state.Complete
    } else {
        [bool]$state.Exists
    }
    $stockActivation = (
        $state.Mode -eq 0 -and
        (-not $state.Exists -or $stateComplete))

    $status = if (
        $netHash -eq $Policy.LegacyNetSha256 -and
        $null -eq $legacyHash -and
        $null -eq $installedManifestHash -and
        $stockActivation
    ) {
        'Stock'
    } elseif (
        $netHash -eq $Policy.CandidateNetSha256 -and
        $legacyHash -eq $Policy.LegacyNetSha256 -and
        $installedManifestHash -eq $manifestHash -and
        $state.Mode -eq 1 -and
        $state.Environment -eq $manifest.Environment -and
        $state.SequenceFloor -ge $manifest.Sequence
    ) {
        'InstalledExact'
    } else {
        'RecoverablePartial'
    }

    [pscustomobject]@{
        State = $status
        ClientRoot = $paths.Client
        OriginSha256 = $Policy.OriginSha256
        NetSha256 = $netHash
        NetLegacySha256 = $legacyHash
        ManifestSha256 = $installedManifestHash
        ExpectedManifestSha256 = $manifestHash
        ActivationMode = [UInt64]$state.Mode
        Environment = [UInt64]$state.Environment
        SequenceFloor = [UInt64]$state.SequenceFloor
        ManifestSequence = [UInt64]$manifest.Sequence
    }
}

function Invoke-RebornSecureBundleApply {
    param(
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$TrustPath,
        [Parameter(Mandatory)][string]$BackupRoot,
        [Parameter(Mandatory)]
        [ValidateSet('OfflineFile', 'Hklm')][string]$StateProvider,
        [string]$StatePath,
        [switch]$AllowHklmWrite,
        [ValidateSet(
            'None',
            'AfterState',
            'AfterManifest',
            'AfterLegacy',
            'AfterCandidate')]
        [string]$FailurePoint = 'None',
        [switch]$LeaveInterrupted,
        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow,
        [scriptblock]$PreCommitValidation
    )

    if ($StateProvider -eq 'Hklm') {
        Assert-RebornProtectedDirectoryPath `
            $ClientRoot 'ClientRoot' -ProtectContents | Out-Null
    }
    $paths = Assert-RebornBundleInputs `
        $Policy $ClientRoot $CandidatePath $ManifestPath $TrustPath
    if ($StateProvider -eq 'Hklm') {
        Assert-RebornProtectedFileSet `
            @($paths.Origin, $paths.Net, $paths.Legacy,
                $paths.InstalledManifest) `
            'live client file'
    }
    $state = Get-RebornActivationState $StateProvider $StatePath
    $manifest = Read-RebornSecureEndpointManifest `
        $paths.Manifest $paths.Trust $state.SequenceFloor -Now $Now
    Assert-RebornManifestPolicyBinding $manifest $Policy
    $status = Get-RebornSecureBundleStatus `
        $Policy $paths.Client $paths.Candidate $paths.Manifest $paths.Trust `
        $StateProvider $StatePath -Now $Now
    if ($status.State -eq 'InstalledExact') {
        return [pscustomobject]@{
            Result = 'AlreadyInstalled'
            BackupPath = $null
        }
    }
    if ($status.State -ne 'Stock') {
        throw "Secure bundle Apply requires exact Stock state, got $($status.State)."
    }

    $backupBase =
        Resolve-RebornNonRootLocalPath $BackupRoot 'BackupRoot'
    if ($StateProvider -eq 'Hklm') {
        $backupBase = Initialize-RebornProtectedDirectoryPath `
            $backupBase 'BackupRoot'
    } else {
        [IO.Directory]::CreateDirectory($backupBase) | Out-Null
        $backupBase =
            Assert-RebornDirectoryPath $backupBase 'BackupRoot'
    }
    $backup = Join-Path $backupBase (
        'client-secure-bundle-v2-Apply-' +
        (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
        [Guid]::NewGuid().ToString('N'))
    if ($StateProvider -eq 'Hklm') {
        $backup = Initialize-RebornProtectedDirectoryPath `
            $backup 'Apply backup'
    } else {
        [IO.Directory]::CreateDirectory($backup) | Out-Null
        $backup = Assert-RebornDirectChildDirectory `
            $backup $backupBase 'Apply backup'
    }
    $files = @(
        New-RebornFileBackupEntry $paths.Net 'Net.dll' $backup
        New-RebornFileBackupEntry $paths.Legacy 'NetLegacy.dll' $backup
        New-RebornFileBackupEntry `
            $paths.InstalledManifest 'RebornNetwork.gwem' $backup
    )
    $recoveryInputs = @(
        New-RebornRecoveryInputSet `
            $Policy $backup $paths.Candidate $paths.Manifest $paths.Trust
    )
    $receipt = [ordered]@{
        schemaVersion = 3
        mode = 'Apply'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        clientRoot = $paths.Client
        policy = $Policy
        manifest = [ordered]@{
            sha256 = $manifest.ManifestSha256
            trustSha256 = $manifest.TrustSha256
            environment = $manifest.Environment.ToString()
            sequence = $manifest.Sequence.ToString()
        }
        stateBefore = [ordered]@{
            existed = $state.Exists
            activationMode = $state.Mode.ToString()
            environment = $state.Environment.ToString()
            sequenceFloor = $state.SequenceFloor.ToString()
        }
        files = $files
        recoveryInputs = $recoveryInputs
    }
    Write-RebornBackupReceipt $receipt $backup
    # The durable receipt and checksum are the sole rollback authority.
    # Re-open and fully validate them before the first activation mutation.
    Read-RebornBackupReceipt `
        $backup $paths.Client $Policy $manifest | Out-Null

    $originLock = Open-RebornOriginMutationLock $paths.Origin
    try {
        try {
            # Advance the irreversible floor while routing remains disabled.
            # SecureRequired is the final commit after every file validates.
            $stagingState = New-RebornActivationState `
                -Mode 0 `
                -Environment $manifest.Environment `
                -SequenceFloor $manifest.Sequence
            Write-RebornActivationState `
                $StateProvider $StatePath $stagingState `
                -AllowHklmWrite:$AllowHklmWrite
            if ($FailurePoint -eq 'AfterState') { throw 'Simulated interruption.' }

            Copy-RebornFileAtomic `
                $paths.Manifest `
                $paths.InstalledManifest `
                $Policy.ManifestSha256
            if ($FailurePoint -eq 'AfterManifest') { throw 'Simulated interruption.' }

            Copy-RebornFileAtomic `
                $paths.Net $paths.Legacy $Policy.LegacyNetSha256
            if ($FailurePoint -eq 'AfterLegacy') { throw 'Simulated interruption.' }

            Copy-RebornFileAtomic `
                $paths.Candidate $paths.Net $Policy.CandidateNetSha256
            if ($FailurePoint -eq 'AfterCandidate') { throw 'Simulated interruption.' }

            $installedManifest = Read-RebornSecureEndpointManifest `
                $paths.InstalledManifest $paths.Trust $manifest.Sequence `
                -Now $Now
            Assert-RebornManifestPolicyBinding $installedManifest $Policy
            if ($installedManifest.Sequence -ne $manifest.Sequence -or
                (Get-RebornSha256 $paths.Legacy) -ne
                    $Policy.LegacyNetSha256 -or
                (Get-RebornSha256 $paths.Net) -ne
                    $Policy.CandidateNetSha256) {
                throw 'Secure bundle staged files failed final validation.'
            }
            if ($StateProvider -eq 'Hklm') {
                Assert-RebornProtectedFileSet `
                    @($paths.Origin, $paths.Net, $paths.Legacy,
                        $paths.InstalledManifest) `
                    'installed live client file'
            }
            if ($null -ne $PreCommitValidation) {
                & $PreCommitValidation $originLock
            }

            $secureState = New-RebornActivationState `
                -Mode 1 `
                -Environment $manifest.Environment `
                -SequenceFloor $manifest.Sequence
            Write-RebornActivationState `
                $StateProvider $StatePath $secureState `
                -AllowHklmWrite:$AllowHklmWrite

            $finalState =
                Get-RebornActivationState $StateProvider $StatePath
            if ($finalState.Mode -ne 1 -or
                $finalState.Environment -ne $manifest.Environment -or
                $finalState.SequenceFloor -lt $manifest.Sequence) {
                throw 'Secure bundle did not reach InstalledExact.'
            }
            return [pscustomobject]@{
                Result = 'InstalledExact'
                BackupPath = $backup
                ManifestSequence = $manifest.Sequence
            }
        }
        catch {
            if (-not $LeaveInterrupted) {
                $saved = $_
                $loaded = Read-RebornBackupReceipt `
                    $backup `
                    $paths.Client `
                    $Policy `
                    $manifest
                $current =
                    Get-RebornActivationState $StateProvider $StatePath
                Set-RebornSafeDisabledState `
                    $current $manifest $Policy $StateProvider $StatePath `
                    -AllowHklmWrite:$AllowHklmWrite
                Restore-RebornBackupFiles $loaded $paths
                throw $saved
            }
            throw
        }
    }
    finally {
        $originLock.Dispose()
    }
}

function Invoke-RebornSecureBundleRestore {
    param(
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][string]$ClientRoot,
        [AllowNull()][AllowEmptyString()][string]$CandidatePath,
        [AllowNull()][AllowEmptyString()][string]$ManifestPath,
        [AllowNull()][AllowEmptyString()][string]$TrustPath,
        [Parameter(Mandatory)][string]$ApplyBackupPath,
        [Parameter(Mandatory)][string]$BackupRoot,
        [Parameter(Mandatory)]
        [ValidateSet('OfflineFile', 'Hklm')][string]$StateProvider,
        [string]$StatePath,
        [switch]$AllowHklmWrite
    )

    if ($StateProvider -eq 'Hklm') {
        Assert-RebornProtectedDirectoryPath `
            $ClientRoot 'ClientRoot' -ProtectContents | Out-Null
        $backupBase = Assert-RebornProtectedDirectoryPath `
            $BackupRoot 'BackupRoot' `
            -ProtectContents -RequireProtectedAcl
    } else {
        $backupBase =
            Assert-RebornDirectoryPath $BackupRoot 'BackupRoot'
    }
    $backupName = [IO.Path]::GetFileName(
        [IO.Path]::GetFullPath($ApplyBackupPath).TrimEnd('\'))
    if ($backupName -cnotmatch (
            '^client-secure-bundle-v2-Apply-' +
            '\d{8}-\d{9}-[0-9a-f]{32}$')) {
        throw 'ApplyBackupPath does not have an issued backup name.'
    }
    Assert-RebornDirectChildDirectory `
        $ApplyBackupPath $backupBase 'ApplyBackupPath' `
        -RequireProtected:($StateProvider -eq 'Hklm') | Out-Null

    $recovery = Get-RebornRecoveryInputSet `
        $ApplyBackupPath $Policy
    $paths = Assert-RebornBundleInputs `
        $Policy $ClientRoot $recovery.Candidate `
        $recovery.Manifest $recovery.Trust
    if ($StateProvider -eq 'Hklm') {
        Assert-RebornProtectedFileSet `
            @($paths.Origin, $paths.Net, $paths.Legacy,
                $paths.InstalledManifest) `
            'live client file'
    }
    $manifest = Read-RebornSecureEndpointManifestForRestore `
        $paths.Manifest $paths.Trust 0
    Assert-RebornManifestPolicyBinding $manifest $Policy
    $backup = Read-RebornBackupReceipt `
        $ApplyBackupPath `
        $paths.Client `
        $Policy `
        $manifest
    $originLock = Open-RebornOriginMutationLock $paths.Origin
    try {
        $current = Get-RebornActivationState $StateProvider $StatePath
        $sourceState = Assert-RebornRestoreSourceState `
            $paths $Policy $manifest $backup.Receipt $current
        Set-RebornSafeDisabledState `
            $current $manifest $Policy $StateProvider $StatePath `
            -AllowHklmWrite:$AllowHklmWrite
        Restore-RebornBackupFiles $backup $paths

        $netHash = Get-RebornSha256 $paths.Net
        $legacyHash = Get-RebornOptionalHash $paths.Legacy
        $manifestHash = Get-RebornOptionalHash $paths.InstalledManifest
        if ($netHash -ne $Policy.LegacyNetSha256 -or
            $null -ne $legacyHash -or
            $null -ne $manifestHash) {
            throw 'Secure bundle Restore did not reproduce the exact predecessor files.'
        }

        [pscustomobject]@{
            Result = 'StockFilesRestored'
            ApplyBackupPath = $backup.Directory
            AcceptedSourceState = $sourceState
            SequenceFloorRetained = (
                Get-RebornActivationState $StateProvider $StatePath
            ).SequenceFloor
        }
    }
    finally {
        $originLock.Dispose()
    }
}

Export-ModuleMember -Function @(
    'New-RebornSecureBundlePolicy',
    'Get-RebornSecureBundleStatus',
    'Invoke-RebornSecureBundleApply',
    'Invoke-RebornSecureBundleRestore'
)
