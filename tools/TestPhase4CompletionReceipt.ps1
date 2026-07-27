$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'Phase4SecureDockerClientCampaign.psm1',
    'Phase4LoopbackAcceptanceProfile.psm1',
    'ControlledHostPrivacyEvidence.psm1',
    'Phase4CompletionValidation.psm1',
    'Phase4CompletionReceipt.psm1',
    'Phase4CompletionReceiptTestSupport.psm1'
)) {
    Import-Module (Join-Path $PSScriptRoot $moduleName) -Force
}
# Composite modules reload this dependency in their private session state.
# Re-import it last so its public pin helpers remain available to this harness.
Import-Module (
    Join-Path $PSScriptRoot 'Phase4SecureDockerClientCampaign.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'Phase4LoopbackAcceptanceProfile.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostPrivacyEvidence.psm1'
) -Force

$passed = 0
$expectedChecks = 20

function Invoke-Check {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    try {
        & $Body
    }
    catch {
        $script:passed++
        Write-Host "PASS $Name"
        return
    }
    throw "Expected failure did not occur: $Name"
}

$root = Join-Path (
    [IO.Path]::GetTempPath()
) ('reborn-phase4-completion-test-' + [Guid]::NewGuid().ToString('N'))
$v3Root = $null
$v2Root = $null
$pins = Get-RebornPhase4SecureDockerPins
$serverHash = 'A' * 64
$releaseHash = 'B' * 64
$optionsHash = 'C' * 64

function New-TestEvidenceLines {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Baseline', 'Fallback', 'Soak')]
        [string]$Profile
    )

    $start =
        '[controlled-host] privacy-safe evidence channel started'
    $common = @(
        '[controlled-host] secure listeners ready',
        '[controlled-host] TLS policy accepted',
        '[controlled-host] accepted secure preface response written',
        '[controlled-host] TLS client authenticated',
        '[controlled-host] UDP endpoint authenticated and bound',
        '[secure-acceptance] authoritative UDP movement accepted',
        '[secure-acceptance] authoritative UDP snapshot queued')
    $stop = '[controlled-host] secure server stopping'
    if ($Profile -ne 'Fallback') {
        return @($start) + $common + @($stop)
    }
    return @(
        $start,
        '[secure-acceptance] phase4 fault campaign enabled'
    ) + $common + @(
        '[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32',
        '[secure-acceptance] one-way TLS fallback observed',
        '[secure-acceptance] authoritative correction forced reason=not_ready',
        '[secure-acceptance] post-fallback TLS movement observed no_switchback=true',
        $stop)
}

function New-TestProfileResult {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Baseline', 'Fallback', 'Soak')]
        [string]$Profile,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][double]$Duration,
        [string]$ServerSha256 = $script:serverHash,
        [switch]$AllowInvalidDuration
    )

    $evidencePath = Join-Path $script:root "secure-server-$Name.log"
    $lines = New-TestEvidenceLines $Profile
    [IO.File]::WriteAllText(
        $evidencePath,
        (($lines -join [Environment]::NewLine) +
            [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $evidence = if ($AllowInvalidDuration) {
        Assert-RebornControlledHostPrivacyEvidence `
            $evidencePath -RequireStopped
    }
    else {
        Assert-RebornControlledHostPrivacyEvidence `
            $evidencePath `
            -Profile $Profile `
            -ObservedDuration ([TimeSpan]::FromSeconds($Duration)) `
            -RequireStopped
    }
    $record = New-RebornPhase4AcceptanceProfileRecord `
        -CampaignId $script:campaign.Record.campaignId `
        -IssuedUserSid $script:campaign.Record.issuedUserSid `
        -EvidenceProfile $Profile `
        -ObservedDurationSeconds $Duration `
        -EvidencePath $evidencePath `
        -EvidenceSha256 (
            Get-FileHash $evidencePath -Algorithm SHA256).Hash `
        -EvidenceBytes $evidence.Bytes `
        -EvidenceEvents $evidence.Events `
        -ServerSha256 $ServerSha256 `
        -ManagedReleaseSetSha256 $script:releaseHash `
        -OptionsSha256 $script:optionsHash `
        -CandidateSha256 $script:pins.CandidateSha256 `
        -ManifestSha256 $script:pins.ManifestSha256 `
        -DatabaseName $script:pins.DockerDatabase
    return Write-RebornPhase4AcceptanceProfileResult `
        $record `
        -SetAclAction { param($Path, $Security) } `
        -AllowTestHook
}

try {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $campaignRecord = New-RebornPhase4CampaignRecord `
        -BackupBaselineNames @() -Pins $pins
    $campaignRecord.state = 'Restored'
    $campaignRecord.trustState = 'EmbeddedRootReleased'
    $campaignRecord.hostsState = 'Restored'
    $campaignRecord.bundleState = 'Restored'
    $campaign = Write-RebornPhase4CampaignReceipt `
        $campaignRecord $root -AllowTestPath -Pins $pins

    $baseline = New-TestProfileResult `
        Baseline baseline 30
    $fallback = New-TestProfileResult `
        Fallback fallback 20
    $soak = New-TestProfileResult `
        Soak soak 600
    $profilePaths = @(
        $baseline.ProfileResultPath,
        $fallback.ProfileResultPath,
        $soak.ProfileResultPath)

    $manual = New-RebornPhase4ManualAttestation `
        -AlternatingAccounts `
        -PreviewReadiness `
        -UnmountedMovement `
        -MountedMovement `
        -WorldGenerationChanges `
        -DeathAndRevive `
        -SessionLifecycle `
        -FallbackCorrection `
        -SoakStability `
        -DatabaseMutationReviewed `
        -ViewerParity Passed
    $status = [pscustomobject]@{
        State = 'Restored'
        DockerState = 'HealthyExact'
        BundleState = 'Stock'
        HostsState = 'Absent'
        RootState = 'Absent'
        ActivationMode = [UInt64]0
        ActivationEnvironment = $pins.ActivationEnvironment
        SequenceFloor = $pins.ManifestSequence
        ManifestSequence = $pins.ManifestSequence
        HandoffState = 'Restored'
        HandoffPath = $campaign.Path
    }
    $docker = [pscustomobject]@{
        State = 'HealthyExact'
        Profile = $pins.DockerProfile
        Database = $pins.DockerDatabase
        TcpPorts = @(6599, 7443)
        UdpPort = 7444
        RestartCount = 0
    }

    Invoke-Check {
        $profiles = @(
            foreach ($path in $profilePaths) {
                Read-RebornPhase4ProfileResult `
                    $path -AllowTestPath -Pins $pins
            })
        if ((@($profiles.Record.profile) -join ',') -cne
            'Baseline,Fallback,Soak') {
            throw 'The exact profile set did not reopen.'
        }
    } 'three durable profile results revalidate'

    Assert-Throws {
        $missing = New-RebornPhase4ManualAttestation `
            -AlternatingAccounts -ViewerParity Unavailable
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $missing $status $docker `
            $root -AllowTestPath -Pins $pins | Out-Null
    } 'incomplete manual matrix is rejected'

    $mismatched = New-TestProfileResult `
        Soak mismatch 600 -ServerSha256 ('D' * 64)
    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            @(
                $baseline.ProfileResultPath,
                $fallback.ProfileResultPath,
                $mismatched.ProfileResultPath) `
            $manual $status $docker $root `
            -AllowTestPath -Pins $pins | Out-Null
    } 'profile build drift is rejected'

    $short = New-TestProfileResult `
        Soak short 599 -AllowInvalidDuration
    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            @(
                $baseline.ProfileResultPath,
                $fallback.ProfileResultPath,
                $short.ProfileResultPath) `
            $manual $status $docker $root `
            -AllowTestPath -Pins $pins | Out-Null
    } 'short Soak duration is rejected'

    Assert-Throws {
        $badDocker = $docker | Select-Object *
        $badDocker.RestartCount = 1
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $manual $status $badDocker `
            $root -AllowTestPath -Pins $pins | Out-Null
    } 'unstable final Docker state is rejected'

    $completion = $null
    Invoke-Check {
        $script:completion =
            Write-RebornPhase4CompletionReceipt `
                $profilePaths $manual $status $docker `
                $root -AllowTestPath -Pins $pins
        if ($completion.Record.result -cne 'Pass' -or
            $completion.Record.schemaVersion -ne 4 -or
            $completion.Record.mode -cne $pins.CompletionMode -or
            $completion.Record.generation -cne
                $pins.CampaignGeneration -or
            $completion.Record.pins.stockOriginSha256 -cne
                $pins.OriginSha256 -or
            $completion.Record.pins.candidateOriginSha256 -cne
                $pins.CandidateOriginSha256 -or
            $completion.Record.pins.nextManifestTrustSha256 -cne
                $pins.NextManifestTrustSha256 -or
            $completion.Record.pins.tlsTrustMode -cne
                $pins.ClientTlsTrustMode -or
            $completion.Record.campaign.id -cne
                $campaign.Record.campaignId -or
            @($completion.Record.profiles).Count -ne 3 -or
            (Get-Item $completion.Path).Length -gt 24KB) {
            throw 'The completion receipt is outside policy.'
        }
    } 'bounded checksummed completion receipt'

    Invoke-Check {
        Assert-RebornPhase4CompletionOriginPinTamperRejected `
            $completion.Path $pins {
                param($Path, $BoundPins)
                Read-RebornPhase4CompletionReceipt `
                    $Path -AllowTestPath -Pins $BoundPins
            }
    } 'both checksummed Origin pin changes are rejected'

    $previewReadyV5Pins =
        Get-RebornPhase4PreviewReadyV5SecureDockerPins
    Assert-Throws {
        Read-RebornPhase4CompletionReceipt `
            $completion.Path -AllowTestPath `
            -Pins $previewReadyV5Pins | Out-Null
    } 'PreviewReadyV5 pins reject a PreviewReadyV6 completion'

    Assert-Throws {
        Read-RebornPhase4CompletionReceipt `
            $completion.Path -AllowTestPath `
            -Pins (Get-RebornPhase4PreviewReadyV1SecureDockerPins) |
                Out-Null
    } 'PreviewReadyV1 pins reject a PreviewReadyV6 completion'

    $previewReadyV2Pins =
        Get-RebornPhase4PreviewReadyV2SecureDockerPins
    Assert-Throws {
        Read-RebornPhase4CompletionReceipt `
            $completion.Path -AllowTestPath `
            -Pins $previewReadyV2Pins | Out-Null
    } 'PreviewReadyV2 pins reject a PreviewReadyV6 completion'

    $previewReadyV3Pins =
        Get-RebornPhase4PreviewReadyV3SecureDockerPins
    Assert-Throws {
        Read-RebornPhase4CompletionReceipt `
            $completion.Path -AllowTestPath `
            -Pins $previewReadyV3Pins | Out-Null
    } 'PreviewReadyV3 pins reject a PreviewReadyV6 completion'

    Invoke-Check {
        $savedRoot = $script:root
        $savedPins = $script:pins
        $savedCampaign = $script:campaign
        try {
            $script:v3Root = Join-Path (
                [IO.Path]::GetTempPath()
            ) ('reborn-phase4-completion-test-' +
                [Guid]::NewGuid().ToString('N'))
            $script:root = $script:v3Root
            $script:pins = $previewReadyV3Pins
            [IO.Directory]::CreateDirectory($script:root) | Out-Null
            $v3CampaignRecord = New-RebornPhase4CampaignRecord `
                -BackupBaselineNames @() -Pins $script:pins
            $v3CampaignRecord.state = 'Restored'
            $v3CampaignRecord.trustState = 'Removed'
            $v3CampaignRecord.hostsState = 'Restored'
            $v3CampaignRecord.bundleState = 'Restored'
            $script:campaign = Write-RebornPhase4CampaignReceipt `
                $v3CampaignRecord $script:root `
                -AllowTestPath -Pins $script:pins
            $v3Baseline = New-TestProfileResult Baseline v3-baseline 30
            $v3Fallback = New-TestProfileResult Fallback v3-fallback 20
            $v3Soak = New-TestProfileResult Soak v3-soak 600
            $v3Status = $status | Select-Object *
            $v3Status.HandoffPath = $script:campaign.Path
            $v3Completion = Write-RebornPhase4CompletionReceipt `
                @(
                    $v3Baseline.ProfileResultPath,
                    $v3Fallback.ProfileResultPath,
                    $v3Soak.ProfileResultPath) `
                $manual $v3Status $docker $script:root `
                -AllowTestPath -Pins $script:pins
            $reopened = Read-RebornPhase4CompletionReceipt `
                $v3Completion.Path -AllowTestPath -Pins $script:pins
            $v6Accepted = $true
            try {
                Read-RebornPhase4CompletionReceipt `
                    $v3Completion.Path -AllowTestPath -Pins $savedPins |
                    Out-Null
            }
            catch {
                $v6Accepted = $false
            }
            if ($v6Accepted -or
                $reopened.Record.schemaVersion -ne 2 -or
                $reopened.Record.generation -cne 'PreviewReadyV3' -or
                $null -eq
                    $reopened.Record.pins.PSObject.Properties[
                        'originSha256'] -or
                $null -ne
                    $reopened.Record.pins.PSObject.Properties[
                        'stockOriginSha256'] -or
                $null -ne
                    $reopened.Record.pins.PSObject.Properties[
                        'candidateOriginSha256']) {
                throw 'PreviewReadyV3 completion compatibility changed.'
            }
        }
        finally {
            $script:root = $savedRoot
            $script:pins = $savedPins
            $script:campaign = $savedCampaign
        }
    } 'PreviewReadyV3 completion remains exact and rejects V6 pins'

    Invoke-Check {
        $savedRoot = $script:root
        $savedPins = $script:pins
        $savedCampaign = $script:campaign
        try {
            $script:v2Root = Join-Path (
                [IO.Path]::GetTempPath()
            ) ('reborn-phase4-completion-test-' +
                [Guid]::NewGuid().ToString('N'))
            $script:root = $script:v2Root
            $script:pins = $previewReadyV2Pins
            [IO.Directory]::CreateDirectory($script:root) | Out-Null
            $v2CampaignRecord = New-RebornPhase4CampaignRecord `
                -BackupBaselineNames @() -Pins $script:pins
            $v2CampaignRecord.state = 'Restored'
            $v2CampaignRecord.trustState = 'Removed'
            $v2CampaignRecord.hostsState = 'Restored'
            $v2CampaignRecord.bundleState = 'Restored'
            $script:campaign = Write-RebornPhase4CampaignReceipt `
                $v2CampaignRecord $script:root `
                -AllowTestPath -Pins $script:pins
            $v2Baseline = New-TestProfileResult Baseline v2-baseline 30
            $v2Fallback = New-TestProfileResult Fallback v2-fallback 20
            $v2Soak = New-TestProfileResult Soak v2-soak 600
            $v2Status = $status | Select-Object *
            $v2Status.HandoffPath = $script:campaign.Path
            $v2Completion = Write-RebornPhase4CompletionReceipt `
                @(
                    $v2Baseline.ProfileResultPath,
                    $v2Fallback.ProfileResultPath,
                    $v2Soak.ProfileResultPath) `
                $manual $v2Status $docker $script:root `
                -AllowTestPath -Pins $script:pins
            $reopened = Read-RebornPhase4CompletionReceipt `
                $v2Completion.Path -AllowTestPath -Pins $script:pins
            if ($reopened.Record.generation -cne 'PreviewReadyV2' -or
                $reopened.Record.mode -cne
                    'Phase4LoopbackAcceptanceCompletion.PreviewReadyV2') {
                throw 'PreviewReadyV2 completion compatibility changed.'
            }
        }
        finally {
            $script:root = $savedRoot
            $script:pins = $savedPins
            $script:campaign = $savedCampaign
        }
    } 'PreviewReadyV2 completion remains readable with historical pins'

    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $manual $status $docker `
            $root `
            -Pins (Get-RebornPhase4HistoricalSecureDockerPins) |
                Out-Null
    } 'historical production completion writes are read-only'

    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $manual $status $docker `
            $root `
            -Pins (Get-RebornPhase4PreviewReadyV1SecureDockerPins) |
                Out-Null
    } 'PreviewReadyV1 production completion writes are read-only'

    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $manual $status $docker `
            $root -Pins $previewReadyV2Pins | Out-Null
    } 'PreviewReadyV2 production completion writes are read-only'

    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $manual $status $docker `
            $root -Pins $previewReadyV5Pins | Out-Null
    } 'PreviewReadyV5 production completion writes are read-only'

    Assert-Throws {
        Write-RebornPhase4CompletionReceipt `
            $profilePaths $manual $status $docker `
            $root -AllowTestPath -Pins $pins | Out-Null
    } 'completion receipt is CreateNew'

    $baselineEvidence = (
        Read-RebornPhase4ProfileResult `
            $baseline.ProfileResultPath `
            -AllowTestPath -Pins $pins).Record.evidencePath
    $baselineBytes = [IO.File]::ReadAllBytes($baselineEvidence)
    try {
        [IO.File]::AppendAllText($baselineEvidence, 'tampered')
        Assert-Throws {
            Read-RebornPhase4CompletionReceipt `
                $completion.Path -AllowTestPath -Pins $pins | Out-Null
        } 'dependency evidence tampering is rejected'
    }
    finally {
        [IO.File]::WriteAllBytes($baselineEvidence, $baselineBytes)
        [Array]::Clear($baselineBytes, 0, $baselineBytes.Length)
    }

    [IO.File]::AppendAllText($completion.Path, 'tampered')
    Assert-Throws {
        Read-RebornPhase4CompletionReceipt `
            $completion.Path -AllowTestPath -Pins $pins | Out-Null
    } 'completion checksum tampering is rejected'

    if ($passed -ne $expectedChecks) {
        throw "Expected $expectedChecks checks, got $passed."
    }
    Write-Host "Phase 4 completion receipt checks passed: $passed"
}
finally {
    foreach ($cleanupRoot in @($root, $v3Root, $v2Root)) {
        if ([string]::IsNullOrWhiteSpace($cleanupRoot) -or
            -not (Test-Path -LiteralPath $cleanupRoot)) {
            continue
        }
        $resolved = [IO.Path]::GetFullPath($cleanupRoot).TrimEnd('\')
        $temp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith(
                $temp,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notmatch
                '^reborn-phase4-completion-test-[0-9a-f]{32}$') {
            throw 'Completion test cleanup escaped its temporary scope.'
        }
        Get-ChildItem -LiteralPath $resolved -Recurse -Force -File |
            ForEach-Object {
                if ($_.IsReadOnly) {
                    $_.IsReadOnly = $false
                }
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
