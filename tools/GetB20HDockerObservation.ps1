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
        ($raw.Length -le 1MB) `
        'Prometheus response exceeds the bounded status size.'
    Assert-B20NoDuplicateJsonProperties $raw
    $response = $raw | ConvertFrom-Json
    Assert-B20Condition `
        ($response.status -ceq 'success') `
        "Prometheus rejected '$Path'."
    return $response.data
}

function Get-B20CurrentValue {
    param([Parameter(Mandatory)][string]$Query)

    $encoded = [Uri]::EscapeDataString($Query)
    $data = Get-B20PrometheusApi "/api/v1/query?query=$encoded"
    Assert-B20Condition `
        ($data.resultType -ceq 'vector') `
        "Query '$Query' returned the wrong result type."
    $result = @($data.result)
    if ($result.Count -ne 1 -or @($result[0].value).Count -ne 2) {
        throw "Query '$Query' did not return exactly one current series."
    }
    $value = [double]::Parse(
        [string]$result[0].value[1],
        [Globalization.CultureInfo]::InvariantCulture)
    Assert-B20Condition `
        (-not [double]::IsNaN($value) -and
            -not [double]::IsInfinity($value)) `
        "Query '$Query' returned a non-finite value."
    return $value
}

function Read-B20JsonFile {
    param([string]$LiteralPath, [long]$MaximumBytes, [string]$Context)

    Assert-B20Condition `
        (Test-Path -LiteralPath $LiteralPath -PathType Leaf) `
        "$Context does not exist."
    $directory = Split-Path -Parent $LiteralPath
    Assert-B20NoReparsePoints $directory $LiteralPath $Context
    Assert-B20Condition `
        ((Get-Item -LiteralPath $LiteralPath).Length -le $MaximumBytes) `
        "$Context exceeds its bounded size."
    $raw = Get-Content -LiteralPath $LiteralPath -Raw
    Assert-B20NoDuplicateJsonProperties $raw
    return $raw | ConvertFrom-Json
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
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
if (-not (Test-Path -LiteralPath $activePath -PathType Leaf)) {
    throw 'No active B20H Docker observation is recorded.'
}
$active = Read-B20JsonFile $activePath 64KB 'Active observation record'
Assert-B20Condition (
    $active.schemaVersion -ceq 'reborn.b20h.active-observation.v2' -and
    $active.topologyKind -ceq 'redis-coordinated-single-worker') (
    'The active observation is not the Redis-coordinated v2 schema.')
$evidenceDirectory = [IO.Path]::GetFullPath(
    (Join-Path $resolvedRoot ([string]$active.evidenceDirectory)))
$rootPrefix = $resolvedRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
$comparison = if ($env:OS -eq 'Windows_NT') {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}
if (-not $evidenceDirectory.StartsWith($rootPrefix, $comparison)) {
    throw 'The active observation path escapes its evidence root.'
}
Assert-B20NoReparsePoints `
    $resolvedRoot $evidenceDirectory 'Observation directory'
$startPath = Join-Path $evidenceDirectory 'observation-start.json'
$record = Read-B20JsonFile $startPath 256KB 'Observation start record'
$validatedRecord = Assert-B20AlphaObservationRecord $record
$startedAt = $validatedRecord.StartedAt
$targetEnd = $validatedRecord.TargetEnd
$now = [DateTimeOffset]::UtcNow

$up = Get-B20CurrentValue 'up{job="godswar-b20h"}'
$ready = Get-B20CurrentValue (
    'godswar_legacy_persistence_observer_ready{job="godswar-b20h"}')
$legacy = Get-B20CurrentValue (
    'sum(godswar_legacy_persistence_invocations_total' +
    '{job="godswar-b20h"}) or vector(0)')
$processStart = Get-B20CurrentValue (
    'godswar_server_operations_process_start_time_seconds' +
    '{job="godswar-b20h"}')
$coordinationReady = Get-B20CurrentValue (
    'godswar_server_operational_coordination' +
    '{job="godswar-b20h",operational_state="ready"}')
$coordinationRoutes = Get-B20CurrentValue (
    'godswar_server_operational_coordination' +
    '{job="godswar-b20h",operational_state="routes"}')
$alerts = Get-B20PrometheusApi '/api/v1/alerts'
$activeAlerts = @(
    $alerts.alerts | Where-Object {
        $_.state -in @('pending', 'firing')
    } | ForEach-Object {
        [string]$_.labels.alertname
    } | Sort-Object -Unique
)

$server = @(
    (Invoke-B20Command docker @('inspect', 'godswar-server') |
        ConvertFrom-Json)
)[0]
$prometheus = @(
    (Invoke-B20Command docker @(
        'inspect', 'godswar-b20h-prometheus') | ConvertFrom-Json)
)[0]
$postgres = @(
    (Invoke-B20Command docker @('inspect', 'godswar-postgres') |
        ConvertFrom-Json)
)[0]
$redis = @(
    (Invoke-B20Command docker @(
        'inspect', 'godswar-main-redis-coordination') | ConvertFrom-Json)
)[0]
$postgresData = @($postgres.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data' -and $_.Type -ceq 'volume'
})
$prometheusData = @($prometheus.Mounts | Where-Object {
    $_.Destination -ceq '/prometheus' -and $_.Type -ceq 'bind' -and $_.RW
})
$revision = [string]$server.Config.Labels.
    'org.opencontainers.image.revision'
$revisionMatches = $revision -ceq [string]$record.approval.sourceCommit
$serverIdentityMatches =
    [string]$server.Id -ceq [string]$record.replica.serverContainerId -and
    [long]$server.RestartCount -eq [long]$record.replica.serverRestartCount
$prometheusIdentityMatches =
    [string]$prometheus.Id -ceq
        [string]$record.replica.prometheusContainerId -and
    [string]$prometheus.Image -ceq
        [string]$record.replica.prometheusImageId -and
    [string]$prometheus.Config.Image -ceq
        [string]$record.monitoring.prometheusImage -and
    $prometheusData.Count -eq 1 -and
    [string]$prometheusData[0].Source -ceq
        [string]$record.replica.prometheusDataSource
$postgresVolumeMatches =
    $postgresData.Count -eq 1 -and
    [string]$postgresData[0].Name -ceq
        [string]$record.replica.postgresVolume
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
$healthy =
    $up -eq 1 -and
    $ready -eq 1 -and
    $coordinationReady -eq 1 -and
    $coordinationRoutes -eq 23 -and
    $legacy -eq 0 -and
    $activeAlerts.Count -eq 0 -and
    $revisionMatches -and
    $serverIdentityMatches -and
    $prometheusIdentityMatches -and
    $postgresVolumeMatches -and
    $artifactHashesMatch -and
    $inputHashesMatch -and
    $serverTopologyMatches -and
    $redisIdentityMatches -and
    $server.State.Health.Status -ceq 'healthy' -and
    $prometheus.State.Health.Status -ceq 'healthy' -and
    $postgres.State.Health.Status -ceq 'healthy'

[pscustomobject]@{
    CurrentStatus = if ($healthy) {
        'current_healthy'
    } else {
        'current_attention_required'
    }
    StartedAtUtc = $startedAt.UtcDateTime.ToString('O')
    TargetEndedAtUtc = $targetEnd.UtcDateTime.ToString('O')
    ElapsedHours = [Math]::Round(($now - $startedAt).TotalHours, 3)
    RemainingHours = [Math]::Round(
        [Math]::Max(0, ($targetEnd - $now).TotalHours),
        3)
    WindowTimeComplete = $now -ge $targetEnd
    TargetUp = [int]$up
    ObserverReady = [int]$ready
    LegacyInvocationTotal = [long]$legacy
    ProcessStartUnixSeconds = $processStart
    RedisCoordinationReady = [int]$coordinationReady
    RedisCoordinationRoutes = [int]$coordinationRoutes
    RevisionMatchesApproval = $revisionMatches
    ServerIdentityMatchesStart = $serverIdentityMatches
    PrometheusIdentityMatchesStart = $prometheusIdentityMatches
    PostgreSqlVolumeMatchesStart = $postgresVolumeMatches
    ObservationArtifactHashesMatch = $artifactHashesMatch
    ComposeInputHashesMatch = $inputHashesMatch
    ServerRedisTopologyMatchesStart = $serverTopologyMatches
    RedisIdentityMatchesStart = $redisIdentityMatches
    RedisHealth = [string]$redis.State.Health.Status
    ActiveAlerts = $activeAlerts
    FullWindowValidity = 'not_evaluated_use_export_command'
    EvidenceDirectory = $evidenceDirectory
    EvidenceKind = 'local-alpha-rehearsal'
    TopologyKind = 'redis-coordinated-single-worker'
    CoordinationProvider = 'Redis'
    EligibleForRetirementAuthorization = $false
    FinalRetirementAuthorized = $false
} | ConvertTo-Json -Depth 5
