[CmdletBinding()]
param(
    [ValidateSet('postgres:17.9-alpine')]
    [string]$PostgresImage = 'postgres:17.9-alpine',

    [string]$ReportPath =
        'artifacts/b19/recovery-gate-result.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$protocolChecksAssembly = Join-Path $repositoryRoot (
    'tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/' +
    'Godswar.Server.ProtocolChecks.dll')
$absoluteReportPath = if ([IO.Path]::IsPathRooted($ReportPath)) {
    [IO.Path]::GetFullPath($ReportPath)
}
else {
    [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $ReportPath))
}

. (Join-Path $PSScriptRoot 'B19PostgresRecoveryGate.Helpers.ps1')
. (Join-Path $PSScriptRoot (
    'B19PostgresRecoveryGate.Reconciliation.ps1'))

$reportDirectory = Split-Path -Parent $absoluteReportPath
New-Item -ItemType Directory `
    -Path $reportDirectory -Force | Out-Null
$reportStream = [IO.FileStream]::new(
    $absoluteReportPath,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write,
    [IO.FileShare]::None,
    4096,
    [IO.FileOptions]::WriteThrough)

$startedAt = [DateTimeOffset]::UtcNow
$runToken = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$containerName = "reborn-b19-$runToken"
$sourceDatabase = "godswar_b19_${runToken}_source"
$restoredDatabase = "godswar_b19_${runToken}_restored"
$remoteDumpPath = "/tmp/reborn-b19-$runToken.custom.dump"
$postgresUser = 'reborn_b19'
$postgresPassword = New-B19RandomHexSecret

$containerStarted = $false
$containerCleanupArmed = $false
$sourceCreated = $false
$restoredCreated = $false
$failureCategory = $null
$primaryError = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[object]]::new()
$scenarios = [Collections.Generic.List[object]]::new()

$report = [ordered]@{
    schemaVersion = 1
    gate = 'B19 disposable PostgreSQL reconciliation recovery'
    status = 'running'
    startedAtUtc = $startedAt.ToString('O')
    finishedAtUtc = $null
    durationMs = 0
    sourceCommit = $null
    sourceTreeDirty = $null
    postgres = [ordered]@{
        image = $PostgresImage
        imageId = $null
        serverVersionNumber = $null
        migrationCount = $null
        migrationHead = $null
    }
    recovery = [ordered]@{
        dumpSha256 = $null
        dumpBytes = 0
        dumpDurationMs = 0
        restoreDurationMs = 0
        verifiedReadyDurationMs = 0
        logicalRpoLostTransactions = $null
        logicalRpoScope =
            'quiesced synthetic snapshot only'
        productionRpoClaim = $false
        productionRtoClaim = $false
    }
    checks = $checks
    scenarios = $scenarios
    cleanup = [ordered]@{
        status = 'pending'
        errors = $cleanupErrors
    }
    failureCategory = $null
    failureMessage = $null
}

try {
    $failureCategory = 'configuration'
    Assert-B19OwnedContainerName $containerName
    Assert-B19DatabaseName $sourceDatabase $runToken
    Assert-B19DatabaseName $restoredDatabase $runToken
    if ($containerName -in @(
            'godswar-postgres',
            'godswar-server')) {
        throw 'B19 may never target a live repository container.'
    }
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker is required for the B19 recovery gate.'
    }
    if (-not (Test-Path `
            -LiteralPath $protocolChecksAssembly `
            -PathType Leaf)) {
        throw 'Release protocol-check assembly is missing. Build first.'
    }
    $commitOutput = @(
        & git -C $repositoryRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $commitOutput.Count -eq 1) {
        $report.sourceCommit = "$($commitOutput[0])".Trim()
    }
    $statusOutput = @(
        & git -C $repositoryRoot status --porcelain=v1 `
            --untracked-files=normal 2>$null)
    if ($LASTEXITCODE -eq 0) {
        $report.sourceTreeDirty = $statusOutput.Count -gt 0
    }

    $failureCategory = 'container-start'
    $collisionCheck = Invoke-B19Docker -AllowFailure `
        -Operation 'random container-name collision check' `
        -Arguments @('inspect', $containerName)
    if ($collisionCheck.ExitCode -eq 0) {
        throw 'The random B19 container name is already in use.'
    }
    $containerCleanupArmed = $true
    Invoke-B19Docker `
        -Operation 'start owned disposable PostgreSQL 17.9' `
        -Arguments @(
            'run',
            '--detach',
            '--name', $containerName,
            '--label',
            'com.reborn.test-scope=b19-postgres-recovery',
            '--publish', '127.0.0.1::5432',
            '--tmpfs',
            '/var/lib/postgresql/data:rw,noexec,nosuid,size=512m',
            '--env', 'POSTGRES_DB=postgres',
            '--env', "POSTGRES_USER=$postgresUser",
            '--env', "POSTGRES_PASSWORD=$postgresPassword",
            $PostgresImage
        ) | Out-Null
    $containerStarted = $true
    Wait-B19PostgresReady $containerName $postgresUser
    $postgresPort = Get-B19PublishedPort $containerName

    $imageInspect = Invoke-B19Docker `
        -Operation 'inspect disposable PostgreSQL image identity' `
        -Arguments @(
            'inspect',
            '--format', '{{.Image}}',
            $containerName
        )
    $report.postgres.imageId =
        ($imageInspect.Output -join '').Trim()
    $versionText = Invoke-B19Psql `
        $containerName $postgresPassword $postgresUser `
        'postgres' 'SHOW server_version_num;'
    $versionNumber = 0
    if (-not [int]::TryParse(
            $versionText.Trim(),
            [ref]$versionNumber) -or
        $versionNumber -ne 170009) {
        throw "B19 requires PostgreSQL 17.9; received '$versionText'."
    }
    $report.postgres.serverVersionNumber = $versionNumber

    $failureCategory = 'source-reconciliation'
    New-B19Database `
        $containerName $postgresPassword $postgresUser `
        $sourceDatabase $runToken
    $sourceCreated = $true
    $sourceConnection = New-B19ConnectionString `
        $postgresPort $postgresUser $postgresPassword $sourceDatabase
    $sourceCheck = 'PostgreSQL bounded economy reconciliation'
    $sourceWatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-B19ProtocolCheck `
        $protocolChecksAssembly $sourceCheck $sourceConnection
    $sourceWatch.Stop()
    $checks.Add([ordered]@{
        scenario = 'source'
        name = $sourceCheck
        status = 'passed'
        durationMs =
            [long]$sourceWatch.Elapsed.TotalMilliseconds
    })

    $sourceMigration = Get-B19MigrationState `
        $containerName $postgresPassword $postgresUser $sourceDatabase
    Assert-B19CurrentMigrationState $sourceMigration
    $report.postgres.migrationCount = $sourceMigration.count
    $report.postgres.migrationHead = $sourceMigration.head
    $sourceReconciliation = Get-B19ReconciliationState `
        $containerName $postgresPassword $postgresUser $sourceDatabase
    Assert-B19Reconciled $sourceReconciliation
    $sourceFingerprint = Get-B19LogicalFingerprint `
        $containerName $postgresPassword $postgresUser $sourceDatabase
    $scenarios.Add([ordered]@{
        name = 'bounded-report-repair-source'
        status = 'passed'
        migration = $sourceMigration
        reconciliation = $sourceReconciliation
        logicalFingerprintSha256 = $sourceFingerprint
    })

    $failureCategory = 'logical-dump'
    $dumpWatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-B19PostgresTool `
        -ContainerName $containerName `
        -Password $postgresPassword `
        -Operation 'create transactionally consistent custom dump' `
        -Arguments @(
            'pg_dump',
            '--format=custom',
            '--compress=9',
            '--serializable-deferrable',
            '--no-owner',
            '--no-privileges',
            '--username', $postgresUser,
            '--dbname', $sourceDatabase,
            '--file', $remoteDumpPath
        ) | Out-Null
    $dumpWatch.Stop()
    $report.recovery.dumpDurationMs =
        [long]$dumpWatch.Elapsed.TotalMilliseconds

    $dumpHashResult = Invoke-B19PostgresTool `
        -ContainerName $containerName `
        -Password $postgresPassword `
        -Operation 'hash custom dump' `
        -Arguments @('sha256sum', $remoteDumpPath)
    $dumpHashLine =
        ($dumpHashResult.Output -join '').Trim()
    if ($dumpHashLine -notmatch '^([a-f0-9]{64})\s+') {
        throw 'Could not parse the custom dump SHA-256.'
    }
    $report.recovery.dumpSha256 = $Matches[1].ToUpperInvariant()
    $dumpSizeResult = Invoke-B19PostgresTool `
        -ContainerName $containerName `
        -Password $postgresPassword `
        -Operation 'measure custom dump' `
        -Arguments @('stat', '-c', '%s', $remoteDumpPath)
    $dumpBytes = 0L
    if (-not [long]::TryParse(
            ($dumpSizeResult.Output -join '').Trim(),
            [ref]$dumpBytes) -or
        $dumpBytes -le 0) {
        throw 'The B19 custom dump size was invalid.'
    }
    $report.recovery.dumpBytes = $dumpBytes

    $failureCategory = 'logical-restore'
    New-B19Database `
        $containerName $postgresPassword $postgresUser `
        $restoredDatabase $runToken
    $restoredCreated = $true
    $verifiedReadyWatch = [Diagnostics.Stopwatch]::StartNew()
    $restoreWatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-B19PostgresTool `
        -ContainerName $containerName `
        -Password $postgresPassword `
        -Operation 'restore custom dump' `
        -Arguments @(
            'pg_restore',
            '--exit-on-error',
            '--no-owner',
            '--no-privileges',
            '--username', $postgresUser,
            '--dbname', $restoredDatabase,
            $remoteDumpPath
        ) | Out-Null
    $restoreWatch.Stop()
    $report.recovery.restoreDurationMs =
        [long]$restoreWatch.Elapsed.TotalMilliseconds

    $restoredMigration = Get-B19MigrationState `
        $containerName $postgresPassword $postgresUser $restoredDatabase
    Assert-B19CurrentMigrationState $restoredMigration
    $restoredReconciliation = Get-B19ReconciliationState `
        $containerName $postgresPassword $postgresUser $restoredDatabase
    Assert-B19Reconciled $restoredReconciliation
    $restoredFingerprint = Get-B19LogicalFingerprint `
        $containerName $postgresPassword $postgresUser $restoredDatabase
    if ($restoredFingerprint -cne $sourceFingerprint) {
        throw 'Restored logical fingerprint differs from the source.'
    }

    $restoredConnection = New-B19ConnectionString `
        $postgresPort $postgresUser $postgresPassword $restoredDatabase
    $restoredCheck =
        'PostgreSQL restored reconciliation verification'
    $restoredCheckWatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-B19ProtocolCheck `
        $protocolChecksAssembly $restoredCheck $restoredConnection
    $restoredCheckWatch.Stop()
    $checks.Add([ordered]@{
        scenario = 'restored'
        name = $restoredCheck
        status = 'passed'
        durationMs =
            [long]$restoredCheckWatch.Elapsed.TotalMilliseconds
    })
    $verifiedReadyWatch.Stop()
    $report.recovery.verifiedReadyDurationMs =
        [long]$verifiedReadyWatch.Elapsed.TotalMilliseconds
    $scenarios.Add([ordered]@{
        name = 'custom-dump-restored-and-verified'
        status = 'passed'
        migration = $restoredMigration
        reconciliation = $restoredReconciliation
        logicalFingerprintSha256 = $restoredFingerprint
    })

    $report.recovery.logicalRpoLostTransactions = 0
    $report.status = 'passed'
    $failureCategory = $null
}
catch {
    $primaryError = $_
    $report.status = 'failed'
    $report.failureCategory = $failureCategory
    $safeMessage = $_.Exception.Message
    if (-not [string]::IsNullOrEmpty($postgresPassword)) {
        $safeMessage = $safeMessage.Replace(
            $postgresPassword,
            '[REDACTED]')
    }
    if ($safeMessage.Length -gt 512) {
        $safeMessage = $safeMessage.Substring(0, 512)
    }
    $report.failureMessage = $safeMessage
}
finally {
    if ($containerStarted) {
        try {
            Invoke-B19PostgresTool `
                -ContainerName $containerName `
                -Password $postgresPassword `
                -Operation 'remove temporary custom dump' `
                -Arguments @('rm', '-f', $remoteDumpPath) |
                Out-Null
        }
        catch {
            $cleanupErrors.Add(
                'Temporary dump cleanup failed.')
        }

        foreach ($databaseEntry in @(
            [pscustomobject]@{
                Name = $restoredDatabase
                Created = $restoredCreated
            },
            [pscustomobject]@{
                Name = $sourceDatabase
                Created = $sourceCreated
            })) {
            if (-not $databaseEntry.Created) {
                continue
            }
            try {
                Remove-B19Database `
                    $containerName $postgresPassword $postgresUser `
                    $databaseEntry.Name $runToken
            }
            catch {
                $cleanupErrors.Add(
                    "Disposable database cleanup failed.")
            }
        }

    }

    if ($containerCleanupArmed) {
        try {
            Remove-B19OwnedContainer $containerName
            $containerStarted = $false
            $containerCleanupArmed = $false
        }
        catch {
            $cleanupErrors.Add(
                'Owned disposable container cleanup failed.')
        }
    }

    $postgresPassword = $null

    if ($cleanupErrors.Count -eq 0 -and
        -not $containerStarted -and
        -not $containerCleanupArmed) {
        $report.cleanup.status = 'passed'
    }
    else {
        $report.cleanup.status = 'failed'
        if ($null -eq $primaryError) {
            $report.status = 'failed'
            $report.failureCategory = 'cleanup'
            $report.failureMessage =
                'One or more disposable resources were not removed.'
        }
    }

    $finishedAt = [DateTimeOffset]::UtcNow
    $report.finishedAtUtc = $finishedAt.ToString('O')
    $report.durationMs =
        [long]($finishedAt - $startedAt).TotalMilliseconds
    $writer = [IO.StreamWriter]::new(
        $reportStream,
        [Text.UTF8Encoding]::new($false),
        4096,
        $true)
    try {
        $reportJson = $report | ConvertTo-Json -Depth 8
        $writer.Write($reportJson)
        $writer.Flush()
        $reportStream.Flush($true)
    }
    finally {
        try {
            $writer.Dispose()
        }
        finally {
            $reportStream.Dispose()
        }
    }
}

if ($report.status -ne 'passed') {
    throw (
        "B19 recovery gate failed [$($report.failureCategory)]: " +
        $report.failureMessage)
}

Write-Host (
    "B19 recovery gate passed: $($checks.Count) checks, " +
    "$($scenarios.Count) scenarios, cleanup verified.")
Write-Host "Machine-readable result: $absoluteReportPath"
