function Invoke-Docker {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [string] $Description = 'Docker command'
    )

    $priorErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $commandOutput = @(& docker @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorErrorAction
    }
    if ($exitCode -ne 0) {
        $detail = ($commandOutput | ForEach-Object ToString) -join [Environment]::NewLine
        throw "$Description failed with exit code $exitCode. $detail"
    }

    return @($commandOutput | ForEach-Object ToString)
}

function Invoke-PostgresTool {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [string] $Description = 'PostgreSQL container command'
    )

    return Invoke-Docker `
        -Description $Description `
        -Arguments (@('exec', $ContainerId) + $Arguments)
}

function Invoke-Psql {
    param(
        [Parameter(Mandatory)]
        [string] $Database,

        [Parameter(Mandatory)]
        [string] $Sql
    )

    $output = Invoke-PostgresTool -Description "psql against $Database" -Arguments @(
        'psql',
        '--no-psqlrc',
        '--set', 'ON_ERROR_STOP=1',
        '--username', $PostgresUser,
        '--dbname', $Database,
        '--tuples-only',
        '--no-align',
        '--command', $Sql
    )
    return (($output | ForEach-Object Trim) | Where-Object { $_ }) -join "`n"
}

function Assert-DisposableDatabaseName {
    param([Parameter(Mandatory)][string] $Database)

    if ($Database -notmatch (
        '^godswar_b03_[a-f0-9]{10}_' +
        '(empty|lifecycle_preflight|prefix|restored|smoke_[0-9]{2})$')) {
        throw "Refusing non-B03 database name '$Database'."
    }
}

function New-DisposableDatabase {
    param([Parameter(Mandatory)][string] $Database)

    Assert-DisposableDatabaseName $Database
    Invoke-Psql -Database 'postgres' -Sql (
        'CREATE DATABASE "' + $Database + '";') | Out-Null
}

function New-DisposableDatabaseFromTemplate {
    param(
        [Parameter(Mandatory)]
        [string] $Database,

        [Parameter(Mandatory)]
        [string] $Template
    )

    Assert-DisposableDatabaseName $Database
    Assert-DisposableDatabaseName $Template
    Invoke-Psql -Database 'postgres' -Sql (
        'CREATE DATABASE "' + $Database +
        '" TEMPLATE "' + $Template + '";') | Out-Null
}

function Remove-DisposableDatabase {
    param([Parameter(Mandatory)][string] $Database)

    Assert-DisposableDatabaseName $Database
    Invoke-Psql -Database 'postgres' -Sql (
        'DROP DATABASE IF EXISTS "' + $Database + '" WITH (FORCE);') | Out-Null
}

function ConvertTo-ConnectionStringValue {
    param([Parameter(Mandatory)][string] $Value)

    return '"' + $Value.Replace('"', '""') + '"'
}

function New-TestConnectionString {
    param([Parameter(Mandatory)][string] $Database)

    Assert-DisposableDatabaseName $Database
    return @(
        'Host=' + (ConvertTo-ConnectionStringValue $PostgresHost)
        "Port=$PostgresPort"
        'Username=' + (ConvertTo-ConnectionStringValue $PostgresUser)
        'Password=' + (ConvertTo-ConnectionStringValue $postgresPassword)
        'Database=' + (ConvertTo-ConnectionStringValue $Database)
        'Timeout=5'
        'Command Timeout=60'
        'Pooling=false'
        'SSL Mode=Disable'
        'Include Error Detail=false'
    ) -join ';'
}

function Get-MigrationState {
    param([Parameter(Mandatory)][string] $Database)

    $value = Invoke-Psql -Database $Database -Sql @'
SELECT count(*)::text || '|' || COALESCE(max(migration_id), '')
FROM public.schema_migrations;
'@
    $parts = $value.Split('|', 2)
    if ($parts.Length -ne 2) {
        throw "Could not parse migration state for disposable database '$Database'."
    }

    return [ordered]@{
        count = [int]$parts[0]
        head = $parts[1]
    }
}

function Get-HistoricalFixtureFingerprint {
    param([Parameter(Mandatory)][string] $Database)

    return Invoke-Psql -Database $Database -Sql @'
SELECT
    (SELECT count(*) FROM public.accounts WHERE id = -903)::text || '|' ||
    (SELECT count(*) FROM public.character_base WHERE id = -903)::text || '|' ||
    (SELECT count(*) FROM public.character_items WHERE id = -903)::text || '|' ||
    (SELECT count(*) FROM public.packet_transactions WHERE id = -903)::text || '|' ||
    md5(
        COALESCE((
            SELECT to_jsonb(account_row)::text
            FROM public.accounts account_row
            WHERE id = -903
        ), 'missing') || '|' ||
        COALESCE((
            SELECT to_jsonb(character_row)::text
            FROM public.character_base character_row
            WHERE id = -903
        ), 'missing') || '|' ||
        COALESCE((
            SELECT to_jsonb(item_row)::text
            FROM public.character_items item_row
            WHERE id = -903
        ), 'missing') || '|' ||
        COALESCE((
            SELECT
                encode(clear_bytes, 'hex') || ':' ||
                encode(raw_bytes, 'hex')
            FROM public.packet_transactions
            WHERE id = -903
        ), 'missing')
    );
'@
}

function Invoke-RequiredProtocolCheck {
    param(
        [Parameter(Mandatory)]
        [string] $Phase,

        [Parameter(Mandatory)]
        [string] $Name,

        [string] $GeneralConnectionString,

        [string] $SchemaReleaseConnectionString,

        [string] $MigrationPrefix
    )

    $checkStarted = [Diagnostics.Stopwatch]::StartNew()
    $priorGeneral = $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING
    $priorRelease = $env:GODSWAR_TEST_SCHEMA_RELEASE_CONNECTION_STRING
    $priorPrefix = $env:GODSWAR_TEST_POSTGRES_MIGRATION_PREFIX
    $outputLines = @()
    $exitCode = -1

    try {
        $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING = $GeneralConnectionString
        $env:GODSWAR_TEST_SCHEMA_RELEASE_CONNECTION_STRING =
            $SchemaReleaseConnectionString
        $env:GODSWAR_TEST_POSTGRES_MIGRATION_PREFIX = $MigrationPrefix

        $priorErrorAction = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $outputLines = @(
                & dotnet $protocolChecksAssembly $Name 2>&1 |
                    ForEach-Object ToString
            )
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $priorErrorAction
        }
        $outputLines | ForEach-Object { Write-Host $_ }

        $skipLines = @($outputLines | Where-Object { $_ -match '^\s*SKIP(?:\s|$)' })
        $passLines = @($outputLines | Where-Object { $_ -eq "PASS $Name" })
        if ($exitCode -ne 0) {
            throw "Required check '$Name' exited with $exitCode."
        }
        if ($skipLines.Count -ne 0) {
            throw "Required check '$Name' emitted SKIP."
        }
        if ($passLines.Count -ne 1) {
            throw "Required check '$Name' did not emit its exact PASS receipt."
        }

        $checkResults.Add([ordered]@{
            phase = $Phase
            name = $Name
            status = 'passed'
            durationMs = [long]$checkStarted.Elapsed.TotalMilliseconds
            exitCode = $exitCode
            skipCount = 0
        })
    }
    catch {
        $skipCount = @(
            $outputLines | Where-Object { $_ -match '^\s*SKIP(?:\s|$)' }
        ).Count
        $checkResults.Add([ordered]@{
            phase = $Phase
            name = $Name
            status = 'failed'
            durationMs = [long]$checkStarted.Elapsed.TotalMilliseconds
            exitCode = $exitCode
            skipCount = $skipCount
        })
        throw
    }
    finally {
        $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING = $priorGeneral
        $env:GODSWAR_TEST_SCHEMA_RELEASE_CONNECTION_STRING = $priorRelease
        $env:GODSWAR_TEST_POSTGRES_MIGRATION_PREFIX = $priorPrefix
        $checkStarted.Stop()
    }
}

function Add-ScenarioResult {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][int] $InitialMigrationCount,
        [Parameter(Mandatory)][System.Collections.IDictionary] $FinalState,
        [Parameter(Mandatory)][long] $DurationMs,
        [string] $FixtureKind = 'none'
    )

    if ($FinalState.count -ne $report.expectedMigrationCount -or
        $FinalState.head -ne $report.expectedMigrationHead) {
        throw (
            "Scenario '$Name' reached $($FinalState.count) migrations through " +
            "'$($FinalState.head)', expected $($report.expectedMigrationCount) " +
            "through '$($report.expectedMigrationHead)'.")
    }

    $scenarioResults.Add([ordered]@{
        name = $Name
        status = 'passed'
        fixtureKind = $FixtureKind
        initialMigrationCount = $InitialMigrationCount
        finalMigrationCount = $FinalState.count
        finalMigrationHead = $FinalState.head
        durationMs = $DurationMs
    })
}
