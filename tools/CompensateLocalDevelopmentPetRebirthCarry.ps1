[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',
    [string]$Database = 'godswar',
    [switch]$DisposableTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# One-purpose repair for the four isolated-development rebirths that carried
# historical surplus but dropped their already-unspent EXP pools.
$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-server'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$compensation = 13493595L
$targetExperience = 20545775L
$family = 'pet_rebirth_carry_compensation'
$operationText = 'localdev|pet-rebirth-carry|audits:631-634|pet:1|v1'
$requestText = $operationText + '|delta:13493595|target:20545775|rev:1005'
. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')

if ($DisposableTest) {
    if ($Database -notmatch '^godswar_rebirth_carry_[a-f0-9]{8,12}$') {
        throw 'Disposable tests require the rebirth-carry database prefix.'
    }
}
elseif ($Database -ne 'godswar') {
    throw 'The real compensation can target only the godswar database.'
}

function Invoke-Psql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Pet rebirth carry compensation failed:`n$($lines -join "`n")"
    }
    $receipt = $lines |
        Where-Object { $_.StartsWith($Marker) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no carry-compensation receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

$environmentState = Initialize-RebirthRepairEnvironment `
    $postgresContainer $serverContainer $redisContainer
$serverRunning = [bool]$environmentState.Server.State.Running
$redisKeyCount = Get-RebirthRepairRedisKeyCount $redisContainer
$originRunning = Test-OriginRunning
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText

$evidenceSql = @'
WITH expected(audit_id, old_level, old_experience, old_rebirths,
              required_level, historical_surplus) AS (VALUES
    (631::bigint, 118, 1239780::bigint, 1, 80, 145673550::bigint),
    (632::bigint, 101, 2646155::bigint, 2, 100, 3838650::bigint),
    (633::bigint, 120, 890830::bigint, 3, 110, 51664650::bigint),
    (634::bigint, 120, 8716830::bigint, 4, 120, 0::bigint)
), source_evidence AS (
    SELECT count(*) AS source_count,
           COALESCE(bool_and(
               audit.user_id_snapshot = 2
               AND audit.pet_id_snapshot = 1
               AND audit.operation = 'rebirth'
               AND audit.outcome = 'committed'
               AND (audit.before_state->>'Level')::integer = old_level
               AND (audit.before_state->>'Experience')::bigint = old_experience
               AND (audit.before_state->>'CompletedRebirths')::integer =
                   old_rebirths
               AND (audit.after_state->>'required_level')::integer =
                   required_level
               AND (audit.after_state->>'carried_experience')::bigint =
                   historical_surplus
               AND (audit.after_state->>'Experience')::bigint =
                   historical_surplus), false) AS source_valid,
           sum(old_experience)::bigint AS lost_experience
    FROM expected
    LEFT JOIN public.pet_operation_audit audit
      ON audit.id = expected.audit_id
), repair_audit AS (
    SELECT * FROM public.command_audit
    WHERE principal_type = 'developer' AND principal_key = '13'
      AND aggregate_type = 'pet' AND aggregate_key = '1'
      AND command_family = '__FAMILY__'
      AND operation_id = decode('__OPERATION_HEX__', 'hex')
), pet AS (
    SELECT * FROM public.character_pets WHERE id = 1
)
'@

$statusSql = @'
BEGIN TRANSACTION READ ONLY;
__EVIDENCE_SQL__
SELECT 'PET_REBIRTH_CARRY_STATUS|' || jsonb_build_object(
    'offlineDatabaseValid', EXISTS (
        SELECT 1 FROM public.accounts account
        JOIN public.character_base character_row
          ON character_row.account_id = account.id
        WHERE account.id = 13 AND account.username = 'test2'
          AND account.login_status = 0 AND character_row.id = 2
          AND character_row.name = 'test2'
          AND character_row.lifecycle_state = 'active'
          AND character_row.checkpoint_owner_id IS NULL),
    'sourceCount', (SELECT source_count FROM source_evidence),
    'sourceValid', (SELECT source_valid FROM source_evidence),
    'lostExperience', (SELECT lost_experience FROM source_evidence),
    'pendingPreviewCount', (SELECT count(*)
        FROM public.character_pet_growth_previews
        WHERE user_id = 2 AND pet_id = 1),
    'petReadyValid', EXISTS (SELECT 1 FROM pet
        WHERE user_id = 2 AND level = 120 AND experience = 7052180
          AND completed_rebirths = 5 AND rebirths_remaining = 6
          AND revision = 1004 AND activity_state = 'owned'
          AND is_carried AND is_summoned AND NOT contributes_to_character
          AND updated_at = '2026-08-13 09:13:45.198302+00'::timestamptz),
    'statsReadyValid', 6 = (SELECT count(*)
        FROM public.character_pet_stat_values
        WHERE pet_id = 1 AND stat_code BETWEEN 1 AND 6
          AND added_savvy = (base_growth_rate + growth_acceleration) * 120
          AND revision = 778),
    'petLevel', (SELECT level FROM pet),
    'petExperience', (SELECT experience FROM pet),
    'petRevision', (SELECT revision FROM pet),
    'repairCount', (SELECT count(*) FROM repair_audit),
    'repairAuditId', (SELECT id FROM repair_audit),
    'repairValid', COALESCE((SELECT
        request_hash = decode('__REQUEST_HASH_HEX__', 'hex')
        AND outcome_code = 'compensated'
        AND (detail_payload->>'compensation')::bigint = 13493595
        AND (detail_payload->>'previousExperience')::bigint = 7052180
        AND (detail_payload->>'currentExperience')::bigint = 20545775
        AND (detail_payload->>'previousPetRevision')::bigint = 1004
        AND (detail_payload->>'currentPetRevision')::bigint = 1005
        AND EXISTS (SELECT 1 FROM pet WHERE user_id = 2 AND level = 120
            AND experience = 20545775 AND revision = 1005)
        FROM repair_audit), false)
)::text FROM source_evidence;
COMMIT;
'@
$statusSql = $statusSql.Replace('__EVIDENCE_SQL__', $evidenceSql).
    Replace('__FAMILY__', $family).
    Replace('__OPERATION_HEX__', $operationHex).
    Replace('__REQUEST_HASH_HEX__', $requestHashHex)
$status = Invoke-Psql $statusSql 'PET_REBIRTH_CARRY_STATUS|'

if ($status.repairCount -gt 1 -or
    ($status.repairCount -eq 1 -and -not $status.repairValid)) {
    throw 'Existing rebirth carry compensation evidence is inconsistent.'
}
$ready = $status.offlineDatabaseValid -and $status.sourceCount -eq 4 -and
    $status.sourceValid -and $status.lostExperience -eq $compensation -and
    $status.pendingPreviewCount -eq 0 -and $status.petReadyValid -and
    $status.statsReadyValid
$offline = -not $serverRunning -and $redisKeyCount -eq 0 -and
    -not $originRunning
$state = if ($status.repairCount -eq 1) { 'Applied' }
    elseif ($ready -and ($DisposableTest -or $offline)) { 'Ready' }
    elseif ($ready) { 'AwaitingOffline' }
    else { 'Refused' }
$summary = [pscustomobject]@{
    Status = $state
    Database = $Database
    PetId = 1
    CurrentLevel = $status.petLevel
    CurrentExperience = $status.petExperience
    TargetExperience = $targetExperience
    CurrentPetRevision = $status.petRevision
    SourceAuditIds = '631,632,633,634'
    Compensation = $compensation
    CompensationAuditId = $status.repairAuditId
    ServerRunning = $serverRunning
    OriginRunning = $originRunning
    RedisPlayerLoginKeyCount = $redisKeyCount
    OperationIdSha256 = $operationHex.ToUpperInvariant()
    RequestHashSha256 = $requestHashHex.ToUpperInvariant()
}
if ($Mode -eq 'Status') { return $summary }
if ($state -eq 'Applied') { return $summary }
if ($state -ne 'Ready') {
    throw 'Exact audits 631-634 or current pet state no longer match.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environmentState $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        "isolated-development $Database pet 1",
        "Add exact $compensation carried EXP and permanent evidence")) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environmentState $redisContainer
}

$applySql = @'
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
CREATE TEMP TABLE repair_result (
    changed boolean NOT NULL, previous_experience bigint NOT NULL,
    current_experience bigint NOT NULL, previous_revision bigint NOT NULL,
    current_revision bigint NOT NULL, audit_id bigint NOT NULL
);
DO $repair$
DECLARE
    v_pet public.character_pets%ROWTYPE;
    v_audit_id bigint;
    v_source_count integer;
    v_source_valid boolean;
    v_lost_experience bigint;
BEGIN
    SELECT * INTO STRICT v_pet FROM public.character_pets
    WHERE id = 1 FOR UPDATE;
    PERFORM 1 FROM public.accounts account
    JOIN public.character_base character_row
      ON character_row.account_id = account.id
    WHERE account.id = 13 AND account.username = 'test2'
      AND account.login_status = 0 AND character_row.id = 2
      AND character_row.name = 'test2'
      AND character_row.lifecycle_state = 'active'
      AND character_row.checkpoint_owner_id IS NULL
    FOR UPDATE OF account, character_row;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Account 13 / character 2 is online or checkpoint-owned.';
    END IF;

    IF EXISTS (SELECT 1 FROM public.command_audit
        WHERE principal_type = 'developer' AND principal_key = '13'
          AND aggregate_type = 'pet' AND aggregate_key = '1'
          AND command_family = '__FAMILY__'
          AND operation_id = decode('__OPERATION_HEX__', 'hex')) THEN
        SELECT id INTO STRICT v_audit_id FROM public.command_audit
        WHERE principal_type = 'developer' AND principal_key = '13'
          AND aggregate_type = 'pet' AND aggregate_key = '1'
          AND command_family = '__FAMILY__'
          AND operation_id = decode('__OPERATION_HEX__', 'hex')
          AND request_hash = decode('__REQUEST_HASH_HEX__', 'hex')
          AND outcome_code = 'compensated'
          AND v_pet.user_id = 2 AND v_pet.level = 120
          AND v_pet.experience = 20545775 AND v_pet.revision = 1005;
        INSERT INTO repair_result VALUES (
            false, v_pet.experience, v_pet.experience,
            v_pet.revision, v_pet.revision, v_audit_id);
        RETURN;
    END IF;

    IF v_pet.user_id <> 2 OR v_pet.level <> 120
       OR v_pet.experience <> 7052180 OR v_pet.completed_rebirths <> 5
       OR v_pet.rebirths_remaining <> 6 OR v_pet.revision <> 1004
       OR v_pet.activity_state <> 'owned' OR NOT v_pet.is_carried
       OR NOT v_pet.is_summoned OR v_pet.contributes_to_character
       OR v_pet.updated_at <>
          '2026-08-13 09:13:45.198302+00'::timestamptz THEN
        RAISE EXCEPTION 'Pet 1 is not the exact post-fifth-rebirth row.';
    END IF;
    IF EXISTS (SELECT 1 FROM public.character_pet_growth_previews
        WHERE user_id = 2 AND pet_id = 1)
       OR 6 <> (SELECT count(*) FROM public.character_pet_stat_values
        WHERE pet_id = 1 AND stat_code BETWEEN 1 AND 6
          AND added_savvy = (base_growth_rate + growth_acceleration) * 120
          AND revision = 778) THEN
        RAISE EXCEPTION 'Pending preview or pet stats do not match.';
    END IF;

    __EVIDENCE_SQL__
    SELECT source_count, source_valid, lost_experience
      INTO STRICT v_source_count, v_source_valid, v_lost_experience
      FROM source_evidence;
    IF v_source_count <> 4 OR NOT v_source_valid
       OR v_lost_experience <> 13493595 THEN
        RAISE EXCEPTION 'Pinned rebirth loss evidence does not match.';
    END IF;

    UPDATE public.character_pets
    SET experience = 20545775, revision = revision + 1,
        updated_at = transaction_timestamp()
    WHERE id = 1 AND user_id = 2 AND level = 120
      AND experience = 7052180 AND revision = 1004;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Pet 1 was not compensated exactly once.';
    END IF;
    INSERT INTO public.command_audit (
        principal_type, principal_key, aggregate_type, aggregate_key,
        command_family, operation_id, request_hash, outcome_code,
        detail_payload, retention_policy
    ) VALUES (
        'developer', '13', 'pet', '1', '__FAMILY__',
        decode('__OPERATION_HEX__', 'hex'),
        decode('__REQUEST_HASH_HEX__', 'hex'), 'compensated',
        jsonb_build_object(
            'source', 'offline_isolated_localdevelopment_compensation',
            'accountId', 13, 'characterId', 2, 'petId', 1,
            'sourcePetOperationAuditIds', jsonb_build_array(631,632,633,634),
            'droppedPools', jsonb_build_array(
                1239780,2646155,890830,8716830),
            'experiencePolicy', 'historical_surplus_plus_unspent_pool',
            'compensation', 13493595,
            'previousLevel', 120, 'currentLevel', 120,
            'previousExperience', 7052180,
            'currentExperience', 20545775,
            'previousPetRevision', 1004, 'currentPetRevision', 1005),
        'permanent'
    ) RETURNING id INTO v_audit_id;
    INSERT INTO repair_result VALUES (
        true, 7052180, 20545775, 1004, 1005, v_audit_id);
END
$repair$;
COMMIT;
SELECT 'PET_REBIRTH_CARRY_RESULT|' || jsonb_build_object(
    'status', CASE WHEN changed THEN 'Applied' ELSE 'AlreadyApplied' END,
    'petId', 1, 'petLevel', 120,
    'previousExperience', previous_experience,
    'currentExperience', current_experience,
    'previousPetRevision', previous_revision,
    'currentPetRevision', current_revision,
    'compensation', 13493595,
    'compensationAuditId', audit_id
)::text FROM repair_result;
'@
$applySql = $applySql.Replace('__EVIDENCE_SQL__', $evidenceSql).
    Replace('__FAMILY__', $family).
    Replace('__OPERATION_HEX__', $operationHex).
    Replace('__REQUEST_HASH_HEX__', $requestHashHex)
Invoke-Psql $applySql 'PET_REBIRTH_CARRY_RESULT|'
