[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string]$CharacterName = 'test2',

    [ValidateRange(1, 8)]
    [int]$PetOrdinal = 2,

    [ValidateRange(1, [long]::MaxValue)]
    [long]$ExpectedPetId = 180,

    [ValidateRange(85, 104)]
    [int]$SavvyTotal = 95,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = 'godswar-dev-postgres',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = 'godswar-dev-server',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = 'godswar'
)

# Local-development fixture only. It reconciles one explicitly identified,
# offline pet to the current Basic-Savvy hatch policy. It never changes Growth
# Rate, level progression, inventory, skills, presence, or another pet.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required but was not found on PATH.'
}

Import-Module (
    Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1'
) -Force

$server = Assert-DevelopmentContainer $ServerContainer 'server'
if ([bool]$server.State.Running) {
    throw "Refusing the offline fixture while '$ServerContainer' is running."
}
$serverEnvironment = @($server.Config.Env)
if ($serverEnvironment -cnotcontains
    'GODSWAR_RUNTIME_PROFILE=LocalDevelopment') {
    throw "'$ServerContainer' is not a LocalDevelopment server."
}

$postgres = Assert-DevelopmentContainer $PostgresContainer 'postgres'
if (-not [bool]$postgres.State.Running) {
    throw "PostgreSQL '$PostgresContainer' is not running."
}
$health = $postgres.State.PSObject.Properties['Health']
if ($null -ne $health -and $null -ne $health.Value -and
    $health.Value.Status -cne 'healthy') {
    throw "PostgreSQL '$PostgresContainer' is not healthy."
}
$dataMounts = @($postgres.Mounts | Where-Object {
    $_.Destination -ceq '/var/lib/postgresql/data'
})
if ($dataMounts.Count -ne 1 -or
    $dataMounts[0].Name -cne 'godswar-dev-postgres-data') {
    throw 'Development PostgreSQL is not using its isolated data volume.'
}

$totalUnits = $SavvyTotal * 100
$baseUnits = [int][Math]::Floor($totalUnits / 6)
$remainder = $totalUnits % 6
$savvyValues = @(for ($index = 0; $index -lt 6; $index++) {
    ($baseUnits + $(if ($index -lt $remainder) { 1 } else { 0 })) / 100.0
})
$invariant = [Globalization.CultureInfo]::InvariantCulture
$savvySql = @($savvyValues | ForEach-Object {
    $_.ToString('0.00', $invariant)
})

$operationBytes = [Guid]::NewGuid().ToByteArray()
$operationHex = -join ($operationBytes | ForEach-Object {
    $_.ToString('x2')
})
$requestText = @(
    $AccountId,
    $CharacterName,
    $PetOrdinal,
    $ExpectedPetId,
    $SavvyTotal,
    ($savvySql -join ',')
) -join '|'
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $requestHash = $sha256.ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($requestText))
} finally {
    $sha256.Dispose()
}
$requestHashHex = -join ($requestHash | ForEach-Object {
    $_.ToString('x2')
})

$safeName = $CharacterName.Replace("'", "''")
$target = "account $AccountId / '$CharacterName' / pet #$PetOrdinal (ID $ExpectedPetId)"
if (-not $PSCmdlet.ShouldProcess(
        $target,
        "Reconcile Basic Savvy to current Calm policy (target total $SavvyTotal)")) {
    return
}

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE pet_hatch_fixture_context (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    pet_id bigint NOT NULL,
    aptitude smallint NOT NULL,
    old_pet_revision bigint NOT NULL,
    new_pet_revision bigint,
    old_baseline_total integer NOT NULL,
    new_baseline_total integer NOT NULL,
    changed boolean NOT NULL DEFAULT false,
    before_state jsonb,
    after_state jsonb,
    audit_id bigint
);

DO `$fixture`$
DECLARE
    v_character_id integer;
    v_pet_id bigint;
    v_aptitude smallint;
    v_pet_revision bigint;
    v_baseline integer;
    v_policy text;
    v_source text;
    v_growth_revealed boolean;
    v_content_revision text;
    v_minimum_savvy integer;
    v_maximum_savvy integer;
    v_before jsonb;
    v_after jsonb;
    v_changed boolean;
    v_audit_id bigint;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.schema_migrations
        WHERE migration_id = '20260811_070_pet_initial_savvy_policy_v3'
    ) OR NOT EXISTS (
        SELECT 1 FROM public.schema_migrations
        WHERE migration_id = '20260811_071_pet_phoenix_growth_activation'
    ) THEN
        RAISE EXCEPTION
            'Apply pet hatch-policy migrations 070 and 071 before this fixture';
    END IF;

    SELECT character_row.id
    INTO v_character_id
    FROM public.character_base character_row
    JOIN public.accounts account
      ON account.id = character_row.account_id
    WHERE character_row.account_id = $AccountId
      AND character_row.name = '$safeName'
      AND character_row.lifecycle_state = 'active'
      AND character_row.checkpoint_owner_id IS NULL
      AND account.login_status = 0
    FOR UPDATE OF character_row, account;
    IF NOT FOUND THEN
        RAISE EXCEPTION
            'Character % on account % is missing, online, or checkpoint-owned',
            '$safeName', $AccountId;
    END IF;

    SELECT pet.id,
           pet.aptitude,
           pet.revision,
           pet.initial_savvy_baseline_total,
           pet.initial_savvy_policy_version,
           pet.initial_savvy_source_version,
           pet.growth_revealed
    INTO v_pet_id,
         v_aptitude,
         v_pet_revision,
         v_baseline,
         v_policy,
         v_source,
         v_growth_revealed
    FROM public.character_pets pet
    JOIN (
        SELECT ranked.id
        FROM (
            SELECT owned.id,
                   row_number() OVER (ORDER BY owned.id) AS ordinal
            FROM public.character_pets owned
            WHERE owned.user_id = v_character_id
        ) ranked
        WHERE ranked.ordinal = $PetOrdinal
    ) target_pet ON target_pet.id = pet.id
    FOR UPDATE OF pet;
    IF NOT FOUND OR v_pet_id <> $ExpectedPetId THEN
        RAISE EXCEPTION
            'Pet ordinal % did not resolve to expected pet ID % (actual %)',
            $PetOrdinal, $ExpectedPetId, v_pet_id;
    END IF;
    IF v_aptitude <> 6 THEN
        RAISE EXCEPTION
            'Expected Calm aptitude 6 for pet %, found %',
            v_pet_id, v_aptitude;
    END IF;
    IF v_source <> 'savvy-plus-growth-v2' THEN
        RAISE EXCEPTION
            'Pet % has unsupported Savvy source %', v_pet_id, v_source;
    END IF;
    IF v_growth_revealed THEN
        RAISE EXCEPTION
            'Pet % already has quality Growth revealed; fixture expects an unrevealed hatch pet',
            v_pet_id;
    END IF;

    PERFORM 1
    FROM public.character_pet_stat_values stat
    WHERE stat.pet_id = v_pet_id
    ORDER BY stat.stat_code
    FOR UPDATE;
    IF (SELECT count(*) FROM public.character_pet_stat_values
        WHERE pet_id = v_pet_id AND stat_code BETWEEN 1 AND 6) <> 6
    THEN
        RAISE EXCEPTION 'Pet % does not have exactly six stat rows', v_pet_id;
    END IF;
    IF (SELECT sum(base_growth_rate)
        FROM public.character_pet_stat_values
        WHERE pet_id = v_pet_id) NOT BETWEEN 0.01 AND 0.10
    THEN
        RAISE EXCEPTION
            'Pet % does not have the required unrevealed Weak Growth total',
            v_pet_id;
    END IF;

    SELECT publication.revision,
           aptitude.minimum_initial_savvy,
           aptitude.maximum_initial_savvy
    INTO v_content_revision, v_minimum_savvy, v_maximum_savvy
    FROM public.pet_content_publication publication
    JOIN public.pet_content_revisions release
      ON release.revision = publication.revision
     AND release.sealed_at IS NOT NULL
     AND release.source = 'reviewed-pet-baseline-v4'
    JOIN public.pet_content_aptitude_definitions aptitude
      ON aptitude.revision = publication.revision
     AND aptitude.aptitude = v_aptitude
    WHERE publication.family = 'pets';
    IF v_content_revision IS NULL OR
       $SavvyTotal NOT BETWEEN v_minimum_savvy AND v_maximum_savvy
    THEN
        RAISE EXCEPTION
            'The sealed V4 Calm policy does not accept Savvy total %',
            $SavvyTotal;
    END IF;

    SELECT jsonb_build_object(
        'pet', to_jsonb(pet),
        'stats', jsonb_agg(to_jsonb(stat) ORDER BY stat.stat_code))
    INTO v_before
    FROM public.character_pets pet
    JOIN public.character_pet_stat_values stat ON stat.pet_id = pet.id
    WHERE pet.id = v_pet_id
    GROUP BY pet.id;

    IF v_policy = 'legacy-high-savvy-range-v1' THEN
        UPDATE public.character_pet_stat_values stat
        SET initial_savvy = desired.savvy +
                (stat.initial_savvy - stat.birth_initial_savvy),
            birth_initial_savvy = desired.savvy,
            rarity_added_savvy = desired.savvy,
            revision = stat.revision + 1
        FROM (VALUES
            (1::smallint, $($savvySql[0])::numeric),
            (2::smallint, $($savvySql[1])::numeric),
            (3::smallint, $($savvySql[2])::numeric),
            (4::smallint, $($savvySql[3])::numeric),
            (5::smallint, $($savvySql[4])::numeric),
            (6::smallint, $($savvySql[5])::numeric)
        ) desired(stat_code, savvy)
        WHERE stat.pet_id = v_pet_id
          AND stat.stat_code = desired.stat_code;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Pet % Savvy rows were not updated', v_pet_id;
        END IF;

        UPDATE public.character_pets pet
        SET initial_savvy_baseline_total = $SavvyTotal,
            rarity_added_savvy_baseline_total = $SavvyTotal,
            initial_savvy_policy_version = 'project-v3',
            rarity_added_savvy_policy_version = 'project-v3',
            revision = pet.revision + 1,
            updated_at = transaction_timestamp()
        WHERE pet.id = v_pet_id
          AND pet.revision = v_pet_revision;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Pet % revision changed during fixture', v_pet_id;
        END IF;
        v_changed := true;
    ELSIF v_policy = 'project-v3' AND
          v_baseline BETWEEN v_minimum_savvy AND v_maximum_savvy THEN
        v_changed := false;
    ELSE
        RAISE EXCEPTION
            'Pet % has unsupported Basic-Savvy policy % / total %',
            v_pet_id, v_policy, v_baseline;
    END IF;

    SELECT jsonb_build_object(
        'pet', to_jsonb(pet),
        'stats', jsonb_agg(to_jsonb(stat) ORDER BY stat.stat_code))
    INTO v_after
    FROM public.character_pets pet
    JOIN public.character_pet_stat_values stat ON stat.pet_id = pet.id
    WHERE pet.id = v_pet_id
    GROUP BY pet.id;

    IF (v_after #>> '{pet,initial_savvy_baseline_total}')::integer
            NOT BETWEEN v_minimum_savvy AND v_maximum_savvy OR
       (SELECT sum(birth_initial_savvy)
        FROM public.character_pet_stat_values
        WHERE pet_id = v_pet_id) IS DISTINCT FROM
            (v_after #>> '{pet,initial_savvy_baseline_total}')::numeric OR
       EXISTS (
           SELECT 1
           FROM jsonb_array_elements(v_before->'stats') old_stat
           JOIN jsonb_array_elements(v_after->'stats') new_stat
             ON new_stat->>'stat_code' = old_stat->>'stat_code'
           WHERE (new_stat->>'initial_savvy')::numeric -
                 (new_stat->>'birth_initial_savvy')::numeric IS DISTINCT FROM
                 (old_stat->>'initial_savvy')::numeric -
                 (old_stat->>'birth_initial_savvy')::numeric
       )
    THEN
        RAISE EXCEPTION
            'Pet % Basic-Savvy reconciliation failed parity validation',
            v_pet_id;
    END IF;

    INSERT INTO public.command_audit (
        principal_type, principal_key,
        aggregate_type, aggregate_key,
        command_family, operation_id, request_hash,
        outcome_code, detail_payload, retention_policy
    ) VALUES (
        'developer', $AccountId::text,
        'pet', v_pet_id::text,
        'pet_hatch_baseline_fixture',
        decode('$operationHex', 'hex'),
        decode('$requestHashHex', 'hex'),
        CASE WHEN v_changed THEN 'updated' ELSE 'unchanged' END,
        jsonb_build_object(
            'source', 'offline_localdevelopment_fixture',
            'characterName', '$safeName',
            'petOrdinal', $PetOrdinal,
            'petContentRevision', v_content_revision,
            'requestedSavvyTotal', $SavvyTotal,
            'changed', v_changed,
            'before', v_before,
            'after', v_after),
        'permanent'
    ) RETURNING id INTO v_audit_id;

    INSERT INTO pet_hatch_fixture_context (
        account_id, character_id, character_name, pet_id, aptitude,
        old_pet_revision, new_pet_revision,
        old_baseline_total, new_baseline_total,
        changed, before_state, after_state, audit_id)
    VALUES (
        $AccountId, v_character_id, '$safeName', v_pet_id, v_aptitude,
        v_pet_revision, (v_after #>> '{pet,revision}')::bigint,
        v_baseline,
        (v_after #>> '{pet,initial_savvy_baseline_total}')::integer,
        v_changed, v_before, v_after, v_audit_id);
END
`$fixture`$;

COMMIT;

SELECT 'PET_HATCH_BASELINE_FIXTURE_RESULT|' || jsonb_build_object(
    'accountId', account_id,
    'characterId', character_id,
    'characterName', character_name,
    'petOrdinal', $PetOrdinal,
    'petId', pet_id,
    'aptitude', aptitude,
    'previousSavvyTotal', old_baseline_total,
    'currentSavvyTotal', new_baseline_total,
    'petRevisionBefore', old_pet_revision,
    'petRevisionAfter', new_pet_revision,
    'changed', changed,
    'auditId', audit_id)::text
FROM pet_hatch_fixture_context;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw (
        "Pet hatch-baseline fixture failed and rolled back:`n" +
        ($lines -join "`n"))
}
$prefix = 'PET_HATCH_BASELINE_FIXTURE_RESULT|'
$result = $lines | Where-Object {
    $_.StartsWith($prefix)
} | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw 'The pet hatch-baseline fixture returned no verification receipt.'
}
$result.Substring($prefix.Length) | ConvertFrom-Json
