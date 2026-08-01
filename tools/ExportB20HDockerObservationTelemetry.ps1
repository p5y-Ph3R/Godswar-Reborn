[CmdletBinding()]
param(
    [string]$EvidenceRoot
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

function Read-B20JsonFile {
    param(
        [string]$LiteralPath,
        [long]$MaximumBytes,
        [string]$Context
    )

    Assert-B20Condition `
        (Test-Path -LiteralPath $LiteralPath -PathType Leaf) `
        "$Context does not exist."
    $directory = Split-Path -Parent $LiteralPath
    Assert-B20NoReparsePoints $directory $LiteralPath $Context
    $item = Get-Item -LiteralPath $LiteralPath
    Assert-B20Condition `
        ($item.Length -le $MaximumBytes) `
        "$Context exceeds its bounded size."
    $raw = Get-Content -LiteralPath $LiteralPath -Raw
    Assert-B20NoDuplicateJsonProperties $raw
    return $raw | ConvertFrom-Json
}

function Get-B20PrometheusApi {
    param([Parameter(Mandatory)][string]$Path)

    $raw = Invoke-B20Command docker @(
        'exec',
        'godswar-b20h-prometheus',
        'wget',
        '-T', '10',
        '-t', '1',
        '-qO-',
        "http://127.0.0.1:9091$Path")
    Assert-B20Condition `
        ($raw.Length -le 16MB) `
        'Prometheus response exceeds the bounded export size.'
    Assert-B20NoDuplicateJsonProperties $raw
    $response = $raw | ConvertFrom-Json
    Assert-B20Condition `
        ($response.status -ceq 'success') `
        "Prometheus rejected '$Path'."
    return $response
}

function Get-B20RawSeries {
    param(
        [string]$Selector,
        [double]$EndEpoch,
        [long]$RangeSeconds,
        [int]$MaximumSeries = 1
    )

    $culture = [Globalization.CultureInfo]::InvariantCulture
    $query = "${Selector}[$($RangeSeconds)s]"
    $encoded = [Uri]::EscapeDataString($query)
    $end = $EndEpoch.ToString('0.###', $culture)
    $path = '/api/v1/query?query={0}&time={1}' -f
        $encoded,
        $end
    $response = Get-B20PrometheusApi $path
    Assert-B20Condition `
        ($response.data.resultType -ceq 'matrix') `
        "Prometheus query '$query' returned the wrong result type."
    $result = @($response.data.result)
    if ($result.Count -eq 0) {
        return @()
    }
    Assert-B20Condition `
        ($result.Count -le $MaximumSeries) `
        "Prometheus query '$query' exceeded the series bound."
    return @(
        $result | ForEach-Object {
            $values = @($_.values)
            Assert-B20Condition `
                ($values.Count -le 100000) `
                "Prometheus query '$query' exceeded the sample bound."
            [pscustomobject]@{
                Metric = $_.metric
                Points = @(
                    $values | ForEach-Object {
                        Assert-B20Condition `
                            (@($_).Count -eq 2) `
                            "Prometheus query '$query' returned a malformed sample."
                        $timestamp = [double]::Parse(
                            [string]$_[0],
                            $culture)
                        $value = [double]::Parse(
                            [string]$_[1],
                            $culture)
                        Assert-B20Condition `
                            (-not [double]::IsNaN($timestamp) -and
                                -not [double]::IsInfinity($timestamp) -and
                                -not [double]::IsNaN($value) -and
                                -not [double]::IsInfinity($value)) `
                            "Prometheus query '$query' returned a non-finite sample."
                        [pscustomobject]@{
                            Timestamp = $timestamp
                            Value = $value
                        }
                    }
                )
            }
        }
    )
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
$active = Read-B20JsonFile $activePath 64KB 'Active observation record'
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
$record = Read-B20JsonFile `
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

$upSeries = @(Get-B20RawSeries `
    $upQuery $queryEndEpoch $rangeSeconds)
$readySeries = @(Get-B20RawSeries `
    $readyQuery $queryEndEpoch $rangeSeconds)
$legacySeries = @(Get-B20RawSeries `
    $legacyQuery $queryEndEpoch $rangeSeconds 128)
$processSeries = @(Get-B20RawSeries `
    $processQuery $queryEndEpoch $rangeSeconds)
Assert-B20Condition `
    ($upSeries.Count -eq 1 -and
        $readySeries.Count -eq 1 -and
        $processSeries.Count -eq 1) `
    'One or more required B20H time series is missing.'
$up = @($upSeries[0].Points)
$ready = @($readySeries[0].Points)
$process = @($processSeries[0].Points)
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
$coverageStartMilliseconds =
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
$confirmationEndMilliseconds = if ($windowCovered) {
    [long][Math]::Round($postTargetPoints[1].Timestamp * 1000d)
}
else {
    $coverageEndMilliseconds
}
$analysisBoundaryMilliseconds = if ($windowCovered) {
    $coverageEndMilliseconds
}
else {
    $generatedAt.ToUnixTimeMilliseconds()
}

$successfulSamples = [Collections.Generic.HashSet[long]]::new()
foreach ($point in $successfulUp) {
    $sample = [long][Math]::Round($point.Timestamp * 1000d)
    if ($sample -ge $coverageStartMilliseconds -and
        $sample -le $coverageEndMilliseconds) {
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

$requiredSeriesMissingSamples = 0
$readySampleSet = Get-B20TimestampSet `
    $ready `
    $coverageStartMilliseconds $coverageEndMilliseconds
$processSampleSet = Get-B20TimestampSet `
    $process `
    $coverageStartMilliseconds $coverageEndMilliseconds
$requiredSeriesMissingSamples += Get-B20MissingSampleCount `
    $successfulSamples $readySampleSet
$requiredSeriesMissingSamples += Get-B20MissingSampleCount `
    $successfulSamples $processSampleSet

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
            $timestamp -ge $coverageStartMilliseconds -and
                $timestamp -le $confirmationEndMilliseconds
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
        $coverageStartMilliseconds $coverageEndMilliseconds
    $requiredSeriesMissingSamples += Get-B20MissingSampleCount `
        $successfulSamples $collectorSampleSet
}

$readyValues = [double[]]@(
    $ready | Where-Object {
        $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
        $timestamp -ge $coverageStartMilliseconds -and
            $timestamp -le $coverageEndMilliseconds
    } | ForEach-Object { $_.Value }
)
$processValues = [double[]]@(
    $process | Where-Object {
        $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
        $timestamp -ge $coverageStartMilliseconds -and
            $timestamp -le $coverageEndMilliseconds
    } | ForEach-Object { $_.Value }
)
Assert-B20Condition `
    ($readyValues.Count -gt 0 -and
        $processValues.Count -gt 0) `
    'The bounded analysis range contains no required samples.'
Assert-B20Condition `
    (@($readyValues | Where-Object {
        $_ -ne 0 -and $_ -ne 1
    }).Count -eq 0) `
    'Observer readiness contains a non-Boolean measurement.'
$observerReadyMinimum = [int](
    $readyValues | Measure-Object -Minimum
).Minimum
$processChanges = Get-B20ChangeCount $processValues
$legacyMaximum = 0d
$legacyResets = 0
foreach ($series in $legacySeries) {
    $values = [double[]]@(
        $series.Points | Where-Object {
            $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
            $timestamp -ge $coverageStartMilliseconds -and
                $timestamp -le $coverageEndMilliseconds
        } | ForEach-Object { $_.Value }
    )
    if ($values.Count -eq 0) {
        continue
    }
    Assert-B20Condition `
        (@($values | Where-Object {
            $_ -lt 0 -or $_ -ne [Math]::Floor($_)
        }).Count -eq 0) `
        'A legacy invocation series is not an integer counter.'
    $legacyMaximum += ($values | Measure-Object -Maximum).Maximum
    $legacyResets += Get-B20DecreaseCount $values
}
$legacyInvocationDelta = [long][Math]::Round($legacyMaximum)

$server = @(
    (Invoke-B20Command docker @('inspect', 'godswar-server') |
        ConvertFrom-Json)
)[0]
$prometheus = @((Invoke-B20Command docker @(
    'inspect', 'godswar-b20h-prometheus') | ConvertFrom-Json))[0]
$postgres = @((Invoke-B20Command docker @(
    'inspect', 'godswar-postgres') | ConvertFrom-Json))[0]
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
$identityReset = if ($serverIdentityMatches -and
    $prometheusIdentityMatches) { 0 } else { 1 }
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
    $coverageStartMilliseconds -le $startMilliseconds -and
    $coverageEndMilliseconds -ge $targetMilliseconds -and
    $observerReadyMinimum -eq 1 -and
    $legacyInvocationDelta -eq 0 -and
    $counterResetCount -eq 0 -and
    $maximumGapMilliseconds -le 300000 -and
    $requiredSeriesMissingSamples -eq 0 -and
    $collectorMaximum -eq 0 -and
    $revisionMatches -and
    $artifactHashesMatch -and
    $postgresVolumeMatches -and
    $activeAlerts.Count -eq 0

$summary = [ordered]@{
    schemaVersion = 'reborn.b20h.docker-telemetry.v1'
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
            $coverageStartMilliseconds / 1000d)
        coverageEndedAtUtc = Convert-B20EpochToUtc (
            $coverageEndMilliseconds / 1000d)
        confirmationScrapeAtUtc = Convert-B20EpochToUtc (
            $confirmationEndMilliseconds / 1000d)
        observerReadyMinimum = $observerReadyMinimum
        legacyInvocationDelta = $legacyInvocationDelta
        counterResetCount = $counterResetCount
        maximumScrapeGapSeconds = $maximumGapSeconds
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
        requiredSeriesMissingSamples = $requiredSeriesMissingSamples
        collectorMaximum = $collectorMaximum
        serverIdentityMatchesStart = $serverIdentityMatches
        prometheusIdentityMatchesStart = $prometheusIdentityMatches
        postgreSqlVolumeMatchesStart = $postgresVolumeMatches
        revisionMatchesApproval = $revisionMatches
        observationArtifactHashesMatch = $artifactHashesMatch
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
$temporaryPath = "$latestPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($temporaryPath, $json + "`n", $encoding)
    if (Test-Path -LiteralPath $latestPath -PathType Leaf) {
        [IO.File]::Replace($temporaryPath, $latestPath, $null)
    }
    else {
        [IO.File]::Move($temporaryPath, $latestPath)
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
$json
