[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTransaction.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkPathSafety.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureEndpointManifestValidation.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTestFixtures.psm1'
) -Force

$authenticatedUsers =
    [Security.Principal.SecurityIdentifier]::new('S-1-5-11')
$inheritance =
    [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
$inheritOnlyModify =
    [Security.AccessControl.FileSystemAccessRule]::new(
        $authenticatedUsers,
        [Security.AccessControl.FileSystemRights]::Modify,
        $inheritance,
        [Security.AccessControl.PropagationFlags]::InheritOnly,
        [Security.AccessControl.AccessControlType]::Allow)
$pathSafetyModule = Get-Module SecureNetworkPathSafety
$inheritOnlyHazard = & $pathSafetyModule {
    param($Rule)
    Test-RebornDirectoryRuleHazard $Rule $true $true $false
} $inheritOnlyModify
Assert-True $inheritOnlyHazard (
    'an unsafe inheritance-only client-content ACE was ignored')

$root = Join-Path (
    [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts'))
) ('slice8-bundle-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $reparseTarget = Join-Path $root 'reparse-target'
    $reparsePath = Join-Path $root 'reparse-link'
    [IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    $reparseRejected = $false
    try {
        New-Item `
            -ItemType Junction `
            -Path $reparsePath `
            -Target $reparseTarget | Out-Null
        try {
            Assert-RebornDirectoryPath `
                $reparsePath 'reparse fixture' | Out-Null
        }
        catch {
            $reparseRejected = $true
        }
    }
    finally {
        if (Test-Path -LiteralPath $reparsePath) {
            [IO.Directory]::Delete($reparsePath, $false)
        }
        [IO.Directory]::Delete($reparseTarget, $false)
    }
    Assert-True $reparseRejected 'reparse path was accepted'

    $client = Join-Path $root 'client'
    $inputs = Join-Path $root 'inputs'
    $backups = Join-Path $root 'backups'
    [IO.Directory]::CreateDirectory($client) | Out-Null
    [IO.Directory]::CreateDirectory($inputs) | Out-Null
    [IO.Directory]::CreateDirectory($backups) | Out-Null
    $origin = Join-Path $client 'Origin.exe'
    $stock = Join-Path $client 'Net.dll'
    $candidate = Join-Path $inputs 'Net.dll'
    $manifest = Join-Path $inputs 'RebornNetwork.gwem'
    $trust = Join-Path $inputs 'manifest-trust.json'
    $state = Join-Path $root 'activation-state.json'
    Write-TestBytes $origin 4096 3
    Write-TestBytes $stock 2048 7
    Write-TestBytes $candidate 3072 11
    New-SignedManifestFixture $manifest $trust

    $originHash = (Get-FileHash $origin -Algorithm SHA256).Hash
    $stockHash = (Get-FileHash $stock -Algorithm SHA256).Hash
    $candidateHash = (Get-FileHash $candidate -Algorithm SHA256).Hash
    $manifestHash = (Get-FileHash $manifest -Algorithm SHA256).Hash
    $trustHash = (Get-FileHash $trust -Algorithm SHA256).Hash
    $policy = New-RebornSecureBundlePolicy `
        $originHash $stockHash $candidateHash $manifestHash $trustHash

    $initial = Get-RebornSecureBundleStatus `
        $policy $client $candidate $manifest $trust `
        OfflineFile $state
    Assert-True ($initial.State -eq 'Stock') 'fixture was not Stock'

    $applied = Invoke-RebornSecureBundleApply `
        $policy $client $candidate $manifest $trust $backups `
        OfflineFile $state
    Assert-True (
        $applied.Result -eq 'InstalledExact'
    ) 'Apply did not finish'
    $installed = Get-RebornSecureBundleStatus `
        $policy $client $candidate $manifest $trust `
        OfflineFile $state
    Assert-True (
        $installed.State -eq 'InstalledExact' -and
        $installed.SequenceFloor -eq 7
    ) 'installed bundle or floor was incorrect'

    $wrongBackups = Join-Path $root 'wrong-backups'
    [IO.Directory]::CreateDirectory($wrongBackups) | Out-Null
    $escapedBackupRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client $candidate $manifest $trust `
            $applied.BackupPath $wrongBackups OfflineFile $state |
            Out-Null
    }
    catch {
        $escapedBackupRejected = $true
    }
    Assert-True (
        $escapedBackupRejected -and
        (Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state).State -eq 'InstalledExact'
    ) 'Restore accepted a backup outside the configured BackupRoot'

    $restored = Invoke-RebornSecureBundleRestore `
        $policy $client $candidate $manifest $trust `
        $applied.BackupPath $backups OfflineFile $state
    $afterRestore = Get-RebornSecureBundleStatus `
        $policy $client $candidate $manifest $trust `
        OfflineFile $state
    Assert-True (
        $restored.Result -eq 'StockFilesRestored' -and
        $afterRestore.State -eq 'Stock' -and
        $afterRestore.SequenceFloor -eq 7 -and
        -not (Test-Path (Join-Path $client 'NetLegacy.dll')) -and
        -not (Test-Path (Join-Path $client 'RebornNetwork.gwem'))
    ) 'Restore did not reproduce Stock while retaining the floor'

    $highManifest = Join-Path $inputs 'UnpinnedHighSequence.gwem'
    $highTrust = Join-Path $inputs 'unpinned-high-trust.json'
    New-SignedManifestFixture $highManifest $highTrust 1000
    $highVerified = Read-RebornSecureEndpointManifest `
        $highManifest $highTrust 0
    Assert-True (
        $highVerified.ManifestSha256 -ceq
            (Get-FileHash $highManifest -Algorithm SHA256).Hash -and
        $highVerified.TrustSha256 -ceq
            (Get-FileHash $highTrust -Algorithm SHA256).Hash
    ) 'verified manifest did not retain exact-byte hashes'
    $transactionModule =
        Get-Module -Name SecureNetworkBundleTransaction
    $unboundHighSequenceRejected = $false
    try {
        & $transactionModule {
            param(
                $Current,
                $VerifiedManifest,
                $Policy,
                $StatePath
            )
            Set-RebornSafeDisabledState `
                $Current `
                $VerifiedManifest `
                $Policy `
                'OfflineFile' `
                $StatePath
        } ([pscustomobject]@{ SequenceFloor = [UInt64]7 }) `
            $highVerified $policy $state
    }
    catch {
        $unboundHighSequenceRejected = $true
    }
    Assert-True (
        $unboundHighSequenceRejected -and
        (Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state).SequenceFloor -eq 7
    ) 'unpinned higher manifest sequence changed the rollback floor'

    $sentinel = Join-Path $root 'receipt-path-sentinel.bin'
    Write-TestBytes $sentinel 64 19
    $sentinelHash = (Get-FileHash $sentinel -Algorithm SHA256).Hash
    $receiptPath = Join-Path $applied.BackupPath 'receipt.json'
    $forgedReceipt =
        Get-Content -LiteralPath $receiptPath -Raw |
        ConvertFrom-Json
    $forgedReceipt.files[2].path = '..\receipt-path-sentinel.bin'
    [IO.File]::WriteAllText(
        $receiptPath,
        ($forgedReceipt | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $applied.BackupPath 'receipt.sha256'),
        (Get-FileHash $receiptPath -Algorithm SHA256).Hash,
        [Text.UTF8Encoding]::new($false))
    $forgedReceiptRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client $candidate $manifest $trust `
            $applied.BackupPath $backups OfflineFile $state | Out-Null
    }
    catch {
        $forgedReceiptRejected = $true
    }
    Assert-True (
        $forgedReceiptRejected -and
        (Get-FileHash $sentinel -Algorithm SHA256).Hash -eq $sentinelHash
    ) 'forged receipt path was not rejected before mutation'

    $forgedReceipt.files[2].path = 'RebornNetwork.gwem'
    $forgedReceipt.manifest.sequence = [Int64]::MaxValue.ToString()
    $forgedReceipt.stateBefore.sequenceFloor =
        [Int64]::MaxValue.ToString()
    [IO.File]::WriteAllText(
        $receiptPath,
        ($forgedReceipt | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $applied.BackupPath 'receipt.sha256'),
        (Get-FileHash $receiptPath -Algorithm SHA256).Hash,
        [Text.UTF8Encoding]::new($false))
    $forgedFloorRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client $candidate $manifest $trust `
            $applied.BackupPath $backups OfflineFile $state | Out-Null
    }
    catch {
        $forgedFloorRejected = $true
    }
    Assert-True (
        $forgedFloorRejected -and
        (Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state).SequenceFloor -eq 7
    ) 'forged receipt metadata changed the rollback floor'

    foreach ($point in @(
        'AfterState',
        'AfterManifest',
        'AfterLegacy',
        'AfterCandidate'
    )) {
        $threw = $false
        try {
            Invoke-RebornSecureBundleApply `
                $policy $client $candidate $manifest $trust $backups `
                OfflineFile $state -FailurePoint $point | Out-Null
        }
        catch {
            $threw = $true
        }
        $rolledBack = Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state
        Assert-True (
            $threw -and $rolledBack.State -eq 'Stock'
        ) "interruption $point did not roll back"
    }

    $expiredRoot = Join-Path $root 'expired-recovery'
    $expiredClient = Join-Path $expiredRoot 'client'
    $expiredInputs = Join-Path $expiredRoot 'inputs'
    $expiredBackups = Join-Path $expiredRoot 'backups'
    foreach ($directory in @(
        $expiredClient,
        $expiredInputs,
        $expiredBackups
    )) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $expiredOrigin = Join-Path $expiredClient 'Origin.exe'
    $expiredStock = Join-Path $expiredClient 'Net.dll'
    $expiredCandidate = Join-Path $expiredInputs 'Net.dll'
    $expiredManifest = Join-Path $expiredInputs 'RebornNetwork.gwem'
    $expiredTrust = Join-Path $expiredInputs 'manifest-trust.json'
    $expiredState = Join-Path $expiredRoot 'activation-state.json'
    Write-TestBytes $expiredOrigin 4096 31
    Write-TestBytes $expiredStock 2048 37
    Write-TestBytes $expiredCandidate 3072 41
    $validAt = [DateTimeOffset]::Parse('2026-01-01T00:00:00Z')
    New-SignedManifestFixture `
        $expiredManifest $expiredTrust 7 $validAt
    $expiredPolicy = New-RebornSecureBundlePolicy `
        (Get-FileHash $expiredOrigin -Algorithm SHA256).Hash `
        (Get-FileHash $expiredStock -Algorithm SHA256).Hash `
        (Get-FileHash $expiredCandidate -Algorithm SHA256).Hash `
        (Get-FileHash $expiredManifest -Algorithm SHA256).Hash `
        (Get-FileHash $expiredTrust -Algorithm SHA256).Hash
    $expiredApplied = Invoke-RebornSecureBundleApply `
        $expiredPolicy $expiredClient $expiredCandidate `
        $expiredManifest $expiredTrust $expiredBackups `
        OfflineFile $expiredState -Now $validAt
    $expiredReceipt = Get-Content -LiteralPath (
        Join-Path $expiredApplied.BackupPath 'receipt.json'
    ) -Raw | ConvertFrom-Json
    Assert-True (
        $expiredReceipt.schemaVersion -eq 3 -and
        @($expiredReceipt.recoveryInputs).Count -eq 3
    ) 'Apply did not retain a schema-3 recovery input set'

    $expiredAt = $validAt.AddHours(2)
    $expiredStatusError = $null
    $expiredApplyError = $null
    try {
        Get-RebornSecureBundleStatus `
            $expiredPolicy $expiredClient $expiredCandidate `
            $expiredManifest $expiredTrust OfflineFile $expiredState `
            -Now $expiredAt | Out-Null
    } catch { $expiredStatusError = $_.Exception.Message }
    try {
        Invoke-RebornSecureBundleApply `
            $expiredPolicy $expiredClient $expiredCandidate `
            $expiredManifest $expiredTrust $expiredBackups `
            OfflineFile $expiredState -Now $expiredAt | Out-Null
    } catch { $expiredApplyError = $_.Exception.Message }
    Assert-True (
        $expiredStatusError -match 'outside its validity interval' -and
        $expiredApplyError -match 'outside its validity interval'
    ) 'expired Status or Apply did not fail closed'

    $recoveryManifest = Join-Path (
        $expiredApplied.BackupPath
    ) 'endpoint-manifest.gwem'
    $damagedRecovery = [IO.File]::ReadAllBytes($recoveryManifest)
    $damagedRecovery[80] = $damagedRecovery[80] -bxor 1
    [IO.File]::WriteAllBytes($recoveryManifest, $damagedRecovery)
    $damagedRecoveryRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $expiredPolicy $expiredClient '' '' '' `
            $expiredApplied.BackupPath $expiredBackups `
            OfflineFile $expiredState | Out-Null
    } catch { $damagedRecoveryRejected = $true }
    Assert-True $damagedRecoveryRejected (
        'Restore accepted a modified self-contained recovery input')
    Copy-Item -LiteralPath $expiredManifest `
        -Destination $recoveryManifest -Force
    Remove-Item -LiteralPath @(
        $expiredCandidate,
        $expiredManifest,
        $expiredTrust
    ) -Force

    $expiredRestored = Invoke-RebornSecureBundleRestore `
        $expiredPolicy $expiredClient '' '' '' `
        $expiredApplied.BackupPath $expiredBackups `
        OfflineFile $expiredState
    $expiredStateDocument =
        Get-Content -LiteralPath $expiredState -Raw | ConvertFrom-Json
    Assert-True (
        $expiredRestored.Result -eq 'StockFilesRestored' -and
        (Get-FileHash $expiredStock -Algorithm SHA256).Hash -ceq
            $expiredPolicy.LegacyNetSha256 -and
        $expiredStateDocument.activationMode -eq '0' -and
        $expiredStateDocument.sequenceFloor -eq '7'
    ) 'expired self-contained Restore did not recover exact Stock'

    $tampered = [IO.File]::ReadAllBytes($manifest)
    $tampered[80] = $tampered[80] -bxor 1
    [IO.File]::WriteAllBytes($manifest, $tampered)
    $tamperRejected = $false
    try {
        Get-RebornSecureBundleStatus `
            $policy $client $candidate $manifest $trust `
            OfflineFile $state | Out-Null
    }
    catch {
        $tamperRejected = $true
    }
    Assert-True $tamperRejected 'tampered manifest was accepted'

    Write-Host 'Secure network bundle core transaction checks passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $artifactRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts')).TrimEnd('\')
    if ($resolved.StartsWith(
            $artifactRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

& (Join-Path $PSScriptRoot 'TestSecureNetworkBundleRestoreState.ps1')
& (Join-Path $PSScriptRoot 'TestSecureNetworkActivationCommit.ps1')
& (Join-Path $PSScriptRoot 'TestSecureNetworkPairedOriginBundle.ps1')
& (Join-Path $PSScriptRoot 'TestSecureNetworkOriginContractGate.ps1')
Write-Host 'Secure network bundle transaction checks passed.'
