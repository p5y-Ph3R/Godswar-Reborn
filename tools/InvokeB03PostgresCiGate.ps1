[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-fA-F0-9]{12,64}$')]
    [string] $ContainerId,

    [ValidateSet('127.0.0.1', 'localhost', '::1')]
    [string] $PostgresHost = '127.0.0.1',

    [ValidateRange(1, 65535)]
    [int] $PostgresPort = 5432,

    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]*$')]
    [string] $PostgresUser = 'postgres',

    [string] $ReportPath = 'artifacts/b03/postgres-ci-result.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$protocolChecksAssembly = Join-Path $repositoryRoot (
    'tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/' +
    'Godswar.Server.ProtocolChecks.dll')
$fixturePath = Join-Path $repositoryRoot (
    'tests/Godswar.Server.ProtocolChecks/Fixtures/b03-prefix-008.sql')
$absoluteReportPath = if ([IO.Path]::IsPathRooted($ReportPath)) {
    [IO.Path]::GetFullPath($ReportPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReportPath))
}

$startedAt = [DateTimeOffset]::UtcNow
$runToken = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$databaseNames = [ordered]@{
    Empty = "godswar_b03_${runToken}_empty"
    Prefix = "godswar_b03_${runToken}_prefix"
    Restored = "godswar_b03_${runToken}_restored"
}
$remoteFixturePath = "/tmp/godswar_b03_${runToken}_fixture.sql"
$remoteDumpPath = "/tmp/godswar_b03_${runToken}_prefix.dump"
$checkResults = [Collections.Generic.List[object]]::new()
$scenarioResults = [Collections.Generic.List[object]]::new()
$cleanupErrors = [Collections.Generic.List[string]]::new()
$primaryError = $null
$failureCategory = $null
$postgresPassword = $env:GODSWAR_B03_POSTGRES_PASSWORD

$report = [ordered]@{
    schemaVersion = 1
    gate = 'B03 mandatory disposable PostgreSQL'
    status = 'running'
    startedAtUtc = $startedAt.ToString('O')
    finishedAtUtc = $null
    durationMs = 0
    sourceCommit = $null
    postgres = [ordered]@{
        requiredMajor = 17
        serverVersionNumber = $null
    }
    expectedMigrationCount = 24
    expectedMigrationHead = '20260729_023_npc_content_release'
    checks = $checkResults
    scenarios = $scenarioResults
    cleanup = [ordered]@{
        status = 'pending'
        errors = $cleanupErrors
    }
    failureCategory = $null
    failureMessage = $null
}

. (Join-Path $PSScriptRoot 'B03PostgresCiGate.Helpers.ps1')

try {
    $failureCategory = 'configuration'
    if ([string]::IsNullOrWhiteSpace($postgresPassword)) {
        throw 'GODSWAR_B03_POSTGRES_PASSWORD is required.'
    }
    if (-not (Test-Path -LiteralPath $protocolChecksAssembly -PathType Leaf)) {
        throw "Release protocol-check assembly is missing. Build the solution first."
    }
    if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
        throw 'The tracked B03 historical fixture is missing.'
    }
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required for the B03 disposable PostgreSQL gate.'
    }

    $commitOutput = @(& git -C $repositoryRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $commitOutput.Count -eq 1) {
        $report.sourceCommit = $commitOutput[0].ToString().Trim()
    }

    $failureCategory = 'postgres-version'
    $versionText = Invoke-Psql -Database 'postgres' -Sql 'SHOW server_version_num;'
    $versionNumber = 0
    if (-not [int]::TryParse($versionText.Trim(), [ref]$versionNumber) -or
        $versionNumber -lt 170000 -or
        $versionNumber -ge 180000) {
        throw "B03 requires PostgreSQL 17; server_version_num was '$versionText'."
    }
    $report.postgres.serverVersionNumber = $versionNumber
    $publishedPorts = Invoke-Docker `
        -Description 'Inspect PostgreSQL service port mapping' `
        -Arguments @('port', $ContainerId, '5432/tcp')
    if (-not @($publishedPorts | Where-Object {
        $_ -match (':' + [regex]::Escape($PostgresPort.ToString()) + '$')
    })) {
        throw (
            "Container $ContainerId does not publish PostgreSQL on the supplied " +
            "loopback port $PostgresPort.")
    }

    $failureCategory = 'migration-foundation'
    Invoke-RequiredProtocolCheck `
        -Phase 'migration-foundation' `
        -Name 'PostgreSQL migration safety foundation'

    $failureCategory = 'empty-bootstrap'
    New-DisposableDatabase $databaseNames.Empty
    $emptyWatch = [Diagnostics.Stopwatch]::StartNew()
    $emptyConnection = New-TestConnectionString $databaseNames.Empty
    Invoke-RequiredProtocolCheck `
        -Phase 'empty-bootstrap' `
        -Name 'PostgreSQL schema release migration paths' `
        -SchemaReleaseConnectionString $emptyConnection
    $emptyState = Get-MigrationState $databaseNames.Empty
    $emptyWatch.Stop()
    Add-ScenarioResult `
        -Name 'empty-bootstrap' `
        -InitialMigrationCount 0 `
        -FinalState $emptyState `
        -DurationMs ([long]$emptyWatch.Elapsed.TotalMilliseconds)

    $failureCategory = 'historical-fixture'
    New-DisposableDatabase $databaseNames.Prefix
    $prefixConnection = New-TestConnectionString $databaseNames.Prefix
    Invoke-RequiredProtocolCheck `
        -Phase 'historical-prefix' `
        -Name 'PostgreSQL migration-prefix fixture' `
        -GeneralConnectionString $prefixConnection `
        -MigrationPrefix '20260723_008_zodiac_skill_grid_state'

    Invoke-Docker -Description 'Copy B03 fixture into PostgreSQL container' -Arguments @(
        'cp',
        $fixturePath,
        "${ContainerId}:$remoteFixturePath"
    ) | Out-Null
    Invoke-PostgresTool -Description 'Load B03 historical fixture' -Arguments @(
        'psql',
        '--no-psqlrc',
        '--set', 'ON_ERROR_STOP=1',
        '--username', $PostgresUser,
        '--dbname', $databaseNames.Prefix,
        '--file', $remoteFixturePath
    ) | Out-Null

    $prefixState = Get-MigrationState $databaseNames.Prefix
    if ($prefixState.count -ne 9 -or
        $prefixState.head -ne '20260723_008_zodiac_skill_grid_state') {
        throw 'The historical fixture did not stop at the exact migration-008 prefix.'
    }
    $sourceFixtureFingerprint =
        Get-HistoricalFixtureFingerprint $databaseNames.Prefix
    if ($sourceFixtureFingerprint -notmatch '^1\|1\|1\|1\|[0-9a-f]{32}$') {
        throw 'The historical source is missing one or more durability sentinels.'
    }

    Invoke-PostgresTool -Description 'Dump B03 historical fixture' -Arguments @(
        'pg_dump',
        '--format=custom',
        '--compress=9',
        '--no-owner',
        '--no-privileges',
        '--username', $PostgresUser,
        '--dbname', $databaseNames.Prefix,
        '--file', $remoteDumpPath
    ) | Out-Null

    New-DisposableDatabase $databaseNames.Restored
    Invoke-PostgresTool -Description 'Restore B03 historical fixture' -Arguments @(
        'pg_restore',
        '--exit-on-error',
        '--no-owner',
        '--no-privileges',
        '--username', $PostgresUser,
        '--dbname', $databaseNames.Restored,
        $remoteDumpPath
    ) | Out-Null
    $restoredPrefixState = Get-MigrationState $databaseNames.Restored
    if ($restoredPrefixState.count -ne 9 -or
        $restoredPrefixState.head -ne '20260723_008_zodiac_skill_grid_state') {
        throw 'The restored historical fixture does not have the exact source history.'
    }
    $restoredFixtureFingerprint =
        Get-HistoricalFixtureFingerprint $databaseNames.Restored
    if ($restoredFixtureFingerprint -ne $sourceFixtureFingerprint) {
        throw 'The restored historical fixture changed a durability sentinel.'
    }

    $historicalWatch = [Diagnostics.Stopwatch]::StartNew()
    $restoredConnection = New-TestConnectionString $databaseNames.Restored
    Invoke-RequiredProtocolCheck `
        -Phase 'historical-upgrade' `
        -Name 'PostgreSQL schema release migration paths' `
        -SchemaReleaseConnectionString $restoredConnection
    $historicalState = Get-MigrationState $databaseNames.Restored
    $historicalWatch.Stop()
    Add-ScenarioResult `
        -Name 'restored-prefix-008-upgrade' `
        -InitialMigrationCount 9 `
        -FinalState $historicalState `
        -DurationMs ([long]$historicalWatch.Elapsed.TotalMilliseconds) `
        -FixtureKind 'synthetic-pg17-custom-dump'

    $failureCategory = 'current-idempotence'
    $currentWatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-RequiredProtocolCheck `
        -Phase 'current-idempotence' `
        -Name 'PostgreSQL schema release migration paths' `
        -SchemaReleaseConnectionString $restoredConnection
    $currentState = Get-MigrationState $databaseNames.Restored
    $currentWatch.Stop()
    Add-ScenarioResult `
        -Name 'current-schema-idempotence' `
        -InitialMigrationCount 24 `
        -FinalState $currentState `
        -DurationMs ([long]$currentWatch.Elapsed.TotalMilliseconds) `
        -FixtureKind 'restored-prefix-008-upgrade'

    $failureCategory = 'repository-smoke'
    foreach ($checkName in @(
        'PostgreSQL forward-only database cleanup',
        'PostgreSQL official NPC content publication',
        'PostgreSQL pinned world-content baseline',
        'PostgreSQL equipment-forge race and preservation',
        'PostgreSQL Zodiac level-up race',
        'PostgreSQL authoritative pet level-up',
        'PostgreSQL pet-egg hatch transaction'
    )) {
        Invoke-RequiredProtocolCheck `
            -Phase 'repository-and-concurrency-smoke' `
            -Name $checkName `
            -GeneralConnectionString $emptyConnection
    }

    $report.status = 'passed'
    $failureCategory = $null
}
catch {
    $primaryError = $_
    $report.status = 'failed'
    $report.failureCategory = $failureCategory
    $report.failureMessage = $_.Exception.Message
}
finally {
    foreach ($remotePath in @($remoteFixturePath, $remoteDumpPath)) {
        try {
            Invoke-PostgresTool -Description "Remove $remotePath" -Arguments @(
                'rm',
                '-f',
                $remotePath
            ) | Out-Null
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }

    $cleanupDatabases = @($databaseNames.Values)
    [array]::Reverse($cleanupDatabases)
    foreach ($database in $cleanupDatabases) {
        try {
            Remove-DisposableDatabase $database
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }

    if ($cleanupErrors.Count -eq 0) {
        $report.cleanup.status = 'passed'
    }
    else {
        $report.cleanup.status = 'failed'
        if ($null -eq $primaryError) {
            $report.status = 'failed'
            $report.failureCategory = 'cleanup'
            $report.failureMessage = 'One or more disposable resources could not be removed.'
        }
    }

    $finishedAt = [DateTimeOffset]::UtcNow
    $report.finishedAtUtc = $finishedAt.ToString('O')
    $report.durationMs = [long]($finishedAt - $startedAt).TotalMilliseconds
    $reportDirectory = Split-Path -Parent $absoluteReportPath
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $absoluteReportPath -Encoding utf8
}

if ($report.status -ne 'passed') {
    throw (
        "B03 PostgreSQL gate failed [$($report.failureCategory)]: " +
        $report.failureMessage)
}

Write-Host (
    "B03 PostgreSQL gate passed: $($checkResults.Count) required checks, " +
    "$($scenarioResults.Count) migration scenarios.")
Write-Host "Machine-readable result: $absoluteReportPath"
