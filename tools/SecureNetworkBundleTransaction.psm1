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

function Get-RebornSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-RebornHash {
    param([string]$Value)
    return $Value -match '^[0-9A-F]{64}$'
}

function New-RebornSecureBundlePolicy {
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

function Get-RebornSecureBundleStatus {
    param(
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$TrustPath,
        [Parameter(Mandatory)]
        [ValidateSet('OfflineFile', 'Hklm')][string]$StateProvider,
        [string]$StatePath
    )

    $paths = Assert-RebornBundleInputs `
        $Policy $ClientRoot $CandidatePath $ManifestPath $TrustPath
    $state = Get-RebornActivationState `
        -Provider $StateProvider `
        -Path $StatePath
    $manifest = Read-RebornSecureEndpointManifest `
        -ManifestPath $paths.Manifest `
        -TrustPath $paths.Trust `
        -InstalledSequenceFloor $state.SequenceFloor
    Assert-RebornManifestPolicyBinding $manifest $Policy
    $manifestHash = $manifest.ManifestSha256
    $netHash = Get-RebornSha256 $paths.Net
    $legacyHash = Get-RebornOptionalHash $paths.Legacy
    $installedManifestHash =
        Get-RebornOptionalHash $paths.InstalledManifest

    $status = if (
        $netHash -eq $Policy.LegacyNetSha256 -and
        $null -eq $legacyHash -and
        $null -eq $installedManifestHash -and
        $state.Mode -eq 0
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
        -Environment $VerifiedManifest.Environment `
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
        [switch]$LeaveInterrupted
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
        $paths.Manifest $paths.Trust $state.SequenceFloor
    Assert-RebornManifestPolicyBinding $manifest $Policy
    $status = Get-RebornSecureBundleStatus `
        $Policy $paths.Client $paths.Candidate $paths.Manifest $paths.Trust `
        $StateProvider $StatePath
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
    $receipt = [ordered]@{
        schemaVersion = 2
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
    }
    Write-RebornBackupReceipt $receipt $backup

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
                $paths.InstalledManifest $paths.Trust $manifest.Sequence
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
        [Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$TrustPath,
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

    $paths = Assert-RebornBundleInputs `
        $Policy $ClientRoot $CandidatePath $ManifestPath $TrustPath
    if ($StateProvider -eq 'Hklm') {
        Assert-RebornProtectedFileSet `
            @($paths.Origin, $paths.Net, $paths.Legacy,
                $paths.InstalledManifest) `
            'live client file'
    }
    $manifest = Read-RebornSecureEndpointManifest `
        $paths.Manifest $paths.Trust 0
    Assert-RebornManifestPolicyBinding $manifest $Policy
    $backup = Read-RebornBackupReceipt `
        $ApplyBackupPath `
        $paths.Client `
        $Policy `
        $manifest
    $current = Get-RebornActivationState $StateProvider $StatePath
    $originLock = Open-RebornOriginMutationLock $paths.Origin
    try {
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
