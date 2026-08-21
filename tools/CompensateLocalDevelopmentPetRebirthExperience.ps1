[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This is intentionally not a general pet editor. It compensates only the
# incorrect audit-8023 refund after its exact 90-level spend trail.
$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$wrongRefund = 127824945L
$correctRefund = 242980800L
$compensation = 115155855L
$family = 'pet_rebirth_experience_compensation'
$operationText = 'localdev|pet-rebirth-exp|audit:629|pet:1|v2'
$requestText = $operationText + '|spent:127824945|delta:115155855'
. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')

if ($DisposableTest) {
    if ($Database -notmatch
        '^godswar_b12_rebirth_compensation_[a-f0-9]{8,12}$') {
        throw 'Disposable tests require the exact B12 database prefix.'
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
        throw "Pet rebirth compensation failed:`n$($lines -join "`n")"
    }
    $receipt = $lines |
        Where-Object { $_.StartsWith($Marker) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no compensation receipt.'
    }
    $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

$environmentState = Initialize-RebirthRepairEnvironment `
    $postgresContainer $serverContainer $redisContainer
$redisKeyCount = Get-RebirthRepairRedisKeyCount $redisContainer
$originRunning = Test-OriginRunning
$operationHex = Get-RepairSha256Hex $operationText
$requestHashHex = Get-RepairSha256Hex $requestText

$evidenceSql = @'
WITH active_content AS (
    SELECT revision FROM public.pet_content_publication
    WHERE family = 'pets'
), costs AS (
    SELECT active_content.revision,
           count(*) FILTER (WHERE current_level BETWEEN 1 AND 90)
               AS wrong_count,
           sum(required_experience) FILTER (
               WHERE current_level BETWEEN 1 AND 90)::bigint AS wrong_refund,
           count(*) FILTER (WHERE current_level BETWEEN 30 AND 119)
               AS correct_count,
           sum(required_experience) FILTER (
               WHERE current_level BETWEEN 30 AND 119)::bigint
               AS correct_refund
    FROM active_content
    JOIN public.pet_content_experience_steps USING (revision)
    GROUP BY active_content.revision
), trace AS (
    SELECT audit.id AS audit_id, inbox.id AS inbox_id,
           event.id AS outbox_id, event.aggregate_version,
           audit.principal_type, audit.principal_key,
           audit.aggregate_type, audit.aggregate_key,
           audit.command_family, audit.outcome_code,
           inbox.result_code, inbox.result_payload, event.event_type,
           row_number() OVER (ORDER BY audit.id)::integer AS ordinal
    FROM public.command_audit audit
    JOIN public.command_inbox inbox ON inbox.audit_id = audit.id
    JOIN public.outbox_events event
      ON event.command_inbox_id = inbox.id
    WHERE audit.id BETWEEN 8031 AND 8120
), trace_evidence AS (
    SELECT count(*) AS trace_count,
           COALESCE(bool_and(
               audit_id = 8030 + ordinal
               AND inbox_id = 8017 + ordinal
               AND outbox_id = 8284 + ordinal
               AND aggregate_version = 676 + ordinal
               AND principal_type = 'account' AND principal_key = '13'
               AND aggregate_type = 'character_pet_value'
               AND aggregate_key = 'character:2'
               AND command_family = 'pet_level_upgrade'
               AND outcome_code = 'committed'
               AND result_code = 'pet_result'
               AND event_type = 'pet.level_upgraded'
               AND (result_payload->>'PetId')::bigint = 1
               AND (result_payload->>'PetLevel')::integer = ordinal + 1
               AND (result_payload->>'PetRevision')::bigint = 350 + ordinal
               AND (result_payload->>'PetExperience')::bigint =
                   127824945 - (SELECT sum(required_experience)::bigint
                     FROM public.pet_content_experience_steps step
                     JOIN active_content active ON active.revision = step.revision
                     WHERE step.current_level BETWEEN 1 AND trace.ordinal)
           ), false) AS trace_valid
    FROM trace
), compensation_audit AS (
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
SELECT 'PET_REBIRTH_COMPENSATION_STATUS|' || jsonb_build_object(
    'offlineDatabaseValid', EXISTS (
        SELECT 1 FROM public.accounts account
        JOIN public.character_base character_row
          ON character_row.account_id = account.id
        WHERE account.id = 13 AND account.username = 'test2'
          AND account.login_status = 0 AND character_row.id = 2
          AND character_row.name = 'test2'
          AND character_row.lifecycle_state = 'active'
          AND character_row.checkpoint_owner_id IS NULL),
    'sourceAuditValid', EXISTS (
        SELECT 1 FROM public.pet_operation_audit audit
        WHERE audit.id = 629 AND audit.user_id = 2 AND audit.pet_id = 1
          AND audit.operation = 'rebirth' AND audit.outcome = 'committed'
          AND (audit.before_state->>'Level')::integer = 120
          AND (audit.before_state->>'Experience')::bigint = 0
          AND (audit.after_state->>'Level')::integer = 1
          AND (audit.after_state->>'Experience')::bigint = 0),
    'wrongRepairValid', EXISTS (
        SELECT 1 FROM public.command_audit audit
        WHERE audit.id = 8023
          AND audit.command_family = 'pet_rebirth_experience_repair'
          AND audit.outcome_code = 'repaired'
          AND (audit.detail_payload->>'sourcePetOperationAuditId')::bigint = 629
          AND (audit.detail_payload->>'previousExperience')::bigint = 0
          AND (audit.detail_payload->>'currentExperience')::bigint = 127824945
          AND (audit.detail_payload->>'previousPetRevision')::bigint = 349
          AND (audit.detail_payload->>'currentPetRevision')::bigint = 350),
    'traceCount', (SELECT trace_count FROM trace_evidence),
    'traceValid', (SELECT trace_valid FROM trace_evidence),
    'laterPetCommandCount', (SELECT count(*) FROM public.command_audit
        WHERE id > 8120 AND aggregate_type = 'character_pet_value'
          AND aggregate_key = 'character:2'),
    'laterPetOperationCount', (SELECT count(*)
        FROM public.pet_operation_audit
        WHERE user_id = 2 AND pet_id = 1 AND id > 630),
    'pendingPreviewCount', (SELECT count(*)
        FROM public.character_pet_growth_previews
        WHERE user_id = 2 AND pet_id = 1),
    'petReadyValid', EXISTS (SELECT 1 FROM pet
        WHERE user_id = 2 AND level = 91 AND experience = 0
          AND completed_rebirths = 1 AND rebirths_remaining = 0
          AND revision = 440 AND activity_state = 'owned'
          AND is_carried AND is_summoned AND NOT contributes_to_character
          AND updated_at = '2026-08-13 07:50:05.90197+00'::timestamptz),
    'statsReadyValid', 6 = (SELECT count(*)
        FROM public.character_pet_stat_values
        WHERE pet_id = 1 AND stat_code BETWEEN 1 AND 6
          AND added_savvy = (base_growth_rate + growth_acceleration) * 91
          AND revision = 290),
    'petLevel', (SELECT level FROM pet),
    'petExperience', (SELECT experience FROM pet),
    'petRevision', (SELECT revision FROM pet),
    'activeContentRevision', (SELECT revision FROM costs),
    'wrongCount', (SELECT wrong_count FROM costs),
    'wrongRefund', (SELECT wrong_refund FROM costs),
    'correctCount', (SELECT correct_count FROM costs),
    'correctRefund', (SELECT correct_refund FROM costs),
    'compensationCount', (SELECT count(*) FROM compensation_audit),
    'compensationAuditId', (SELECT id FROM compensation_audit),
    'compensationValid', COALESCE((SELECT
        request_hash = decode('__REQUEST_HASH_HEX__', 'hex')
        AND outcome_code = 'compensated'
        AND (detail_payload->>'previousExperience')::bigint = 0
        AND (detail_payload->>'currentExperience')::bigint = 115155855
        AND (detail_payload->>'previousPetRevision')::bigint = 440
        AND (detail_payload->>'currentPetRevision')::bigint = 441
        AND EXISTS (SELECT 1 FROM pet WHERE user_id = 2 AND level = 91
            AND experience = 115155855 AND revision = 441)
        FROM compensation_audit), false)
)::text FROM costs;
COMMIT;
'@
$statusSql = $statusSql.Replace('__EVIDENCE_SQL__', $evidenceSql).
    Replace('__FAMILY__', $family).
    Replace('__OPERATION_HEX__', $operationHex).
    Replace('__REQUEST_HASH_HEX__', $requestHashHex)
$status = Invoke-Psql $statusSql 'PET_REBIRTH_COMPENSATION_STATUS|'

if ($status.compensationCount -gt 1 -or
    ($status.compensationCount -eq 1 -and -not $status.compensationValid)) {
    throw 'Existing rebirth compensation evidence is inconsistent.'
}
$ready = $status.offlineDatabaseValid -and $status.sourceAuditValid -and
    $status.wrongRepairValid -and $status.traceCount -eq 90 -and
    $status.traceValid -and $status.laterPetCommandCount -eq 0 -and
    $status.laterPetOperationCount -eq 0 -and
    $status.pendingPreviewCount -eq 0 -and $status.petReadyValid -and
    $status.statsReadyValid -and $status.wrongCount -eq 90 -and
    $status.wrongRefund -eq $wrongRefund -and
    $status.correctCount -eq 90 -and
    $status.correctRefund -eq $correctRefund
$state = if ($status.compensationCount -eq 1) { 'Applied' }
    elseif ($ready -and ($DisposableTest -or
        ($redisKeyCount -eq 0 -and -not $originRunning))) { 'Ready' }
    else { 'Refused' }
$summary = [pscustomobject]@{
    Status = $state
    Database = $Database
    PetId = 1
    PetLevel = $status.petLevel
    CurrentExperience = $status.petExperience
    TargetExperience = $compensation
    CurrentPetRevision = $status.petRevision
    WrongRefund = $status.wrongRefund
    CorrectRefund = $status.correctRefund
    Compensation = $compensation
    LevelUpgradeTraceCount = $status.traceCount
    CompensationAuditId = $status.compensationAuditId
    OriginRunning = $originRunning
    RedisPlayerLoginKeyCount = $redisKeyCount
    OperationIdSha256 = $operationHex.ToUpperInvariant()
    RequestHashSha256 = $requestHashHex.ToUpperInvariant()
}
if ($Mode -eq 'Status') { return $summary }
if ($state -eq 'Applied') { return $summary }
if ($state -ne 'Ready') {
    throw 'Exact audit 629 / repair 8023 / 90-level spend trail no longer matches.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environmentState $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        "isolated-development $Database pet 1",
        "Add the exact $compensation EXP correction and permanent evidence")) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environmentState $redisContainer
}

$applySql = @'
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
CREATE TEMP TABLE compensation_result (
    changed boolean NOT NULL, previous_experience bigint NOT NULL,
    current_experience bigint NOT NULL, previous_revision bigint NOT NULL,
    current_revision bigint NOT NULL, audit_id bigint NOT NULL
);
DO $compensation$
DECLARE
    v_pet public.character_pets%ROWTYPE;
    v_audit_id bigint;
    v_wrong_refund bigint;
    v_correct_refund bigint;
    v_trace_count integer;
    v_trace_valid boolean;
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
          AND v_pet.user_id = 2 AND v_pet.level = 91
          AND v_pet.experience = 115155855 AND v_pet.revision = 441;
        INSERT INTO compensation_result VALUES (
            false, v_pet.experience, v_pet.experience,
            v_pet.revision, v_pet.revision, v_audit_id);
        RETURN;
    END IF;

    IF v_pet.user_id <> 2 OR v_pet.level <> 91 OR v_pet.experience <> 0
       OR v_pet.completed_rebirths <> 1 OR v_pet.rebirths_remaining <> 0
       OR v_pet.revision <> 440 OR v_pet.activity_state <> 'owned'
       OR NOT v_pet.is_carried OR NOT v_pet.is_summoned
       OR v_pet.contributes_to_character OR v_pet.updated_at <>
          '2026-08-13 07:50:05.90197+00'::timestamptz THEN
        RAISE EXCEPTION 'Pet 1 is not the exact post-spend row.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.pet_operation_audit
        WHERE id = 629 AND user_id = 2 AND pet_id = 1
          AND operation = 'rebirth' AND outcome = 'committed'
          AND (before_state->>'Level')::integer = 120
          AND (after_state->>'Level')::integer = 1)
       OR NOT EXISTS (SELECT 1 FROM public.command_audit
        WHERE id = 8023
          AND command_family = 'pet_rebirth_experience_repair'
          AND outcome_code = 'repaired'
          AND (detail_payload->>'currentExperience')::bigint = 127824945)
       OR EXISTS (SELECT 1 FROM public.command_audit
        WHERE id > 8120 AND aggregate_type = 'character_pet_value'
          AND aggregate_key = 'character:2')
       OR EXISTS (SELECT 1 FROM public.pet_operation_audit
        WHERE user_id = 2 AND pet_id = 1 AND id > 630)
       OR EXISTS (SELECT 1 FROM public.character_pet_growth_previews
        WHERE user_id = 2 AND pet_id = 1)
       OR 6 <> (SELECT count(*) FROM public.character_pet_stat_values
        WHERE pet_id = 1 AND stat_code BETWEEN 1 AND 6
          AND added_savvy = (base_growth_rate + growth_acceleration) * 91
          AND revision = 290) THEN
        RAISE EXCEPTION 'Source or later pet evidence does not match.';
    END IF;

    __EVIDENCE_SQL__
    SELECT wrong_refund, correct_refund, trace_count, trace_valid
      INTO STRICT v_wrong_refund, v_correct_refund,
          v_trace_count, v_trace_valid
      FROM costs CROSS JOIN trace_evidence;
    IF v_wrong_refund <> 127824945 OR v_correct_refund <> 242980800
       OR v_trace_count <> 90 OR NOT v_trace_valid THEN
        RAISE EXCEPTION 'Pinned EXP curve or exact spend trail does not match.';
    END IF;

    UPDATE public.character_pets
    SET experience = 115155855, revision = revision + 1,
        updated_at = transaction_timestamp()
    WHERE id = 1 AND user_id = 2 AND level = 91
      AND experience = 0 AND revision = 440;
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
            'sourcePetOperationAuditId', 629,
            'incorrectRepairCommandAuditId', 8023,
            'firstLevelUpgradeCommandAuditId', 8031,
            'lastLevelUpgradeCommandAuditId', 8120,
            'levelUpgradeCount', 90,
            'originalPetLevel', 120, 'requiredRebirthLevel', 30,
            'incorrectRefund', 127824945, 'correctRefund', 242980800,
            'compensation', 115155855,
            'experiencePolicy', 'historical_costs_levels_30_through_119',
            'previousLevel', 91, 'currentLevel', 91,
            'previousExperience', 0, 'currentExperience', 115155855,
            'previousPetRevision', 440, 'currentPetRevision', 441),
        'permanent'
    ) RETURNING id INTO v_audit_id;
    INSERT INTO compensation_result VALUES (
        true, 0, 115155855, 440, 441, v_audit_id);
END
$compensation$;
COMMIT;
SELECT 'PET_REBIRTH_COMPENSATION_RESULT|' || jsonb_build_object(
    'status', CASE WHEN changed THEN 'Applied' ELSE 'AlreadyApplied' END,
    'petId', 1, 'petLevel', 91,
    'previousExperience', previous_experience,
    'currentExperience', current_experience,
    'previousPetRevision', previous_revision,
    'currentPetRevision', current_revision,
    'compensation', 115155855, 'correctRefund', 242980800,
    'compensationAuditId', audit_id
)::text FROM compensation_result;
'@
$applySql = $applySql.Replace('__EVIDENCE_SQL__', $evidenceSql).
    Replace('__FAMILY__', $family).
    Replace('__OPERATION_HEX__', $operationHex).
    Replace('__REQUEST_HASH_HEX__', $requestHashHex)
Invoke-Psql $applySql 'PET_REBIRTH_COMPENSATION_RESULT|'
