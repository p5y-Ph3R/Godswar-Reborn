[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTransaction.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleTestFixtures.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkActivationState.psm1'
) -Force

$root = Join-Path (
    [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\artifacts'))
) ('slice8-restore-state-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $client = Join-Path $root 'client'
    $inputs = Join-Path $root 'inputs'
    $backups = Join-Path $root 'backups'
    foreach ($directory in @($client, $inputs, $backups)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $origin = Join-Path $client 'Origin.exe'
    $net = Join-Path $client 'Net.dll'
    $candidate = Join-Path $inputs 'Net.dll'
    $manifest = Join-Path $inputs 'RebornNetwork.gwem'
    $trust = Join-Path $inputs 'manifest-trust.json'
    $state = Join-Path $root 'activation-state.json'
    Write-TestBytes $origin 4096 53
    Write-TestBytes $net 2048 59
    Write-TestBytes $candidate 3072 61
    New-SignedManifestFixture $manifest $trust 7

    $originHash = (Get-FileHash $origin -Algorithm SHA256).Hash
    $stockHash = (Get-FileHash $net -Algorithm SHA256).Hash
    $policy = New-RebornSecureBundlePolicy `
        $originHash `
        $stockHash `
        (Get-FileHash $candidate -Algorithm SHA256).Hash `
        (Get-FileHash $manifest -Algorithm SHA256).Hash `
        (Get-FileHash $trust -Algorithm SHA256).Hash

    $applied = Invoke-RebornSecureBundleApply `
        $policy $client $candidate $manifest $trust $backups `
        OfflineFile $state
    Write-TestBytes $net 3333 67
    $unknownBefore = Get-TestManagedState $client $state
    $unknownRejected = $false
    $unknownError = $null
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client '' '' '' $applied.BackupPath $backups `
            OfflineFile $state | Out-Null
    }
    catch {
        $unknownRejected = $true
        $unknownError = $_.Exception.Message
    }
    $unknownAfter = Get-TestManagedState $client $state
    Assert-True (
        $unknownRejected -and
        $unknownError -match 'No mutation was performed' -and
        (Test-TestManagedStateEqual $unknownBefore $unknownAfter)
    ) 'unknown current Net.dll was not rejected before every mutation'

    [IO.File]::Copy($candidate, $net, $true)
    $normalRestore = Invoke-RebornSecureBundleRestore `
        $policy $client '' '' '' $applied.BackupPath $backups `
        OfflineFile $state
    Assert-True (
        $normalRestore.Result -eq 'StockFilesRestored' -and
        $normalRestore.AcceptedSourceState -eq 'InstalledExact'
    ) 'normal InstalledExact Restore did not report its accepted source state'
    $normalDisabled = Get-RebornActivationState OfflineFile $state
    Assert-True (
        $normalDisabled.Exists -and
        $normalDisabled.Mode -eq 0 -and
        $normalDisabled.Environment -eq 1 -and
        $normalDisabled.SequenceFloor -eq 7
    ) 'normal Restore did not retain exact manifest environment and floor'

    foreach ($invalid in @(
        [pscustomobject]@{
            Name = 'environment zero'
            State = New-RebornActivationState 0 0 7
        },
        [pscustomobject]@{
            Name = 'wrong environment'
            State = New-RebornActivationState 0 2 7
        },
        [pscustomobject]@{
            Name = 'wrong floor'
            State = New-RebornActivationState 0 1 8
        }
    )) {
        Write-RebornActivationState OfflineFile $state $invalid.State
        $beforeInvalid = Get-TestManagedState $client $state
        $invalidRejected = $false
        try {
            Invoke-RebornSecureBundleRestore `
                $policy $client '' '' '' $applied.BackupPath $backups `
                OfflineFile $state | Out-Null
        }
        catch {
            $invalidRejected = $true
        }
        $afterInvalid = Get-TestManagedState $client $state
        Assert-True (
            $invalidRejected -and
            (Test-TestManagedStateEqual $beforeInvalid $afterInvalid)
        ) "Restore accepted $($invalid.Name) activation authority"
    }

    [IO.File]::WriteAllText(
        $state,
        '{"activationMode":"0","sequenceFloor":"7"}',
        [Text.UTF8Encoding]::new($false))
    $partialStateHash =
        (Get-FileHash $state -Algorithm SHA256).Hash
    $partialNetHash =
        (Get-FileHash $net -Algorithm SHA256).Hash
    $partialRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client '' '' '' $applied.BackupPath $backups `
            OfflineFile $state | Out-Null
    }
    catch {
        $partialRejected = $true
    }
    Assert-True (
        $partialRejected -and
        (Get-FileHash $state -Algorithm SHA256).Hash -ceq
            $partialStateHash -and
        (Get-FileHash $net -Algorithm SHA256).Hash -ceq
            $partialNetHash
    ) 'Restore accepted or mutated a partial activation state'
    Write-RebornActivationState `
        OfflineFile $state (New-RebornActivationState 0 1 7)

    $idempotentRestore = Invoke-RebornSecureBundleRestore `
        $policy $client '' '' '' $applied.BackupPath $backups `
        OfflineFile $state
    Assert-True (
        $idempotentRestore.AcceptedSourceState -eq 'StockDisabled'
    ) 'exact Stock/idempotent Restore was not accepted'

    $expectedInterruptedStates = [ordered]@{
        AfterState = 'StockDisabled'
        AfterManifest = 'AfterManifest'
        AfterLegacy = 'AfterLegacy'
        AfterCandidate = 'AfterCandidate'
    }
    foreach ($failurePoint in $expectedInterruptedStates.Keys) {
        $existing = @(
            Get-ChildItem -LiteralPath $backups -Directory |
                ForEach-Object { $_.FullName }
        )
        $interrupted = $false
        try {
            Invoke-RebornSecureBundleApply `
                $policy $client $candidate $manifest $trust $backups `
                OfflineFile $state `
                -FailurePoint $failurePoint `
                -LeaveInterrupted | Out-Null
        }
        catch {
            $interrupted = $_.Exception.Message -match (
                'Simulated interruption')
        }
        $created = @(
            Get-ChildItem -LiteralPath $backups -Directory |
                Where-Object { $_.FullName -notin $existing }
        )
        Assert-True (
            $interrupted -and $created.Count -eq 1
        ) "could not identify interrupted Apply receipt: $failurePoint"

        $recovered = Invoke-RebornSecureBundleRestore `
            $policy $client '' '' '' $created[0].FullName $backups `
            OfflineFile $state
        Assert-True (
            $recovered.AcceptedSourceState -ceq
                $expectedInterruptedStates[$failurePoint] -and
            (Get-FileHash $net -Algorithm SHA256).Hash -ceq $stockHash -and
            -not (Test-Path (Join-Path $client 'NetLegacy.dll')) -and
            -not (Test-Path (Join-Path $client 'RebornNetwork.gwem'))
        ) "Restore rejected or misclassified stage: $failurePoint"
    }

    $candidate2 = Join-Path $inputs 'Net-v2.dll'
    $manifest2 = Join-Path $inputs 'RebornNetwork-v2.gwem'
    $trust2 = Join-Path $inputs 'manifest-trust-v2.json'
    Write-TestBytes $candidate2 3584 71
    New-SignedManifestFixture $manifest2 $trust2 8
    $policy2 = New-RebornSecureBundlePolicy `
        $originHash `
        $stockHash `
        (Get-FileHash $candidate2 -Algorithm SHA256).Hash `
        (Get-FileHash $manifest2 -Algorithm SHA256).Hash `
        (Get-FileHash $trust2 -Algorithm SHA256).Hash
    $applied2 = Invoke-RebornSecureBundleApply `
        $policy2 $client $candidate2 $manifest2 $trust2 $backups `
        OfflineFile $state

    $newInstallBefore = Get-TestManagedState $client $state
    $oldReceiptRejected = $false
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client '' '' '' $applied.BackupPath $backups `
            OfflineFile $state | Out-Null
    }
    catch {
        $oldReceiptRejected = $true
    }
    $newInstallAfter = Get-TestManagedState $client $state
    Assert-True (
        $oldReceiptRejected -and
        (Test-TestManagedStateEqual $newInstallBefore $newInstallAfter)
    ) 'stale receipt changed a newer installed transaction'

    $restored2 = Invoke-RebornSecureBundleRestore `
        $policy2 $client '' '' '' $applied2.BackupPath $backups `
        OfflineFile $state
    Assert-True (
        $restored2.AcceptedSourceState -eq 'InstalledExact'
    ) 'newer transaction did not restore through its own receipt'

    $staleStateBefore = Get-TestManagedState $client $state
    $staleStateRejected = $false
    $staleStateError = $null
    try {
        Invoke-RebornSecureBundleRestore `
            $policy $client '' '' '' $applied.BackupPath $backups `
            OfflineFile $state | Out-Null
    }
    catch {
        $staleStateRejected = $true
        $staleStateError = $_.Exception.Message
    }
    $staleStateAfter = Get-TestManagedState $client $state
    Assert-True (
        $staleStateRejected -and
        $staleStateError -match 'No mutation was performed' -and
        (Test-TestManagedStateEqual $staleStateBefore $staleStateAfter)
    ) 'stale receipt/floor was not rejected before every mutation'

    Write-Host 'Secure bundle restore source-state checks passed.'
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
