function New-B19RandomHexSecret {
    $bytes = [byte[]]::new(32)
    $generator =
        [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
        return -join (
            $bytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $generator.Dispose()
    }
}

function Invoke-B19Docker {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Operation,

        [switch]$AllowFailure
    )

    $priorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& docker @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorPreference
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Disposable PostgreSQL operation '$Operation' failed."
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { "$_" })
    }
}

function Assert-B19OwnedContainerName {
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -in @('godswar-postgres', 'godswar-server') -or
        $Name -notmatch '^reborn-b19-[a-f0-9]{12}$') {
        throw "Refusing non-B19 container target '$Name'."
    }
}

function Assert-B19DatabaseName {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RunToken
    )

    $expectedPrefix = "godswar_b19_${RunToken}_"
    if ($Name -notin @(
            "${expectedPrefix}source",
            "${expectedPrefix}restored")) {
        throw "Refusing non-B19 database target '$Name'."
    }
}

function Wait-B19PostgresReady {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$PostgresUser
    )

    Assert-B19OwnedContainerName $ContainerName
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $probe = Invoke-B19Docker -AllowFailure `
            -Operation 'readiness probe' `
            -Arguments @(
                'exec',
                $ContainerName,
                'pg_isready',
                '--username', $PostgresUser,
                '--dbname', 'postgres',
                '--timeout', '1'
            )
        if ($probe.ExitCode -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    throw 'Disposable PostgreSQL did not become ready within 15 seconds.'
}

function Get-B19PublishedPort {
    param([Parameter(Mandatory)][string]$ContainerName)

    Assert-B19OwnedContainerName $ContainerName
    $result = Invoke-B19Docker `
        -Operation 'published loopback port discovery' `
        -Arguments @('port', $ContainerName, '5432/tcp')
    $bindings = @($result.Output | Where-Object {
        $_ -match '^(127\.0\.0\.1|\[::1\]):(\d+)$'
    })
    if ($bindings.Count -ne 1) {
        throw 'Disposable PostgreSQL does not have one loopback-only port.'
    }
    if ($bindings[0] -notmatch ':(\d+)$') {
        throw 'Could not parse the disposable PostgreSQL port.'
    }

    return [int]$Matches[1]
}

function Invoke-B19PostgresTool {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation,
        [switch]$AllowFailure
    )

    Assert-B19OwnedContainerName $ContainerName
    Invoke-B19Docker -AllowFailure:$AllowFailure `
        -Operation $Operation `
        -Arguments (
            @(
                'exec',
                '--env', "PGPASSWORD=$Password",
                $ContainerName
            ) + $Arguments)
}

function Invoke-B19Psql {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Sql
    )

    $result = Invoke-B19PostgresTool `
        -ContainerName $ContainerName `
        -Password $Password `
        -Operation "bounded query against $Database" `
        -Arguments @(
            'psql',
            '--no-psqlrc',
            '--set', 'ON_ERROR_STOP=1',
            '--username', $PostgresUser,
            '--dbname', $Database,
            '--tuples-only',
            '--no-align',
            '--command', $Sql
        )
    (($result.Output | ForEach-Object Trim) |
        Where-Object { $_ }) -join "`n"
}

function New-B19Database {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$RunToken
    )

    Assert-B19DatabaseName $Database $RunToken
    Invoke-B19Psql `
        $ContainerName $Password $PostgresUser 'postgres' `
        "CREATE DATABASE `"$Database`";" | Out-Null
}

function Remove-B19Database {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$RunToken
    )

    Assert-B19DatabaseName $Database $RunToken
    Invoke-B19Psql `
        $ContainerName $Password $PostgresUser 'postgres' `
        "DROP DATABASE IF EXISTS `"$Database`" WITH (FORCE);" |
        Out-Null
}

function New-B19ConnectionString {
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$Database
    )

    @(
        'Host=127.0.0.1'
        "Port=$Port"
        "Username=$PostgresUser"
        "Password=$Password"
        "Database=$Database"
        'Timeout=5'
        'Command Timeout=60'
        'Pooling=false'
        'SSL Mode=Disable'
        'Include Error Detail=false'
    ) -join ';'
}

function Invoke-B19ProtocolCheck {
    param(
        [Parameter(Mandatory)][string]$AssemblyPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ConnectionString
    )

    $priorConnection =
        $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING
    $priorPreference = $ErrorActionPreference
    $output = @()
    try {
        $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING =
            $ConnectionString
        $ErrorActionPreference = 'Continue'
        $output = @(& dotnet $AssemblyPath $Name 2>&1 |
            ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorPreference
        $env:GODSWAR_TEST_POSTGRES_CONNECTION_STRING =
            $priorConnection
    }

    $output | ForEach-Object { Write-Host $_ }
    $skipCount = @($output | Where-Object {
        $_ -match '^SKIP(?:\s|$)'
    }).Count
    if ($exitCode -ne 0 -or
        $skipCount -ne 0 -or
        @($output | Where-Object { $_ -eq "PASS $Name" }).Count -ne 1) {
        throw "Required B19 protocol check '$Name' did not pass exactly."
    }
}

function Get-B19MigrationState {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Database
    )

    $value = Invoke-B19Psql `
        $ContainerName $Password $PostgresUser $Database @'
SELECT count(*)::text || '|' || COALESCE(max(migration_id), '')
FROM public.schema_migrations;
'@
    $parts = $value.Split('|', 2)
    if ($parts.Length -ne 2) {
        throw "Could not parse migration state for '$Database'."
    }

    [ordered]@{
        count = [int]$parts[0]
        head = $parts[1]
    }
}

function Assert-B19CurrentMigrationState {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$State
    )

    if ($State.count -ne 55 -or
        $State.head -cne
            '20260803_054_elemental_class_suit_attributes') {
        throw (
            "B19 expected 55 migrations through " +
            "'20260803_054_elemental_class_suit_attributes'.")
    }
}

function Get-B19LogicalFingerprint {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$PostgresUser,
        [Parameter(Mandatory)][string]$Database
    )

    $canonical = Invoke-B19Psql `
        $ContainerName $Password $PostgresUser $Database @'
WITH logical_state AS (
    SELECT jsonb_build_object(
        'migrations', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY migration_id)
            FROM (
                SELECT
                    migration_id,
                    description,
                    btrim(checksum) AS checksum
                FROM public.schema_migrations
            ) row_data
        ), '[]'::jsonb),
        'baselines', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY character_id)
            FROM public.character_economy_baseline row_data
        ), '[]'::jsonb),
        'baselineItems', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data)
                ORDER BY character_id, item_instance_id)
            FROM public.character_inventory_baseline_items row_data
        ), '[]'::jsonb),
        'currencyLedger', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.character_currency_ledger row_data
        ), '[]'::jsonb),
        'inventoryLedger', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.character_inventory_ledger row_data
        ), '[]'::jsonb),
        'characters', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.character_base row_data
        ), '[]'::jsonb),
        'characterItems', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.character_items row_data
        ), '[]'::jsonb),
        'commandAudit', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.command_audit row_data
        ), '[]'::jsonb),
        'commandInbox', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.command_inbox row_data
        ), '[]'::jsonb),
        'outbox', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.outbox_events row_data
        ), '[]'::jsonb),
        'outboxPositions', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY
                    consumer_key,
                    aggregate_type,
                    aggregate_key)
            FROM public.outbox_consumer_positions row_data
        ), '[]'::jsonb),
        'monsterRewardSettlements', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY death_event_id)
            FROM public.monster_death_reward_settlements row_data
        ), '[]'::jsonb),
        'petStreamVersions', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY character_id)
            FROM public.pet_durable_stream_versions row_data
        ), '[]'::jsonb),
        'characterPets', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
            FROM public.character_pets row_data
        ), '[]'::jsonb),
        'npcContentRevisions', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision)
            FROM public.npc_content_revisions row_data
        ), '[]'::jsonb),
        'npcSpawnDefinitions', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision, map_id, object_id)
            FROM public.npc_spawn_definitions row_data
        ), '[]'::jsonb),
        'npcContentPublication', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY family)
            FROM public.npc_content_publication row_data
        ), '[]'::jsonb),
        'npcDialogueRevisions', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision)
            FROM public.npc_dialogue_revisions row_data
        ), '[]'::jsonb),
        'npcDialogueTexts', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision, npc_key)
            FROM public.npc_dialogue_texts row_data
        ), '[]'::jsonb),
        'npcDialogueProfiles', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision, profile_key)
            FROM public.npc_dialogue_profiles row_data
        ), '[]'::jsonb),
        'npcDialogueProfileEntries', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision, profile_key, menu_order)
            FROM public.npc_dialogue_profile_entries row_data
        ), '[]'::jsonb),
        'npcDialogueBindings', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY revision, npc_key)
            FROM public.npc_dialogue_bindings row_data
        ), '[]'::jsonb),
        'npcDialoguePublication', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(row_data)
                ORDER BY family)
            FROM public.npc_dialogue_publication row_data
        ), '[]'::jsonb),
        'walletReconciliation', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY character_id)
            FROM public.character_wallet_reconciliation row_data
        ), '[]'::jsonb),
        'inventoryReconciliation', COALESCE((
            SELECT jsonb_agg(to_jsonb(row_data) ORDER BY character_id)
            FROM public.character_inventory_reconciliation row_data
        ), '[]'::jsonb)
    )::text AS canonical
)
SELECT canonical FROM logical_state;
'@
    if ([string]::IsNullOrWhiteSpace($canonical)) {
        throw "Logical fingerprint input for '$Database' was empty."
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return -join (
            $algorithm.ComputeHash($bytes) |
                ForEach-Object { $_.ToString('X2') })
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $algorithm.Dispose()
    }
}

function Remove-B19OwnedContainer {
    param([Parameter(Mandatory)][string]$ContainerName)

    Assert-B19OwnedContainerName $ContainerName
    if (-not (Test-B19ExactContainerExists $ContainerName)) {
        return
    }

    $inspect = Invoke-B19Docker `
        -Operation 'owned-container label inspection' `
        -Arguments @(
            'inspect',
            '--format',
            '{{json .Config.Labels}}',
            $ContainerName
        )
    $labels = ($inspect.Output -join '') |
        ConvertFrom-Json -ErrorAction Stop
    if ([string]$labels.'com.reborn.test-scope' -cne
        'b19-postgres-recovery') {
        throw 'Refusing to remove a container without the B19 owner label.'
    }

    Invoke-B19Docker `
        -Operation 'owned disposable container removal' `
        -Arguments @('rm', '--force', '--volumes', $ContainerName) |
        Out-Null

    if (Test-B19ExactContainerExists $ContainerName) {
        throw 'The exact owned disposable container still exists after removal.'
    }
}

function Test-B19ExactContainerExists {
    param([Parameter(Mandatory)][string]$ContainerName)

    Assert-B19OwnedContainerName $ContainerName
    $list = Invoke-B19Docker `
        -Operation 'exact container presence verification' `
        -Arguments @(
            'ps',
            '--all',
            '--format',
            '{{.Names}}'
        )
    $matches = @(
        $list.Output |
            ForEach-Object { "$_".Trim() } |
            Where-Object { $_ -ceq $ContainerName }
    )
    if ($matches.Count -gt 1) {
        throw 'Docker returned duplicate exact container names.'
    }

    return $matches.Count -eq 1
}
