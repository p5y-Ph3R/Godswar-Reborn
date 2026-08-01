[CmdletBinding()]
param(
    [switch]$SkipPromtool
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$composePath = Join-Path $repositoryRoot 'docker-compose.yml'
$prometheusPath =
    Join-Path $repositoryRoot 'tools/docker/b20h/prometheus.yml'
$rulesPath = Join-Path $repositoryRoot 'tools/docker/b20h/rules.yml'
$rulesTestPath =
    Join-Path $repositoryRoot 'tools/docker/b20h/rules.test.yml'
$dockerIgnorePath = Join-Path $repositoryRoot '.dockerignore'
$expectedCommit = '0123456789abcdef0123456789abcdef01234567'
$savedCommit = $env:GODSWAR_SOURCE_COMMIT
$savedEvidence = $env:GODSWAR_B20H_EVIDENCE_DIRECTORY
Import-Module `
    (Join-Path $PSScriptRoot 'B20HDockerObservation.Integrity.psm1') `
    -Force

try {
    $hostExecutable = (Get-Process -Id $PID).Path
    $stderrProbe = Invoke-B20Command $hostExecutable @(
        '-NoProfile',
        '-Command',
        "[Console]::Error.Write('expected-stderr'); exit 0")
    Assert-Condition `
        ($stderrProbe -cmatch 'expected-stderr') `
        'Successful native stderr must not abort the command wrapper.'

    $env:GODSWAR_SOURCE_COMMIT = $expectedCommit
    $env:GODSWAR_B20H_EVIDENCE_DIRECTORY =
        Join-Path $repositoryRoot 'artifacts/b20h-compose-test'
    $rendered = @(
        & docker compose `
            -f $composePath `
            --profile legacy-raw `
            --profile b20h-observation `
            config `
            --format json 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw "B20H Compose render failed: $($rendered -join "`n")"
    }
    $compose = ($rendered -join [Environment]::NewLine) |
        ConvertFrom-Json
    $server = $compose.services.server
    $postgres = $compose.services.postgres
    $observer = $compose.services.'b20h-prometheus'

    Assert-Condition `
        ($server.build.args.GODSWAR_SOURCE_COMMIT -ceq $expectedCommit) `
        'The server image must receive the exact approved source commit.'
    Assert-Condition `
        ($server.labels.'com.reborn.source.commit' -ceq $expectedCommit) `
        'The server container must carry the approved source commit.'
    $serverPorts = @($server.ports)
    Assert-Condition `
        (@($serverPorts | Where-Object {
            $_.target -in @(9090, 9091) -or $_.host_ip -notmatch '^127\.'
        }).Count -eq 0) `
        'Management and Prometheus must remain private container loopback.'
    Assert-Condition `
        ($postgres.restart -ceq 'unless-stopped') `
        'PostgreSQL must recover from an ordinary Docker restart.'
    $postgresData = @(
        $postgres.volumes | Where-Object {
            $_.target -ceq '/var/lib/postgresql/data' -and
            $_.type -ceq 'volume'
        }
    )
    Assert-Condition `
        ($postgresData.Count -eq 1) `
        'PostgreSQL must use exactly one durable named data volume.'

    Assert-Condition `
        (@($observer.profiles).Count -eq 1 -and
            $observer.profiles[0] -ceq 'b20h-observation') `
        'The observer must remain opt-in under its exact profile.'
    Assert-Condition `
        ($observer.image -ceq (
            'prom/prometheus:v3.13.1@sha256:' +
            '3c42b892cf723fa54d2f262c37a0e1f80aa8c8ddb1da7b9b0df9455a35a7f893')) `
        'The observer image must remain versioned and digest-pinned.'
    Assert-Condition `
        ($observer.network_mode -ceq 'service:server') `
        'The observer must share only the server loopback namespace.'
    Assert-Condition `
        ($null -eq $observer.PSObject.Properties['ports']) `
        'The observer must not publish a management or Prometheus port.'
    Assert-Condition `
        ($server.environment.GODSWAR_MANAGEMENT_ENABLED -ceq 'true' -and
            $server.environment.GODSWAR_MANAGEMENT_BIND_HOST -ceq
                '127.0.0.1' -and
            $server.environment.GODSWAR_MANAGEMENT_PORT -ceq '9090') `
        'The observed management endpoint must remain private loopback.'
    Assert-Condition `
        ($observer.read_only -eq $true -and
            @($observer.cap_drop) -ccontains 'ALL' -and
            @($observer.security_opt) -ccontains
                'no-new-privileges:true') `
        'The observer must retain its read-only least-privilege boundary.'
    Assert-Condition `
        (@($observer.command) -ccontains
            '--storage.tsdb.retention.time=15d') `
        'The metrics retention must cover more than the 168-hour window.'
    Assert-Condition `
        (@($observer.command) -ccontains
            '--web.listen-address=127.0.0.1:9091') `
        'Prometheus must remain private on shared container loopback.'
    Assert-Condition `
        (@($observer.command) -ccontains
            '--config.file=/etc/prometheus/prometheus.yml' -and
            @($observer.command) -ccontains
                '--storage.tsdb.path=/prometheus') `
        'Prometheus must use the approved config and durable TSDB paths.'
    $metricsVolume = @(
        $observer.volumes | Where-Object {
            $_.target -ceq '/prometheus' -and
            $_.type -ceq 'bind' -and
            [IO.Path]::GetFullPath($_.source) -ceq
                [IO.Path]::GetFullPath((Join-Path `
                    $env:GODSWAR_B20H_EVIDENCE_DIRECTORY 'prometheus')) -and
            ($null -eq $_.PSObject.Properties['read_only'] -or
                -not $_.read_only)
        }
    )
    Assert-Condition `
        ($metricsVolume.Count -eq 1) `
        'Prometheus TSDB must have one durable writable evidence bind.'
    foreach ($requiredMount in @(
        [pscustomobject]@{
            Target = '/etc/prometheus/prometheus.yml'
            Source = $prometheusPath
        },
        [pscustomobject]@{
            Target = '/etc/prometheus/b20h-rules.yml'
            Source = $rulesPath
        })) {
        $mount = @($observer.volumes | Where-Object {
            $_.target -ceq $requiredMount.Target -and
            [IO.Path]::GetFullPath($_.source) -ceq $requiredMount.Source -and
            $_.type -ceq 'bind' -and $_.read_only
        })
        Assert-Condition `
            ($mount.Count -eq 1) `
            "Prometheus requires the exact read-only '$($requiredMount.Target)' mount."
    }
    Assert-Condition `
        (@($observer.volumes).Count -eq 3) `
        'Prometheus must not receive unapproved additional mounts.'

    $config = Get-Content -LiteralPath $prometheusPath -Raw
    $rules = Get-Content -LiteralPath $rulesPath -Raw
    $dockerIgnore = Get-Content -LiteralPath $dockerIgnorePath -Raw
    foreach ($secretPattern in @(
        'appsettings.Local.json',
        'appsettings.*.local.json',
        '*.pfx',
        '*.p12',
        '*.key')) {
        Assert-Condition `
            (@($dockerIgnore -split "`r?`n") -ccontains $secretPattern) `
            "Docker build context must exclude '$secretPattern'."
    }
    Assert-Condition `
        ($config -cmatch 'scrape_interval:\s+30s' -and
            $config -cmatch 'scrape_timeout:\s+5s' -and
            $config -cmatch '127\.0\.0\.1:9090') `
        'The observer must scrape private metrics every 30 seconds.'
    Assert-Condition `
        (@([regex]::Matches($config, '(?m)^\s*- job_name:')).Count -eq 1 -and
            @([regex]::Matches($config, '(?m)^\s*- 127\.0\.0\.1:9090\r?$')).Count -eq 1 -and
            $config -cnotmatch '(?m)^\s*remote_(?:write|read):') `
        'Prometheus must have exactly one local target and no remote endpoint.'
    foreach ($requiredAlert in @(
        'B20HTelemetryGap',
        'B20HTargetMissing',
        'B20HObserverMissing',
        'B20HObserverNotReady',
        'B20HLegacyPersistenceInvoked',
        'B20HServerProcessRestarted',
        'B20HProcessMetricMissing',
        'B20HLegacyCounterReset',
        'B20HMetricsCollectorPressure',
        'B20HMetricsCollectorMissing')) {
        Assert-Condition `
            ($rules -cmatch "alert:\s+$requiredAlert(?:\r?\n)") `
            "Required fail-closed alert '$requiredAlert' is missing."
    }

    foreach ($scriptName in @(
        'StartB20HDockerObservation.ps1',
        'GetB20HDockerObservation.ps1',
        'ExportB20HDockerObservationTelemetry.ps1',
        'B20HDockerObservation.Integrity.psm1')) {
        $tokens = $null
        $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot $scriptName),
            [ref]$tokens,
            [ref]$errors)
        Assert-Condition `
            ($errors.Count -eq 0) `
            "$scriptName has a PowerShell parser error."
    }

    if (-not $SkipPromtool) {
        $image = [string]$observer.image
        $result = @(
            & docker run `
                --rm `
                --entrypoint=/bin/promtool `
                --volume "${prometheusPath}:/etc/prometheus/prometheus.yml:ro" `
                --volume "${rulesPath}:/etc/prometheus/b20h-rules.yml:ro" `
                $image `
                check config /etc/prometheus/prometheus.yml 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw "Promtool rejected B20H configuration: $($result -join "`n")"
        }

        $discovery = @(
            & docker run `
                --rm `
                --entrypoint=/bin/promtool `
                --volume "${prometheusPath}:/etc/prometheus/prometheus.yml:ro" `
                --volume "${rulesPath}:/etc/prometheus/b20h-rules.yml:ro" `
                $image `
                check service-discovery `
                /etc/prometheus/prometheus.yml `
                godswar-b20h `
                --timeout=5s 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw "Promtool rejected service discovery: $($discovery -join "`n")"
        }
        $targets = ($discovery -join [Environment]::NewLine) |
            ConvertFrom-Json
        Assert-Condition `
            (@($targets).Count -eq 1 -and
                $targets[0].labels.'__address__' -ceq
                    '127.0.0.1:9090' -and
                $targets[0].labels.job -ceq 'godswar-b20h' -and
                $targets[0].labels.realm -ceq 'tempest' -and
                $targets[0].labels.replica -ceq 'tempest-world-01') `
            'Prometheus must discover exactly the approved private target.'

        $ruleTestResult = @(
            & docker run `
                --rm `
                --entrypoint=/bin/promtool `
                --volume "${rulesPath}:/etc/prometheus/b20h-rules.yml:ro" `
                --volume "${rulesTestPath}:/etc/prometheus/b20h-rules.test.yml:ro" `
                $image `
                test rules /etc/prometheus/b20h-rules.test.yml 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw "Promtool rejected B20H rule behavior: $($ruleTestResult -join "`n")"
        }
    }

    Write-Host 'B20H Docker observation checks passed.'
}
finally {
    $env:GODSWAR_SOURCE_COMMIT = $savedCommit
    $env:GODSWAR_B20H_EVIDENCE_DIRECTORY = $savedEvidence
}
