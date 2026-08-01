[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$ChangeId = 'alpha-b20h-20260801',

    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$ApprovedByRole = 'project-owner',

    [ValidatePattern('^[a-z0-9][a-z0-9._:-]{0,127}$')]
    [string]$ReplicaName = 'tempest-world-01',

    [string]$EvidenceRoot,

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

function Get-B20PrometheusQuery {
    param([Parameter(Mandatory)][string]$Query)

    $encoded = [Uri]::EscapeDataString($Query)
    $raw = Invoke-B20Command docker @(
        'exec',
        'godswar-b20h-prometheus',
        'wget',
        '-T', '10',
        '-t', '1',
        '-qO-',
        "http://127.0.0.1:9091/api/v1/query?query=$encoded")
    Assert-B20Condition `
        ($raw.Length -le 1MB) `
        'Prometheus response exceeds the bounded startup size.'
    Assert-B20NoDuplicateJsonProperties $raw
    $response = $raw | ConvertFrom-Json
    Assert-B20Condition `
        ($response.status -ceq 'success' -and
            $response.data.resultType -ceq 'vector') `
        "Prometheus rejected query '$Query'."
    return @($response.data.result)
}

function Get-B20MetricValue {
    param(
        [Parameter(Mandatory)]
        [object[]]$Result,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Result.Count -ne 1 -or @($Result[0].value).Count -ne 2) {
        throw "$Description must have exactly one current series."
    }
    $value = [double]::Parse(
        [string]$Result[0].value[1],
        [Globalization.CultureInfo]::InvariantCulture)
    Assert-B20Condition `
        (-not [double]::IsNaN($value) -and
            -not [double]::IsInfinity($value)) `
        "$Description returned a non-finite value."
    return $value
}

function Get-B20ActiveAlertCount {
    $raw = Invoke-B20Command docker @(
        'exec',
        'godswar-b20h-prometheus',
        'wget',
        '-T', '10',
        '-t', '1',
        '-qO-',
        'http://127.0.0.1:9091/api/v1/alerts')
    Assert-B20Condition `
        ($raw.Length -le 1MB) `
        'Prometheus alert response exceeds the bounded startup size.'
    Assert-B20NoDuplicateJsonProperties $raw
    $response = $raw | ConvertFrom-Json
    Assert-B20Condition `
        ($response.status -ceq 'success') `
        'Prometheus rejected the startup alert query.'
    return @($response.data.alerts | Where-Object {
        $_.state -in @('pending', 'firing')
    }).Count
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
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
}
Write-B20Json `
    -LiteralPath (Join-Path $evidenceDirectory 'approval.json') `
    -Value $approval `
    -CreateNew

$artifactSha256 = Get-B20ObservationArtifactHashes $repositoryRoot

    $env:GODSWAR_SOURCE_COMMIT = $sourceCommit
    $env:GODSWAR_B20H_EVIDENCE_DIRECTORY = $evidenceDirectory

    Invoke-B20Command docker @(
        'compose',
        '--project-directory', $repositoryRoot,
        '--profile', 'legacy-raw',
        '--profile', 'b20h-observation',
        'up',
        '--build',
        '--detach',
        '--wait',
        '--wait-timeout', '300',
        'server',
        'b20h-prometheus') | Write-Host

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
        $prometheusInspect.State.Health.Status -cne 'healthy') {
        throw 'Every B20H Docker component must be healthy before T0.'
    }

    $postgresVolume = @(
        $postgresInspect.Mounts | Where-Object {
            $_.Destination -ceq '/var/lib/postgresql/data'
        }
    )
    if ($postgresVolume.Count -ne 1 -or
        $postgresVolume[0].Type -cne 'volume') {
        throw 'PostgreSQL is not using exactly one durable named data volume.'
    }

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        try {
            $upResult = Get-B20PrometheusQuery 'up{job="godswar-b20h"}'
            $readyResult = Get-B20PrometheusQuery (
                'godswar_legacy_persistence_observer_ready' +
                '{job="godswar-b20h"}')
            $processResult = Get-B20PrometheusQuery (
                'godswar_server_operations_process_start_time_seconds' +
                '{job="godswar-b20h"}')
            $legacyResult = Get-B20PrometheusQuery (
                'sum(godswar_legacy_persistence_invocations_total' +
                '{job="godswar-b20h"}) or vector(0)')
            $collectorBaselineClean = $true
            foreach ($state in @(
                'dropped_instruments',
                'dropped_series',
                'dropped_tags',
                'dropped_measurements',
                'truncated_snapshots')) {
                $collectorResult = Get-B20PrometheusQuery (
                    'godswar_server_metrics_collector' +
                    "{job=`"godswar-b20h`",state=`"$state`"}")
                if ((Get-B20MetricValue `
                        $collectorResult "Collector $state") -ne 0) {
                    $collectorBaselineClean = $false
                }
            }
            if ((Get-B20MetricValue $upResult 'Target health') -eq 1 -and
                (Get-B20MetricValue $readyResult 'Observer readiness') -eq 1 -and
                (Get-B20MetricValue $processResult 'Process start') -gt 0 -and
                (Get-B20MetricValue $legacyResult 'Legacy invocation') -eq 0 -and
                (Get-B20ActiveAlertCount) -eq 0 -and
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
        schemaVersion = 'reborn.b20h.docker-observation.v1'
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
            schemaVersion = 'reborn.b20h.active-observation.v1'
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
        RedisRequired = $false
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
