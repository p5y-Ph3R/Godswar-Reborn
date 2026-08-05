[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$ChangeId = 'alpha-b20h-20260801',

    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$ApprovedByRole = 'project-owner',

    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$ReplicaName = 'tempest-world-01',

    [string]$EvidenceRoot,

    [string]$BaseEnvironmentFile,

    [string]$RedisEnvironmentFile,

    [switch]$UsePrebuiltServerImage,

    [switch]$AllowMutation
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

function Write-B20Json {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [object]$Value,

        [switch]$CreateNew
    )

    $json = $Value | ConvertTo-Json -Depth 12
    $text = $json + [Environment]::NewLine
    $encoding = [Text.UTF8Encoding]::new($false)
    if ($CreateNew) {
        $stream = [IO.File]::Open(
            $LiteralPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $writer = [IO.StreamWriter]::new($stream, $encoding)
            try {
                $writer.Write($text)
                $writer.Flush()
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
        return
    }
    [IO.File]::WriteAllText($LiteralPath, $text, $encoding)
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
foreach ($environmentPath in @(
    $baseEnvironmentPath,
    $redisEnvironmentPath)) {
    Assert-B20Condition `
        (Test-Path -LiteralPath $environmentPath -PathType Leaf) `
        "Required Compose environment file is missing: $environmentPath"
}
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
$existingRoot = $resolvedRoot
while (-not (Test-Path -LiteralPath $existingRoot)) {
    $existingRoot = Split-Path -Parent $existingRoot
    Assert-B20Condition `
        (-not [string]::IsNullOrWhiteSpace($existingRoot)) `
        'The evidence root has no existing filesystem ancestor.'
}
Assert-B20NoReparsePoints `
    $pathRoot $existingRoot 'Evidence root'
$activePath = Join-Path $resolvedRoot 'active-observation.json'

if (-not $AllowMutation) {
    throw (
        'Starting B20H rebuilds/recreates the game-server container. ' +
        'Pass -AllowMutation after confirming the local alpha interruption.')
}

if ($ReplicaName -cne 'tempest-world-01') {
    throw (
        'The checked-in single-replica Prometheus configuration is bound ' +
        "to tempest-world-01, not '$ReplicaName'.")
}

& (Join-Path $PSScriptRoot 'TestRedisMainComposeProfile.ps1') `
    -EnvironmentFile $redisEnvironmentPath `
    -BaseEnvironmentFile $baseEnvironmentPath `
    -RequireLivePostgres | Out-Null
$inputSha256 = Get-B20ObservationInputHashes `
    $baseEnvironmentPath $redisEnvironmentPath
$composeArguments = @(
    'compose',
    '--project-name', 'reborn',
    '--env-file', $baseEnvironmentPath,
    '--env-file', $redisEnvironmentPath,
    '-f', (Join-Path $repositoryRoot 'docker-compose.yml'),
    '-f', (Join-Path $repositoryRoot 'docker-compose.redis.yml'),
    '--profile', 'redis-coordinated',
    '--profile', 'b20h-observation'
)
$renderedCompose = Invoke-B20Command docker ($composeArguments + @(
    'config', '--format', 'json')) | ConvertFrom-Json
$renderedPostgres = $renderedCompose.services.postgres
Assert-B20Condition (
    Test-B20RenderedObservationTopology `
        $renderedCompose $redisEnvironmentPath) (
    'Rendered Compose does not select the exact Redis-coordinated topology.')

$null = New-Item -ItemType Directory -Path $resolvedRoot -Force
Assert-B20NoReparsePoints `
    $resolvedRoot $resolvedRoot 'Evidence root'
$campaignLock = [IO.File]::Open(
    (Join-Path $resolvedRoot '.campaign.lock'),
    [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None)
$savedCommit = $env:GODSWAR_SOURCE_COMMIT
$savedEvidence = $env:GODSWAR_B20H_EVIDENCE_DIRECTORY
try {

$gitStatus = Invoke-B20Command git @(
    '-C', $repositoryRoot, 'status', '--porcelain')
if (-not [string]::IsNullOrWhiteSpace($gitStatus)) {
    throw 'Commit or remove repository changes before starting B20H.'
}
$sourceCommit = (
    Invoke-B20Command git @('-C', $repositoryRoot, 'rev-parse', 'HEAD')
).Trim()
if ($sourceCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'The source revision is not an exact 40-character Git commit.'
}

if (Test-Path -LiteralPath $activePath) {
    throw (
        'An active B20H record already exists. Inspect it with ' +
        'tools/GetB20HDockerObservation.ps1; never silently replace a window.')
}

if ($UsePrebuiltServerImage) {
    $prebuiltImage = @(
        (Invoke-B20Command docker @(
            'image', 'inspect', 'reborn-server:latest')) |
            ConvertFrom-Json
    )[0]
    $prebuiltRevision = [string]$prebuiltImage.Config.Labels.
        'org.opencontainers.image.revision'
    Assert-B20Condition ($prebuiltRevision -ceq $sourceCommit) (
        'The prebuilt reborn-server image does not carry the exact ' +
        'approved Git revision.')
}

$approvedAt = [DateTimeOffset]::UtcNow
$runId = '{0}-{1}' -f
    $approvedAt.UtcDateTime.ToString('yyyyMMddTHHmmssZ'),
    $sourceCommit.Substring(0, 12)
$evidenceDirectory = Join-Path $resolvedRoot $runId
if (Test-Path -LiteralPath $evidenceDirectory) {
    throw "Evidence directory already exists: $evidenceDirectory"
}
$null = New-Item -ItemType Directory -Path $evidenceDirectory
$prometheusDirectory = Join-Path $evidenceDirectory 'prometheus'
$null = New-Item `
    -ItemType Directory `
    -Path $prometheusDirectory
Assert-B20NoReparsePoints `
    $resolvedRoot $prometheusDirectory 'Prometheus evidence directory'
Invoke-B20Command docker @(
    'run', '--rm', '--network', 'none', '--read-only',
    '--user', '0:0', '--cap-drop', 'ALL', '--cap-add', 'CHOWN',
    '--cap-add', 'FOWNER', '--security-opt', 'no-new-privileges',
    '--entrypoint=/bin/sh',
    '--volume', "${prometheusDirectory}:/prometheus",
    ('prom/prometheus:v3.13.1@sha256:' +
        '3c42b892cf723fa54d2f262c37a0e1f80aa8c8ddb1da7b9b0df9455a35a7f893'),
    '-c', 'chown 65534:65534 /prometheus && chmod 0700 /prometheus') |
    Out-Null

$approval = [ordered]@{
    schemaVersion = 'reborn.b20h.docker-approval.v1'
    approvalKind = 'local-alpha-rehearsal'
    eligibleForRetirementAuthorization = $false
    approved = $true
    changeId = $ChangeId
    approvedByRole = $ApprovedByRole
    approvedAtUtc = $approvedAt.UtcDateTime.ToString('O')
    approvedMinimumHours = 168
    expectedReplicaCount = 1
    replicaName = $ReplicaName
    sourceCommit = $sourceCommit
    serverImageMode = if ($UsePrebuiltServerImage) {
        'verified-prebuilt-local'
    }
    else {
        'compose-build'
    }
}
Write-B20Json `
    -LiteralPath (Join-Path $evidenceDirectory 'approval.json') `
    -Value $approval `
    -CreateNew

$artifactSha256 = Get-B20ObservationArtifactHashes $repositoryRoot

    $env:GODSWAR_SOURCE_COMMIT = $sourceCommit
    $env:GODSWAR_B20H_EVIDENCE_DIRECTORY = $evidenceDirectory

    $expectedPostgresVolume = 'reborn_godswar-postgres-data'
    $null = Invoke-B20Command docker @(
        'volume', 'inspect', $expectedPostgresVolume)
    $preflightPostgres = @(
        (Invoke-B20Command docker @('inspect', 'godswar-postgres') |
            ConvertFrom-Json)
    )[0]
    Assert-B20Condition (
        $preflightPostgres.State.Health.Status -ceq 'healthy' -and
        (Test-B20PostgresEnvironment `
            $preflightPostgres $renderedPostgres) -and
        @($preflightPostgres.Mounts | Where-Object {
            $_.Destination -ceq '/var/lib/postgresql/data' -and
            $_.Type -ceq 'volume' -and
            $_.Name -ceq $expectedPostgresVolume
        }).Count -eq 1) (
        'The authoritative reborn PostgreSQL volume is not healthy and mounted.')

    Invoke-B20Command docker ($composeArguments + @(
        'stop', 'b20h-prometheus')) | Out-Null
    Invoke-B20Command docker ($composeArguments + @(
        'rm', '--force', 'b20h-prometheus')) | Out-Null
    Invoke-B20Command docker ($composeArguments + @(
        'up', '--detach', '--wait', '--wait-timeout', '120',
        '--force-recreate', 'redis-coordination')) | Write-Host
    $serverUpArguments = @(
        'up', '--detach', '--wait', '--wait-timeout', '300',
        '--no-deps', '--force-recreate')
    if ($UsePrebuiltServerImage) {
        $serverUpArguments += @('--no-build', '--pull', 'never')
    }
    else {
        $serverUpArguments += '--build'
    }
    $serverUpArguments += 'server'
    Invoke-B20Command docker (
        $composeArguments + $serverUpArguments) | Write-Host
    Invoke-B20Command docker ($composeArguments + @(
        'up', '--detach', '--wait', '--wait-timeout', '180',
        '--no-deps', '--force-recreate', 'b20h-prometheus')) | Write-Host

    $serverInspect = @(
        (Invoke-B20Command docker @('inspect', 'godswar-server') |
            ConvertFrom-Json)
    )[0]
    $postgresInspect = @(
        (Invoke-B20Command docker @('inspect', 'godswar-postgres') |
            ConvertFrom-Json)
    )[0]
    $prometheusInspect = @(
        (Invoke-B20Command docker @(
            'inspect', 'godswar-b20h-prometheus') | ConvertFrom-Json)
    )[0]
    $redisInspect = @(
        (Invoke-B20Command docker @(
            'inspect', 'godswar-main-redis-coordination') |
            ConvertFrom-Json)
    )[0]
    $prometheusData = @($prometheusInspect.Mounts | Where-Object {
        $_.Destination -ceq '/prometheus' -and
        $_.Type -ceq 'bind' -and $_.RW
    })
    $pathComparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if ($prometheusData.Count -ne 1 -or
        -not [IO.Path]::GetFullPath([string]$prometheusData[0].Source).Equals(
            [IO.Path]::GetFullPath($prometheusDirectory),
            $pathComparison)) {
        throw 'Prometheus is not writing to the approved evidence directory.'
    }

    $imageRevision = [string]$serverInspect.Config.Labels.
        'org.opencontainers.image.revision'
    $containerRevision = [string]$serverInspect.Config.Labels.
        'com.reborn.source.commit'
    if ($imageRevision -cne $sourceCommit -or
        $containerRevision -cne $sourceCommit) {
        throw 'The deployed server does not carry the approved Git revision.'
    }
    if ($serverInspect.State.Health.Status -cne 'healthy' -or
        $postgresInspect.State.Health.Status -cne 'healthy' -or
        $prometheusInspect.State.Health.Status -cne 'healthy' -or
        $redisInspect.State.Health.Status -cne 'healthy') {
        throw 'Every B20H Docker component must be healthy before T0.'
    }

    $postgresVolume = @(
        $postgresInspect.Mounts | Where-Object {
            $_.Destination -ceq '/var/lib/postgresql/data'
        }
    )
    if ($postgresVolume.Count -ne 1 -or
        $postgresVolume[0].Type -cne 'volume' -or
        $postgresVolume[0].Name -cne $expectedPostgresVolume) {
        throw 'PostgreSQL is not using exactly one durable named data volume.'
    }

    $redisStartedAt = [DateTimeOffset]::Parse(
        [string]$redisInspect.State.StartedAt).UtcDateTime.ToString('O')
    $serverNetwork = Get-B20ComposeNetworkIdentity $serverInspect
    $redisNetwork = Get-B20ComposeNetworkIdentity $redisInspect
    Assert-B20Condition ($serverNetwork.Id -ceq $redisNetwork.Id) (
        'The server and Redis do not share the exact approved network.')
    $coordinationReceipt = [ordered]@{
        provider = 'Redis'
        environment = 'tempest-local'
        runtimeProfile = 'LocalDevelopment'
        serverNodeId = 'tempest-openworld-01'
        expectedRouteCount = 23
        composeProject = 'reborn'
        redisComposeService = 'redis-coordination'
        networkName = $redisNetwork.Name
        networkId = $redisNetwork.Id
        serverComposeConfigHash = [string](
            $serverInspect.Config.Labels.'com.docker.compose.config-hash')
        redisContainerId = [string]$redisInspect.Id
        redisImageId = [string]$redisInspect.Image
        redisImageReference = [string]$redisInspect.Config.Image
        redisStartedAtUtc = $redisStartedAt
        redisRestartCount = [long]$redisInspect.RestartCount
        redisComposeConfigHash = [string](
            $redisInspect.Config.Labels.'com.docker.compose.config-hash')
        redisHealthAtStart = [string]$redisInspect.State.Health.Status
        inputSha256 = $inputSha256
    }
    Assert-B20Condition (
        Test-B20ServerRedisTopology `
            $serverInspect $coordinationReceipt $repositoryRoot) (
        'The game server is not running the exact Redis-coordinated topology.')
    Assert-B20Condition (
        Test-B20RedisIdentity `
            $redisInspect $coordinationReceipt $repositoryRoot) (
        'Redis identity does not match the exact coordinated topology.')

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        try {
            $upValue = Get-B20PrometheusCurrentValue `
                'up{job="godswar-b20h"}'
            $readyValue = Get-B20PrometheusCurrentValue (
                'godswar_legacy_persistence_observer_ready' +
                '{job="godswar-b20h"}')
            $processValue = Get-B20PrometheusCurrentValue (
                'godswar_server_operations_process_start_time_seconds' +
                '{job="godswar-b20h"}')
            $coordinationReadyValue = Get-B20PrometheusCurrentValue (
                'godswar_server_operational_coordination' +
                '{job="godswar-b20h",operational_state="ready"}')
            $coordinationRoutesValue = Get-B20PrometheusCurrentValue (
                'godswar_server_operational_coordination' +
                '{job="godswar-b20h",operational_state="routes"}')
            $legacyValue = Get-B20PrometheusCurrentValue (
                'sum(godswar_legacy_persistence_invocations_total' +
                '{job="godswar-b20h"}) or vector(0)')
            $collectorBaselineClean = $true
            foreach ($state in @(
                'dropped_instruments',
                'dropped_series',
                'dropped_tags',
                'dropped_measurements',
                'truncated_snapshots')) {
                $collectorValue = Get-B20PrometheusCurrentValue (
                    'godswar_server_metrics_collector' +
                    "{job=`"godswar-b20h`",state=`"$state`"}")
                if ($collectorValue -ne 0) {
                    $collectorBaselineClean = $false
                }
            }
            $alerts = (Get-B20PrometheusApi '/api/v1/alerts' 1MB).data.alerts
            if ($upValue -eq 1 -and
                $readyValue -eq 1 -and
                $processValue -gt 0 -and
                $coordinationReadyValue -eq 1 -and
                $coordinationRoutesValue -eq 23 -and
                $legacyValue -eq 0 -and
                @($alerts | Where-Object {
                    $_.state -in @('pending', 'firing')
                }).Count -eq 0 -and
                $collectorBaselineClean) {
                break
            }
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw
            }
        }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        throw 'Prometheus did not observe a clean B20H baseline before timeout.'
    }

    $startedAt = [DateTimeOffset]::UtcNow
    $targetEnd = $startedAt.AddHours(168)
    $startRecord = [ordered]@{
        schemaVersion = 'reborn.b20h.docker-observation.v2'
        topologyKind = 'redis-coordinated-single-worker'
        status = 'running'
        approval = $approval
        window = [ordered]@{
            startedAtUtc = $startedAt.UtcDateTime.ToString('O')
            targetEndedAtUtc = $targetEnd.UtcDateTime.ToString('O')
            approvedMinimumHours = 168
        }
        expectedReplicaCount = 1
        replica = [ordered]@{
            name = $ReplicaName
            serverContainerId = [string]$serverInspect.Id
            serverImageId = [string]$serverInspect.Image
            serverRestartCount = [long]$serverInspect.RestartCount
            prometheusContainerId = [string]$prometheusInspect.Id
            prometheusImageId = [string]$prometheusInspect.Image
            prometheusDataSource = [string]$prometheusData[0].Source
            postgresVolume = [string]$postgresVolume[0].Name
        }
        coordination = $coordinationReceipt
        monitoring = [ordered]@{
            scrapeIntervalSeconds = 30
            maximumScrapeGapSeconds = 300
            retentionDays = 15
            prometheusImage = [string]$prometheusInspect.Config.Image
            artifactSha256 = $artifactSha256
        }
    }
    $recordPath = Join-Path $evidenceDirectory 'observation-start.json'
    Write-B20Json `
        -LiteralPath $recordPath `
        -Value $startRecord `
        -CreateNew

    $relativeDirectory = $runId
    Write-B20Json `
        -LiteralPath $activePath `
        -Value ([ordered]@{
            schemaVersion = 'reborn.b20h.active-observation.v2'
            topologyKind = 'redis-coordinated-single-worker'
            evidenceKind = 'local-alpha-rehearsal'
            eligibleForRetirementAuthorization = $false
            runId = $runId
            evidenceDirectory = $relativeDirectory
            startedAtUtc = $startedAt.UtcDateTime.ToString('O')
            targetEndedAtUtc = $targetEnd.UtcDateTime.ToString('O')
        }) `
        -CreateNew

    [pscustomobject]@{
        Status = 'running'
        StartedAtUtc = $startedAt.UtcDateTime.ToString('O')
        TargetEndedAtUtc = $targetEnd.UtcDateTime.ToString('O')
        SourceCommit = $sourceCommit
        EvidenceDirectory = $evidenceDirectory
        PostgreSqlVolume = [string]$postgresVolume[0].Name
        RedisRequired = $true
        RedisContainerId = [string]$redisInspect.Id
        RedisCoordinationRoutes = 23
        EvidenceKind = 'local-alpha-rehearsal'
        EligibleForRetirementAuthorization = $false
    } | ConvertTo-Json
}
catch {
    if ($null -ne (Get-Variable `
            -Name evidenceDirectory `
            -ErrorAction SilentlyContinue) -and
        (Test-Path -LiteralPath $evidenceDirectory -PathType Container)) {
        [IO.File]::WriteAllText(
            (Join-Path $evidenceDirectory 'startup-failed.txt'),
            [DateTimeOffset]::UtcNow.UtcDateTime.ToString('O') +
            [Environment]::NewLine + $_.Exception.Message +
            [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    }
    throw
}
finally {
    $env:GODSWAR_SOURCE_COMMIT = $savedCommit
    $env:GODSWAR_B20H_EVIDENCE_DIRECTORY = $savedEvidence
    $campaignLock.Dispose()
}
