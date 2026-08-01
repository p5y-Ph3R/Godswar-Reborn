[CmdletBinding()]
param(
    [string]$EvidenceRoot,
    [string]$BaseEnvironmentFile,
    [string]$RedisEnvironmentFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-B20Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

. (Join-Path $PSScriptRoot 'B20RetirementEvidence.StrictJson.ps1')
Import-Module `
    (Join-Path $PSScriptRoot 'B20HDockerObservation.Integrity.psm1') `
    -Force
Import-Module `
    (Join-Path $PSScriptRoot 'B20HDockerObservation.Telemetry.psm1') `
    -Force
Import-Module `
    (Join-Path $PSScriptRoot 'B20HDockerObservation.Topology.psm1') `
    -Force

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($BaseEnvironmentFile)) {
    $BaseEnvironmentFile = Join-Path $repositoryRoot '.env'
}
if ([string]::IsNullOrWhiteSpace($RedisEnvironmentFile)) {
    $RedisEnvironmentFile = Join-Path `
        $repositoryRoot 'artifacts/redis-main-local/redis.local.env'
}
$baseEnvironmentPath = [IO.Path]::GetFullPath($BaseEnvironmentFile)
$redisEnvironmentPath = [IO.Path]::GetFullPath($RedisEnvironmentFile)
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repositoryRoot 'artifacts/b20h-observation'
}
$resolvedRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$pathRoot = [IO.Path]::GetPathRoot($resolvedRoot)
if ($resolvedRoot.Length -gt $pathRoot.Length) {
    $resolvedRoot = $resolvedRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}
$activePath = Join-Path $resolvedRoot 'active-observation.json'
$active = Read-B20BoundedJsonFile `
    $activePath 64KB 'Active observation record'
Assert-B20Condition (
    $active.schemaVersion -ceq 'reborn.b20h.active-observation.v2' -and
    $active.topologyKind -ceq 'redis-coordinated-single-worker') (
    'The active observation is not the Redis-coordinated v2 schema.')
$evidenceDirectory = [IO.Path]::GetFullPath(
    (Join-Path $resolvedRoot ([string]$active.evidenceDirectory)))
$comparison = if ($env:OS -eq 'Windows_NT') {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}
$rootPrefix = $resolvedRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
Assert-B20Condition `
    ($evidenceDirectory.StartsWith($rootPrefix, $comparison)) `
    'The active observation path escapes its evidence root.'
Assert-B20NoReparsePoints `
    $resolvedRoot $evidenceDirectory 'Observation directory'
$record = Read-B20BoundedJsonFile `
    (Join-Path $evidenceDirectory 'observation-start.json') `
    256KB `
    'Observation start record'
$validatedRecord = Assert-B20AlphaObservationRecord $record
$startedAt = $validatedRecord.StartedAt
$targetEnd = $validatedRecord.TargetEnd
$generatedAt = [DateTimeOffset]::UtcNow
$startEpoch = $startedAt.ToUnixTimeMilliseconds() / 1000d
$queryEndEpoch = $generatedAt.ToUnixTimeMilliseconds() / 1000d
Assert-B20Condition `
    ($queryEndEpoch -gt $startEpoch) `
    'The observation has not run long enough to export telemetry.'
$rangeSeconds = [long][Math]::Ceiling($queryEndEpoch - $startEpoch) + 300
Assert-B20Condition `
    ($rangeSeconds -le 1296000) `
    'The raw Prometheus query exceeds the 15-day retention bound.'

$upQuery = 'up{job="godswar-b20h"}'
$readyQuery =
    'godswar_legacy_persistence_observer_ready{job="godswar-b20h"}'
$legacyQuery = 'godswar_legacy_persistence_invocations_total' +
    '{job="godswar-b20h"}'
$processQuery =
    'godswar_server_operations_process_start_time_seconds' +
    '{job="godswar-b20h"}'
$coordinationReadyQuery =
    'godswar_server_operational_coordination' +
    '{job="godswar-b20h",operational_state="ready"}'
$coordinationRoutesQuery =
    'godswar_server_operational_coordination' +
    '{job="godswar-b20h",operational_state="routes"}'

$upSeries = @(Get-B20RawSeries `
    $upQuery $queryEndEpoch $rangeSeconds)
$readySeries = @(Get-B20RawSeries `
    $readyQuery $queryEndEpoch $rangeSeconds)
$legacySeries = @(Get-B20RawSeries `
    $legacyQuery $queryEndEpoch $rangeSeconds 128)
$processSeries = @(Get-B20RawSeries `
    $processQuery $queryEndEpoch $rangeSeconds)
$coordinationReadySeries = @(Get-B20RawSeries `
    $coordinationReadyQuery $queryEndEpoch $rangeSeconds)
$coordinationRoutesSeries = @(Get-B20RawSeries `
    $coordinationRoutesQuery $queryEndEpoch $rangeSeconds)
Assert-B20Condition `
    ($upSeries.Count -eq 1 -and
        $readySeries.Count -eq 1 -and
        $processSeries.Count -eq 1 -and
        $coordinationReadySeries.Count -eq 1 -and
        $coordinationRoutesSeries.Count -eq 1) `
    'One or more required B20H time series is missing.'
$up = @($upSeries[0].Points)
$ready = @($readySeries[0].Points)
$process = @($processSeries[0].Points)
$coordinationReady = @($coordinationReadySeries[0].Points)
$coordinationRoutes = @($coordinationRoutesSeries[0].Points)
$successfulUp = @(
    $up | Where-Object {
        Assert-B20Condition `
            ($_.Value -eq 0 -or $_.Value -eq 1) `
            'Prometheus up must be the exact integer 0 or 1.'
        $_.Value -eq 1
    }
)

$startMilliseconds = $startedAt.ToUnixTimeMilliseconds()
$targetMilliseconds = $targetEnd.ToUnixTimeMilliseconds()
$coverageStartPoint = @(
    $successfulUp | Where-Object {
        [long][Math]::Round($_.Timestamp * 1000d) -le $startMilliseconds
    } | Sort-Object Timestamp -Descending | Select-Object -First 1
)
Assert-B20Condition `
    ($coverageStartPoint.Count -eq 1) `
    'No successful scrape proves coverage at observation start.'
$coverageStartMs =
    [long][Math]::Round($coverageStartPoint[0].Timestamp * 1000d)

$postTargetPoints = @(
    $successfulUp | Where-Object {
        [long][Math]::Round($_.Timestamp * 1000d) -ge $targetMilliseconds
    } | Sort-Object Timestamp -Unique
)
$windowCovered = $postTargetPoints.Count -ge 2
$coverageEndPoint = if ($windowCovered) {
    $postTargetPoints[0]
}
else {
    @($successfulUp | Sort-Object Timestamp -Descending |
        Select-Object -First 1)[0]
}
$coverageEndMilliseconds =
    [long][Math]::Round($coverageEndPoint.Timestamp * 1000d)
$confirmationMs = if ($windowCovered) {
    [long][Math]::Round($postTargetPoints[1].Timestamp * 1000d)
}
else {
    $coverageEndMilliseconds
}
$analysisBoundaryMilliseconds = if ($windowCovered) {
    $confirmationMs
}
else {
    $generatedAt.ToUnixTimeMilliseconds()
}

$successfulSamples = [Collections.Generic.HashSet[long]]::new()
foreach ($point in $successfulUp) {
    $sample = [long][Math]::Round($point.Timestamp * 1000d)
    if ($sample -ge $coverageStartMs -and
        $sample -le $confirmationMs) {
        $null = $successfulSamples.Add($sample)
    }
}
$orderedSamples = @($successfulSamples | Sort-Object)
Assert-B20Condition `
    ($orderedSamples.Count -gt 0) `
    'No successful scrape exists inside the observation range.'
$maximumGapMilliseconds = 0L
for ($index = 1; $index -lt $orderedSamples.Count; $index++) {
    $maximumGapMilliseconds = [Math]::Max(
        $maximumGapMilliseconds,
        $orderedSamples[$index] - $orderedSamples[$index - 1])
}
$maximumGapMilliseconds = [Math]::Max(
    $maximumGapMilliseconds,
    $analysisBoundaryMilliseconds - $orderedSamples[-1])

$missingSamples = 0
$readySampleSet = Get-B20TimestampSet `
    $ready `
    $coverageStartMs $confirmationMs
$processSampleSet = Get-B20TimestampSet `
    $process `
    $coverageStartMs $confirmationMs
$coordinationReadySampleSet = Get-B20TimestampSet `
    $coordinationReady `
    $coverageStartMs $confirmationMs
$coordinationRoutesSampleSet = Get-B20TimestampSet `
    $coordinationRoutes `
    $coverageStartMs $confirmationMs
$missingSamples += Get-B20MissingSampleCount `
    $successfulSamples $readySampleSet
$missingSamples += Get-B20MissingSampleCount `
    $successfulSamples $processSampleSet
$missingSamples += Get-B20MissingSampleCount `
    $successfulSamples $coordinationReadySampleSet
$missingSamples += Get-B20MissingSampleCount `
    $successfulSamples $coordinationRoutesSampleSet

$collectorStates = @(
    'dropped_instruments',
    'dropped_series',
    'dropped_tags',
    'dropped_measurements',
    'truncated_snapshots'
)
$collectorMaximum = 0d
foreach ($state in $collectorStates) {
    $collectorQuery =
        'godswar_server_metrics_collector' +
        "{job=`"godswar-b20h`",state=`"$state`"}"
    $collectorSeries = @(Get-B20RawSeries `
        $collectorQuery $queryEndEpoch $rangeSeconds)
    Assert-B20Condition `
        ($collectorSeries.Count -eq 1) `
        "Collector evidence series '$state' is missing."
    $collector = @($collectorSeries[0].Points)
    $collectorValues = @(
        $collector | Where-Object {
            $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
            $timestamp -ge $coverageStartMs -and
                $timestamp -le $confirmationMs
        } | ForEach-Object { $_.Value }
    )
    Assert-B20Condition `
        ($collectorValues.Count -gt 0) `
        "Collector evidence series '$state' has no bounded samples."
    Assert-B20Condition `
        (@($collectorValues | Where-Object {
            $_ -lt 0 -or $_ -ne [Math]::Floor($_)
        }).Count -eq 0) `
        "Collector evidence series '$state' is not an integer counter."
    $collectorMaximum = [Math]::Max(
        $collectorMaximum,
        ($collectorValues | Measure-Object -Maximum).Maximum)
    $collectorSampleSet = Get-B20TimestampSet `
        $collector `
        $coverageStartMs $confirmationMs
    $missingSamples += Get-B20MissingSampleCount `
        $successfulSamples $collectorSampleSet
    if ($windowCovered -and
        -not $collectorSampleSet.Contains($confirmationMs)) {
        $missingSamples++
    }
}

$readyValues = [double[]]@(
    $ready | Where-Object {
        $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
        $timestamp -ge $coverageStartMs -and
            $timestamp -le $confirmationMs
    } | ForEach-Object { $_.Value }
)
$processValues = [double[]]@(
    $process | Where-Object {
        $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
        $timestamp -ge $coverageStartMs -and
            $timestamp -le $confirmationMs
    } | ForEach-Object { $_.Value }
)
$coordinationReadyValues = [double[]]@(
    $coordinationReady | Where-Object {
        $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
        $timestamp -ge $coverageStartMs -and
            $timestamp -le $confirmationMs
    } | ForEach-Object { $_.Value }
)
$coordinationRouteValues = [double[]]@(
    $coordinationRoutes | Where-Object {
        $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
        $timestamp -ge $coverageStartMs -and
            $timestamp -le $confirmationMs
    } | ForEach-Object { $_.Value }
)
Assert-B20Condition `
    ($readyValues.Count -gt 0 -and
        $processValues.Count -gt 0 -and
        $coordinationReadyValues.Count -gt 0 -and
        $coordinationRouteValues.Count -gt 0) `
    'The bounded analysis range contains no required samples.'
Assert-B20Condition `
    (@($readyValues | Where-Object {
        $_ -ne 0 -and $_ -ne 1
    }).Count -eq 0) `
    'Observer readiness contains a non-Boolean measurement.'
$observerReadyMinimum = [int](
    $readyValues | Measure-Object -Minimum
).Minimum
Assert-B20Condition (
    @($coordinationReadyValues | Where-Object {
        $_ -ne 0 -and $_ -ne 1
    }).Count -eq 0 -and
    @($coordinationRouteValues | Where-Object {
        $_ -lt 0 -or $_ -ne [Math]::Floor($_)
    }).Count -eq 0) (
    'Redis coordination returned an invalid measurement.')
$coordinationReadyMinimum = [int](
    $coordinationReadyValues | Measure-Object -Minimum).Minimum
$coordinationRouteMinimum = [int](
    $coordinationRouteValues | Measure-Object -Minimum).Minimum
$coordinationRouteMaximum = [int](
    $coordinationRouteValues | Measure-Object -Maximum).Maximum
$processChanges = Get-B20ChangeCount $processValues
$legacyEvidence = Get-B20LegacyEvidence $legacySeries `
    $coverageStartMs $confirmationMs $windowCovered
$legacyMaximum = $legacyEvidence.Maximum
$legacyResets = $legacyEvidence.Resets
$missingSamples += $legacyEvidence.MissingConfirmation
$legacyInvocationDelta = [long][Math]::Round($legacyMaximum)

$server = @(
    (Invoke-B20Command docker @('inspect', 'godswar-server') |
        ConvertFrom-Json)
)[0]
$prometheus = @((Invoke-B20Command docker @(
    'inspect', 'godswar-b20h-prometheus') | ConvertFrom-Json))[0]
$postgres = @((Invoke-B20Command docker @(
    'inspect', 'godswar-postgres') | ConvertFrom-Json))[0]
$redis = @((Invoke-B20Command docker @(
    'inspect', 'godswar-main-redis-coordination') | ConvertFrom-Json))[0]
$postgresData = @($postgres.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data' -and $_.Type -ceq 'volume'
})
$prometheusData = @($prometheus.Mounts | Where-Object {
    $_.Destination -ceq '/prometheus' -and $_.Type -ceq 'bind' -and $_.RW
})
$serverIdentityMatches =
    [string]$server.Id -ceq [string]$record.replica.serverContainerId -and
    [long]$server.RestartCount -eq [long]$record.replica.serverRestartCount
$revisionMatches =
    [string]$server.Config.Labels.'org.opencontainers.image.revision' -ceq
        [string]$record.approval.sourceCommit
$prometheusIdentityMatches =
    [string]$prometheus.Id -ceq [string]$record.replica.prometheusContainerId -and
    [string]$prometheus.Image -ceq [string]$record.replica.prometheusImageId -and
    [string]$prometheus.Config.Image -ceq [string]$record.monitoring.prometheusImage -and
    $prometheusData.Count -eq 1 -and
    [string]$prometheusData[0].Source -ceq [string]$record.replica.prometheusDataSource
$postgresVolumeMatches = $postgresData.Count -eq 1 -and
    [string]$postgresData[0].Name -ceq [string]$record.replica.postgresVolume
$artifactHashesMatch = Test-B20ObservationArtifactHashes `
    $record.monitoring.artifactSha256 $repositoryRoot
$inputHashesMatch = Test-B20ObservationInputHashes `
    $record.coordination.inputSha256 `
    $baseEnvironmentPath `
    $redisEnvironmentPath
$serverTopologyMatches = Test-B20ServerRedisTopology `
    $server $record.coordination $repositoryRoot
$redisIdentityMatches = Test-B20RedisIdentity `
    $redis $record.coordination $repositoryRoot
$identityReset = if ($serverIdentityMatches -and
    $prometheusIdentityMatches -and $redisIdentityMatches) { 0 } else { 1 }
$counterResetCount = $processChanges + $legacyResets + $identityReset

$alerts = (Get-B20PrometheusApi '/api/v1/alerts').data
$activeAlerts = @(
    $alerts.alerts | Where-Object {
        $_.state -in @('pending', 'firing')
    } | ForEach-Object {
        [string]$_.labels.alertname
    } | Sort-Object -Unique
)
$maximumGapSeconds = [long][Math]::Ceiling(
    $maximumGapMilliseconds / 1000d)
$telemetryPasses =
    $windowCovered -and
    $coverageStartMs -le $startMilliseconds -and
    $coverageEndMilliseconds -ge $targetMilliseconds -and
    $observerReadyMinimum -eq 1 -and
    $coordinationReadyMinimum -eq 1 -and
    $coordinationRouteMinimum -eq 23 -and
    $coordinationRouteMaximum -eq 23 -and
    $legacyInvocationDelta -eq 0 -and
    $counterResetCount -eq 0 -and
    $maximumGapMilliseconds -le 300000 -and
    $missingSamples -eq 0 -and
    $collectorMaximum -eq 0 -and
    $revisionMatches -and
    $artifactHashesMatch -and
    $inputHashesMatch -and
    $serverTopologyMatches -and
    $redisIdentityMatches -and
    $postgresVolumeMatches -and
    $activeAlerts.Count -eq 0

$summary = [ordered]@{
    schemaVersion = 'reborn.b20h.docker-telemetry.v2'
    topologyKind = 'redis-coordinated-single-worker'
    evidenceKind = 'local-alpha-rehearsal'
    eligibleForRetirementAuthorization = $false
    status = if ($telemetryPasses) {
        'telemetry_passed'
    }
    elseif ($windowCovered) {
        'telemetry_failed'
    }
    else {
        'in_progress'
    }
    generatedAtUtc = $generatedAt.UtcDateTime.ToString('O')
    sourceCommit = [string]$record.approval.sourceCommit
    replica = [ordered]@{
        name = [string]$record.replica.name
        coverageStartedAtUtc = Convert-B20EpochToUtc (
            $coverageStartMs / 1000d)
        coverageEndedAtUtc = Convert-B20EpochToUtc (
            $coverageEndMilliseconds / 1000d)
        confirmationScrapeAtUtc = Convert-B20EpochToUtc (
            $confirmationMs / 1000d)
        observerReadyMinimum = $observerReadyMinimum
        legacyInvocationDelta = $legacyInvocationDelta
        counterResetCount = $counterResetCount
        maximumScrapeGapSeconds = $maximumGapSeconds
    }
    coordination = [ordered]@{
        provider = 'Redis'
        environment = 'tempest-local'
        serverNodeId = 'tempest-openworld-01'
        readyMinimum = $coordinationReadyMinimum
        routeCountMinimum = $coordinationRouteMinimum
        routeCountMaximum = $coordinationRouteMaximum
        redisIdentityMatchesStart = $redisIdentityMatches
        redisHealthyAtExport =
            [string]$redis.State.Health.Status -ceq 'healthy'
    }
    window = [ordered]@{
        startedAtUtc = $startedAt.UtcDateTime.ToString('O')
        targetEndedAtUtc = $targetEnd.UtcDateTime.ToString('O')
        wallClockTargetReached = $generatedAt -ge $targetEnd
        telemetryCoverageComplete = $windowCovered
    }
    diagnostics = [ordered]@{
        processStartChanges = $processChanges
        legacyCounterDecreases = $legacyResets
        requiredSeriesMissingSamples = $missingSamples
        collectorMaximum = $collectorMaximum
        serverIdentityMatchesStart = $serverIdentityMatches
        prometheusIdentityMatchesStart = $prometheusIdentityMatches
        postgreSqlVolumeMatchesStart = $postgresVolumeMatches
        revisionMatchesApproval = $revisionMatches
        observationArtifactHashesMatch = $artifactHashesMatch
        composeInputHashesMatch = $inputHashesMatch
        serverRedisTopologyMatchesStart = $serverTopologyMatches
        activeAlerts = $activeAlerts
        finalRetirementAuthorized = $false
    }
}
$json = $summary | ConvertTo-Json -Depth 10
$timestamp = $generatedAt.UtcDateTime.ToString('yyyyMMddTHHmmssfffZ')
$immutablePath = Join-Path `
    $evidenceDirectory `
    ("telemetry-summary-$timestamp-" +
        "$([Guid]::NewGuid().ToString('N')).json")
$latestPath = Join-Path $evidenceDirectory 'telemetry-summary-latest.json'
$encoding = [Text.UTF8Encoding]::new($false)
$immutableStream = [IO.File]::Open(
    $immutablePath,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write,
    [IO.FileShare]::None)
try {
    $immutableWriter = [IO.StreamWriter]::new($immutableStream, $encoding)
    try {
        $immutableWriter.Write($json + "`n")
        $immutableWriter.Flush()
    }
    finally {
        $immutableWriter.Dispose()
    }
}
finally {
    $immutableStream.Dispose()
}
Set-B20AtomicLatestText $latestPath ($json + "`n")
$json
