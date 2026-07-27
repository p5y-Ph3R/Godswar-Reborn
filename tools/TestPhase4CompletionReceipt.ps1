$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($moduleName in @(
    'Phase4SecureDockerClientCampaign.psm1',
    'Phase4LoopbackAcceptanceProfile.psm1',
    'ControlledHostPrivacyEvidence.psm1',
    'Phase4CompletionValidation.psm1',
    'Phase4CompletionReceipt.psm1'
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
$expectedChecks = 11

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
    $campaignRecord.trustState = 'Removed'
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
            $completion.Record.schemaVersion -ne 2 -or
            $completion.Record.mode -cne $pins.CompletionMode -or
            $completion.Record.generation -cne
                $pins.CampaignGeneration -or
            $completion.Record.pins.nextManifestTrustSha256 -cne
                $pins.NextManifestTrustSha256 -or
            $completion.Record.campaign.id -cne
                $campaign.Record.campaignId -or
            @($completion.Record.profiles).Count -ne 3 -or
            (Get-Item $completion.Path).Length -gt 24KB) {
            throw 'The completion receipt is outside policy.'
        }
    } 'bounded checksummed completion receipt'

    Assert-Throws {
        Read-RebornPhase4CompletionReceipt `
            $completion.Path -AllowTestPath `
            -Pins (Get-RebornPhase4HistoricalSecureDockerPins) |
                Out-Null
    } 'historical pins reject a PreviewReadyV1 completion'

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
    if (Test-Path -LiteralPath $root) {
        $resolved = [IO.Path]::GetFullPath($root).TrimEnd('\')
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
