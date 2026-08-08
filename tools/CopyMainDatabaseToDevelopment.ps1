[CmdletBinding()]
param(
    [string]$ConfigurationDirectory,
    [switch]$AllowDevelopmentDataReplacement
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1') -Force
Import-Module `
    (Join-Path $PSScriptRoot 'DevelopmentDatabaseClone.Sql.psm1') -Force

function Invoke-PsqlScalar {
    param([string]$Container, [string]$Database, [string]$Sql)

    $value = & docker exec $Container psql `
        --username godswar `
        --dbname $Database `
        --tuples-only `
        --no-align `
        --set ON_ERROR_STOP=1 `
        --command $Sql
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL query failed in '$Container'."
    }
    return ($value -join "`n").Trim()
}

function Write-RestrictedSecret {
    param([string]$LiteralPath, [string]$Value)

    [IO.File]::WriteAllText(
        $LiteralPath,
        $Value,
        [Text.UTF8Encoding]::new($false))
    Protect-DevelopmentPrivateFile $LiteralPath | Out-Null
}

function Get-TextSha256 {
    param([string]$Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $sha256.Dispose()
    }
}

function Assert-DevelopmentPostgresIdentity {
    param([Parameter(Mandatory)][string]$ExpectedContainerId)

    $container = Assert-DevelopmentContainer `
        'godswar-dev-postgres' 'postgres'
    $volumes = @($container.Mounts | Where-Object {
        $_.Destination -ceq '/var/lib/postgresql/data'
    })
    if ([string]$container.Id -cne $ExpectedContainerId -or
        $volumes.Count -ne 1 -or
        $volumes[0].Name -cne 'godswar-dev-postgres-data') {
        throw 'Development PostgreSQL identity changed during clone work.'
    }
    return $container
}

$repositoryRoot = Get-DevelopmentRepositoryRoot
$configurationRoot = Get-DevelopmentConfigurationDirectory `
    $ConfigurationDirectory
$environmentPath = Get-DevelopmentEnvironmentPath $ConfigurationDirectory
$mainGuard = Get-MainObservationGuard
$source = Get-DockerContainer 'godswar-postgres'
$target = Assert-DevelopmentContainer `
    'godswar-dev-postgres' 'postgres'
if ($source.State.Health.Status -cne 'healthy' -or
    $target.State.Health.Status -cne 'healthy') {
    throw 'Both source and development PostgreSQL must be healthy.'
}

$sourceVolume = @($source.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data'
})
$targetVolume = @($target.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data'
})
if ($sourceVolume.Count -ne 1 -or
    $sourceVolume[0].Name -cne 'reborn_godswar-postgres-data') {
    throw 'Source PostgreSQL is not the expected authoritative volume.'
}
if ($targetVolume.Count -ne 1 -or
    $targetVolume[0].Name -cne 'godswar-dev-postgres-data') {
    throw 'Target PostgreSQL is not the isolated development volume.'
}

$tableCount = [int](Invoke-PsqlScalar `
    'godswar-dev-postgres' 'godswar' (
        "select count(*) from information_schema.tables " +
        "where table_schema = 'public';"))
if ($tableCount -gt 0 -and -not $AllowDevelopmentDataReplacement) {
    [pscustomobject]@{
        Status = 'existing_development_database_preserved'
        PublicTableCount = $tableCount
        TargetContainerId = [string]$target.Id
        TargetVolume = [string]$targetVolume[0].Name
    }
    return
}

$leaseCountSql = Get-DevelopmentCloneLeaseCountSql
$activeLeases = [int](Invoke-PsqlScalar `
    'godswar-postgres' 'godswar' $leaseCountSql)
if ($activeLeases -ne 0) {
    throw 'Source outbox has an active lease; retry the clone later.'
}
$unreviewedEventSql = Get-DevelopmentCloneUnreviewedEventSql
$unknownEvents = Invoke-PsqlScalar `
    'godswar-postgres' 'godswar' $unreviewedEventSql
if (-not [string]::IsNullOrWhiteSpace($unknownEvents)) {
    throw "Source outbox contains an unreviewed event type: $unknownEvents"
}

$migrationSql = Get-DevelopmentCloneMigrationSql
$countSql = Get-DevelopmentCloneCountSql
$sourceMigrationsBefore = Invoke-PsqlScalar `
    'godswar-postgres' 'godswar' $migrationSql
$sourceCountsBefore = Invoke-PsqlScalar `
    'godswar-postgres' 'godswar' $countSql

$workRoot = Protect-DevelopmentPrivateDirectory (
    Join-Path $configurationRoot 'clone-work')
$workDirectory = [IO.Path]::GetFullPath((Join-Path `
    $workRoot ([Guid]::NewGuid().ToString('N'))))
$workPrefix = [IO.Path]::GetFullPath($workRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $workDirectory.StartsWith(
        $workPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Clone workspace escaped its bounded development directory.'
}
$workDirectory = Protect-DevelopmentPrivateDirectory $workDirectory
$sourcePasswordPath = Join-Path $workDirectory 'source.pgpass'
$targetPasswordPath = Join-Path $workDirectory 'target.pgpass'
$dumpPath = Join-Path $workDirectory 'godswar.dump'
$postgresImage = 'postgres@sha256:' +
    'c7526c0f6c3f30260a563d7bcf8ad778effac59a44f8ffa86678c35418338609'
$stagingDatabase = 'godswar_clone_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 16)
$backupDatabase = 'godswar_previous_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 16)
$stagingExists = $false
$backupExists = $false
$receipt = $null
$cloneResult = $null

try {
    $sourcePassword = Get-DotEnvValue `
        (Join-Path $repositoryRoot '.env') 'POSTGRES_PASSWORD'
    Write-RestrictedSecret $sourcePasswordPath `
        "godswar-postgres:5432:godswar:godswar:$sourcePassword"
    $sourcePassword = $null

    $backupMount = "type=bind,source=$workDirectory,target=/backup"
    $sourceSecretMount =
        "type=bind,source=$sourcePasswordPath," +
        'target=/run/secrets/pgpass,readonly'
    & docker run --rm `
        --network reborn_default `
        --read-only `
        --tmpfs '/tmp:rw,noexec,nosuid,nodev,size=32m' `
        --security-opt no-new-privileges `
        --mount $backupMount `
        --mount $sourceSecretMount `
        --entrypoint sh `
        $postgresImage `
        -ec (
            'cp /run/secrets/pgpass /tmp/pgpass; ' +
            'chmod 600 /tmp/pgpass; export PGPASSFILE=/tmp/pgpass; ' +
            'exec pg_dump --host godswar-postgres --port 5432 ' +
            '--username godswar --dbname godswar --format=custom ' +
            '--compress=9 --serializable-deferrable --no-owner ' +
            '--no-privileges --file /backup/godswar.dump')
    if ($LASTEXITCODE -ne 0) {
        throw 'The consistent source PostgreSQL dump failed.'
    }
    if (-not (Test-Path -LiteralPath $dumpPath -PathType Leaf) -or
        (Get-Item -LiteralPath $dumpPath).Length -le 0) {
        throw 'The source PostgreSQL dump is absent or empty.'
    }
    $activeLeasesAfterDump = [int](Invoke-PsqlScalar `
        'godswar-postgres' 'godswar' $leaseCountSql)
    if ($activeLeasesAfterDump -ne 0) {
        throw 'Source outbox acquired a lease during the clone window.'
    }
    $sourceMigrationsAfter = Invoke-PsqlScalar `
        'godswar-postgres' 'godswar' $migrationSql
    if ($sourceMigrationsAfter -cne $sourceMigrationsBefore) {
        throw 'Source migrations changed during the clone window.'
    }
    $sourceCountsAfter = Invoke-PsqlScalar `
        'godswar-postgres' 'godswar' $countSql
    $sourceChangedDuringDump =
        $sourceCountsAfter -cne $sourceCountsBefore
    $unknownEventsAfterDump = Invoke-PsqlScalar `
        'godswar-postgres' 'godswar' $unreviewedEventSql
    if (-not [string]::IsNullOrWhiteSpace($unknownEventsAfterDump)) {
        throw (
            'Source outbox added an unreviewed event during the clone: ' +
            $unknownEventsAfterDump)
    }

    $devPasswordPath = Get-DotEnvValue `
        $environmentPath 'GODSWAR_DEV_POSTGRES_PASSWORD_FILE'
    $devPassword = Read-DevelopmentSecretFile $devPasswordPath
    Write-RestrictedSecret $targetPasswordPath `
        "godswar-dev-postgres:5432:*:godswar:$devPassword"
    $devPassword = $null
    $devSecretMount =
        "type=bind,source=$targetPasswordPath," +
        'target=/run/secrets/pgpass,readonly'

    & docker run --rm `
        --network reborn_dev_runtime `
        --read-only `
        --tmpfs '/tmp:rw,noexec,nosuid,nodev,size=16m' `
        --security-opt no-new-privileges `
        --mount $devSecretMount `
        --entrypoint sh `
        $postgresImage `
        -ec (
            'cp /run/secrets/pgpass /tmp/pgpass; ' +
            'chmod 600 /tmp/pgpass; export PGPASSFILE=/tmp/pgpass; ' +
            'exec psql --host godswar-dev-postgres --port 5432 ' +
            '--username godswar --dbname godswar --tuples-only ' +
            '--no-align --set ON_ERROR_STOP=1 --command "select 1"') `
        | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Development PostgreSQL restore credential validation failed.'
    }

    Assert-DevelopmentPostgresIdentity ([string]$target.Id) | Out-Null
    & docker exec godswar-dev-postgres createdb `
        --username godswar --owner godswar $stagingDatabase
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the isolated staging database.'
    }
    $stagingExists = $true

    & docker run --rm `
        --network reborn_dev_runtime `
        --read-only `
        --tmpfs '/tmp:rw,noexec,nosuid,nodev,size=32m' `
        --security-opt no-new-privileges `
        --mount $backupMount `
        --mount $devSecretMount `
        --entrypoint sh `
        $postgresImage `
        -ec (
            'cp /run/secrets/pgpass /tmp/pgpass; ' +
            'chmod 600 /tmp/pgpass; export PGPASSFILE=/tmp/pgpass; ' +
            'exec pg_restore --host godswar-dev-postgres --port 5432 ' +
            '--username godswar --dbname ' + $stagingDatabase + ' ' +
            '--exit-on-error ' +
            '--single-transaction --no-owner --no-privileges ' +
            '/backup/godswar.dump') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Restoring the isolated development PostgreSQL clone failed.'
    }

    $targetActiveLeases = [int](Invoke-PsqlScalar `
        'godswar-dev-postgres' $stagingDatabase $leaseCountSql)
    if ($targetActiveLeases -ne 0) {
        throw 'Restored development outbox contains an active lease.'
    }
    $targetUnknownEvents = Invoke-PsqlScalar `
        'godswar-dev-postgres' $stagingDatabase $unreviewedEventSql
    if (-not [string]::IsNullOrWhiteSpace($targetUnknownEvents)) {
        throw (
            'Restored outbox contains an unreviewed event type: ' +
            $targetUnknownEvents)
    }
    $targetMigrations = Invoke-PsqlScalar `
        'godswar-dev-postgres' $stagingDatabase $migrationSql
    if ($sourceMigrationsBefore -cne $targetMigrations) {
        throw 'Development migration IDs/checksums differ from the source.'
    }
    $targetCounts = Invoke-PsqlScalar `
        'godswar-dev-postgres' $stagingDatabase $countSql
    if (-not $sourceChangedDuringDump -and
        $sourceCountsBefore -cne $targetCounts) {
        throw 'Development account/character/item counts differ from source.'
    }

    $devServer = & docker ps -q --filter 'name=^/godswar-dev-server$'
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not verify the development server state.'
    }
    if (-not [string]::IsNullOrWhiteSpace(($devServer -join ''))) {
        Assert-DevelopmentContainer `
            'godswar-dev-server' 'server' | Out-Null
        & docker stop --time 45 godswar-dev-server | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not stop the development server before replacement.'
        }
    }

    Assert-DevelopmentPostgresIdentity ([string]$target.Id) | Out-Null

    & docker exec godswar-dev-postgres psql `
        --username godswar --dbname postgres --set ON_ERROR_STOP=1 `
        --command 'ALTER DATABASE godswar WITH ALLOW_CONNECTIONS false;' `
        | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not quiesce the previous development database.'
    }
    & docker exec godswar-dev-postgres psql `
        --username godswar --dbname postgres --set ON_ERROR_STOP=1 `
        --command (
            "select pg_terminate_backend(pid) from pg_stat_activity " +
            "where datname = 'godswar' and pid <> pg_backend_pid();") `
        | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & docker exec godswar-dev-postgres psql `
            --username godswar --dbname postgres `
            --command 'ALTER DATABASE godswar WITH ALLOW_CONNECTIONS true;' `
            | Out-Null
        throw 'Could not quiesce development database connections.'
    }
    & docker exec godswar-dev-postgres psql `
        --username godswar --dbname postgres --set ON_ERROR_STOP=1 `
        --command "ALTER DATABASE godswar RENAME TO $backupDatabase;" `
        | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & docker exec godswar-dev-postgres psql `
            --username godswar --dbname postgres `
            --command 'ALTER DATABASE godswar WITH ALLOW_CONNECTIONS true;' `
            | Out-Null
        throw 'Could not preserve the previous development database.'
    }
    $backupExists = $true

    & docker exec godswar-dev-postgres psql `
        --username godswar --dbname postgres --set ON_ERROR_STOP=1 `
        --command "ALTER DATABASE $stagingDatabase RENAME TO godswar;" `
        | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & docker exec godswar-dev-postgres psql `
            --username godswar --dbname postgres --set ON_ERROR_STOP=1 `
            --command "ALTER DATABASE $backupDatabase RENAME TO godswar;" `
            | Out-Null
        $rollbackExitCode = $LASTEXITCODE
        if ($rollbackExitCode -eq 0) {
            $backupExists = $false
            & docker exec godswar-dev-postgres psql `
                --username godswar --dbname postgres `
                --command 'ALTER DATABASE godswar WITH ALLOW_CONNECTIONS true;' `
                | Out-Null
        }
        if ($rollbackExitCode -ne 0) {
            throw (
                'Clone promotion and automatic previous-database restore failed; ' +
                "preserved database is '$backupDatabase'.")
        }
        throw 'Clone promotion failed; the previous database was restored.'
    }
    $stagingExists = $false

    & docker exec godswar-dev-postgres dropdb `
        --force --username godswar $backupDatabase
    if ($LASTEXITCODE -ne 0) {
        throw "Promoted clone is live, but old database '$backupDatabase' remains."
    }
    $backupExists = $false

    $dump = Get-Item -LiteralPath $dumpPath
    $dumpHash = (Get-FileHash $dumpPath -Algorithm SHA256).Hash
    $dumpLength = [long]$dump.Length
    $counts = $targetCounts.Split('|')
    $sourceBeforeCounts = $sourceCountsBefore.Split('|')
    $sourceAfterCounts = $sourceCountsAfter.Split('|')
    $migrationLines = @($targetMigrations -split "`n")
    $receipt = [ordered]@{
        schemaVersion = 'reborn.development-database-clone.v2'
        clonedAtUtc = [DateTimeOffset]::UtcNow.UtcDateTime.ToString('O')
        source = [ordered]@{
            containerId = [string]$source.Id
            volume = [string]$sourceVolume[0].Name
            database = 'godswar'
            changedDuringDump = $sourceChangedDuringDump
            rowCountsBefore = $sourceBeforeCounts
            rowCountsAfter = $sourceAfterCounts
        }
        target = [ordered]@{
            containerId = [string]$target.Id
            volume = [string]$targetVolume[0].Name
            database = 'godswar'
        }
        dump = [ordered]@{
            sha256 = $dumpHash
            bytes = $dumpLength
            retained = $false
        }
        migrations = [ordered]@{
            count = $migrationLines.Count
            orderedSha256 = Get-TextSha256 $targetMigrations
            head = ($migrationLines[-1] -split '\|')[0]
        }
        rowCounts = [ordered]@{
            accounts = [long]$counts[0]
            characters = [long]$counts[1]
            characterItems = [long]$counts[2]
        }
        activeOutboxLeases = 0
        b20hContainerIdentitiesPreserved = $true
    }
}
finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    if ($stagingExists) {
        & docker exec godswar-dev-postgres dropdb `
            --if-exists --force --username godswar $stagingDatabase `
            | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $cleanupErrors.Add(
                "Could not remove staging database '$stagingDatabase'.")
        }
    }
    if (Test-Path -LiteralPath $workDirectory -PathType Container) {
        try {
            $resolvedForDelete = [IO.Path]::GetFullPath($workDirectory)
            if (-not $resolvedForDelete.StartsWith(
                    $workPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to remove an unbounded clone workspace.'
            }
            Remove-Item -LiteralPath $resolvedForDelete -Recurse -Force
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    if ($cleanupErrors.Count -ne 0) {
        throw ('Development clone cleanup failed: ' +
            ($cleanupErrors -join ' | '))
    }
}

if (Test-Path -LiteralPath $workDirectory) {
    throw 'Clone workspace cleanup did not complete.'
}
if ($null -eq $receipt) {
    throw 'Verified clone receipt data was not produced.'
}
Assert-MainObservationGuardUnchanged $mainGuard | Out-Null
$receiptDirectory = Protect-DevelopmentPrivateDirectory (
    Join-Path $configurationRoot 'clone-receipts')
$receiptPath = Join-Path $receiptDirectory (
    [DateTimeOffset]::UtcNow.UtcDateTime.ToString('yyyyMMddTHHmmssfffZ') +
    '-database-clone.json')
[IO.File]::WriteAllText(
    $receiptPath,
    ($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Status = 'cloned_and_verified'
    Receipt = $receiptPath
    MigrationCount = $receipt.migrations.count
    MigrationHead = $receipt.migrations.head
    Accounts = $receipt.rowCounts.accounts
    Characters = $receipt.rowCounts.characters
    CharacterItems = $receipt.rowCounts.characterItems
    SourceChangedDuringDump = $receipt.source.changedDuringDump
    TemporaryDumpRetained = $false
}
