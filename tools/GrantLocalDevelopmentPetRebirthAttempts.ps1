[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = 'godswar',

    [switch]$DisposableTest
)

# Exact, one-time local-development fixture for the current test2/Jolo pet.
# This is intentionally not a general pet editor. It changes only the pet's
# remaining rebirth allowance from zero to ten, advances its revision once,
# and appends immutable command-audit evidence. Inventory and pet progression
# are left untouched. The real target must be fully offline.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-server'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$family = 'pet_rebirth_attempt_fixture'
$sourceRevision = 468L
$targetRemaining = 10
$operationText = @(
    'localdev', 'pet-rebirth-attempts', 'account:13', 'character:2',
    'pet:1', "source-revision:$sourceRevision", "target:$targetRemaining", 'v1'
) -join '|'
$requestText = @(
    $operationText, 'name:Jolo', 'completed:1', 'remaining:0',
    'level:118', 'experience:1239780'
) -join '|'

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')

if ($DisposableTest) {
    if ($Database -notmatch
        '^godswar_pet_rebirth_attempts_[a-f0-9]{8,12}$') {
        throw 'Disposable tests require the pet-rebirth-attempts database prefix.'
    }
}
elseif ($Database -cne 'godswar') {
    throw 'The real fixture can target only the godswar database.'
}

function Invoke-Psql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $Database 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Pet rebirth-attempt fixture failed:`n$($lines -join "`n")"
    }
    $receipt = $lines |
        Where-Object { $_.StartsWith($Marker) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no rebirth-attempt receipt.'
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
WITH target AS (
    SELECT pet.*
    FROM public.character_pets pet
    WHERE pet.id = 1
), active_content AS (
    SELECT settings.maximum_rebirth_count
    FROM public.pet_content_publication publication
    JOIN public.pet_content_revisions revision
      ON revision.revision = publication.revision
     AND revision.sealed_at IS NOT NULL
    JOIN public.pet_content_settings settings
      ON settings.revision = publication.revision
    WHERE publication.family = 'pets'
), fixture_audit AS (
    SELECT audit.*
    FROM public.command_audit audit
    WHERE audit.principal_type = 'developer'
      AND audit.principal_key = '13'
      AND audit.aggregate_type = 'pet'
      AND audit.aggregate_key = '1'
      AND audit.command_family = '__FAMILY__'
      AND audit.operation_id = decode('__OPERATION_HEX__', 'hex')
), evidence AS (
    SELECT
        EXISTS (
            SELECT 1
            FROM public.accounts account
            JOIN public.character_base character_row
              ON character_row.account_id = account.id
            WHERE account.id = 13
              AND account.username = 'test2'
              AND account.login_status = 0
              AND character_row.id = 2
              AND character_row.name = 'test2'
              AND character_row.lifecycle_state = 'active'
              AND character_row.checkpoint_owner_id IS NULL
        ) AS owner_offline,
        EXISTS (
            SELECT 1 FROM target pet
            WHERE pet.user_id = 2
              AND pet.name = 'Jolo'
              AND pet.level = 118
              AND pet.experience = 1239780
              AND pet.completed_rebirths = 1
              AND pet.rebirths_remaining = 0
              AND pet.revision = 468
              AND pet.updated_at =
                  '2026-08-13 08:39:03.35139+00'::timestamptz
              AND pet.activity_state = 'owned'
              AND pet.is_carried
              AND pet.is_summoned
              AND NOT pet.contributes_to_character
              AND pet.has_soul_contract
        ) AS before_valid,
        EXISTS (
            SELECT 1 FROM target pet
            WHERE pet.user_id = 2
              AND pet.name = 'Jolo'
              AND pet.level = 118
              AND pet.experience = 1239780
              AND pet.completed_rebirths = 1
              AND pet.rebirths_remaining = 10
              AND pet.revision = 469
              AND pet.activity_state = 'owned'
              AND pet.is_carried
              AND pet.is_summoned
              AND NOT pet.contributes_to_character
              AND pet.has_soul_contract
        ) AS after_valid,
        EXISTS (
            SELECT 1 FROM public.command_audit audit
            WHERE audit.id = 8155
              AND audit.principal_type = 'account'
              AND audit.principal_key = '13'
              AND audit.aggregate_type = 'character_pet_value'
              AND audit.aggregate_key = 'character:2'
              AND audit.command_family = 'pet_level_upgrade'
              AND audit.outcome_code = 'committed'
        ) AS source_command_valid,
        (SELECT count(*) FROM public.command_audit audit
          WHERE audit.id > 8155
            AND audit.aggregate_type = 'character_pet_value'
            AND audit.aggregate_key = 'character:2') AS later_command_count,
        (SELECT count(*) FROM public.pet_operation_audit audit
          WHERE audit.user_id = 2 AND audit.pet_id = 1
            AND audit.id > 630) AS later_operation_count,
        (SELECT count(*) FROM public.character_pet_growth_previews preview
          WHERE preview.user_id = 2 AND preview.pet_id = 1)
            AS pending_preview_count,
        (SELECT count(*) FROM active_content) AS content_count,
        (SELECT maximum_rebirth_count FROM active_content)
            AS maximum_rebirth_count,
        (SELECT count(*) FROM fixture_audit) AS audit_count,
        COALESCE((
            SELECT audit.request_hash =
                       decode('__REQUEST_HASH_HEX__', 'hex')
               AND audit.outcome_code = 'granted'
               AND audit.retention_policy = 'permanent'
               AND (audit.detail_payload->>'accountId')::integer = 13
               AND (audit.detail_payload->>'characterId')::integer = 2
               AND (audit.detail_payload->>'petId')::bigint = 1
               AND (audit.detail_payload->>'previousRemaining')::integer = 0
               AND (audit.detail_payload->>'currentRemaining')::integer = 10
               AND (audit.detail_payload->>'previousPetRevision')::bigint = 468
               AND (audit.detail_payload->>'currentPetRevision')::bigint = 469
            FROM fixture_audit audit
        ), false) AS audit_valid
)
'@
$evidenceSql = $evidenceSql.
    Replace('__FAMILY__', $family).
    Replace('__OPERATION_HEX__', $operationHex).
    Replace('__REQUEST_HASH_HEX__', $requestHashHex)

$statusSql = @'
BEGIN TRANSACTION READ ONLY;
__EVIDENCE_SQL__
SELECT 'PET_REBIRTH_ATTEMPTS_STATUS|' || jsonb_build_object(
    'ownerOffline', evidence.owner_offline,
    'beforeValid', evidence.before_valid,
    'afterValid', evidence.after_valid,
    'sourceCommandValid', evidence.source_command_valid,
    'laterCommandCount', evidence.later_command_count,
    'laterOperationCount', evidence.later_operation_count,
    'pendingPreviewCount', evidence.pending_preview_count,
    'contentCount', evidence.content_count,
    'maximumRebirthCount', evidence.maximum_rebirth_count,
    'auditCount', evidence.audit_count,
    'auditValid', evidence.audit_valid,
    'petLevel', target.level,
    'petExperience', target.experience,
    'completedRebirths', target.completed_rebirths,
    'rebirthsRemaining', target.rebirths_remaining,
    'petRevision', target.revision,
    'auditId', (SELECT id FROM fixture_audit)
)::text
FROM evidence CROSS JOIN target;
COMMIT;
'@
$statusSql = $statusSql.Replace('__EVIDENCE_SQL__', $evidenceSql)
$status = Invoke-Psql $statusSql 'PET_REBIRTH_ATTEMPTS_STATUS|'

if ($status.auditCount -gt 1 -or
    ($status.auditCount -eq 1 -and
        (-not $status.auditValid -or -not $status.afterValid))) {
    throw 'Existing rebirth-attempt evidence is inconsistent.'
}
$databaseReady = $status.ownerOffline -and $status.beforeValid -and
    $status.sourceCommandValid -and $status.laterCommandCount -eq 0 -and
    $status.laterOperationCount -eq 0 -and
    $status.pendingPreviewCount -eq 0 -and $status.contentCount -eq 1 -and
    $status.maximumRebirthCount -ge 11 -and $status.auditCount -eq 0
$runtimeOffline = $DisposableTest -or
    (-not [bool]$environmentState.Server.State.Running -and
        $redisKeyCount -eq 0 -and -not $originRunning)
$state = if ($status.auditCount -eq 1) {
    'Applied'
} elseif ($databaseReady -and $runtimeOffline) {
    'Ready'
} elseif ($databaseReady) {
    'AwaitingOffline'
} else {
    'Refused'
}
$summary = [pscustomobject]@{
    Status = $state
    Database = $Database
    AccountId = 13
    CharacterId = 2
    PetId = 1
    PetLevel = $status.petLevel
    PetExperience = $status.petExperience
    CompletedRebirths = $status.completedRebirths
    RebirthsRemaining = $status.rebirthsRemaining
    TotalRebirthAllowance =
        [int]$status.completedRebirths + [int]$status.rebirthsRemaining
    TargetRemaining = $targetRemaining
    CurrentPetRevision = $status.petRevision
    MaximumRebirthCount = $status.maximumRebirthCount
    AuditId = $status.auditId
    ServerRunning = [bool]$environmentState.Server.State.Running
    OriginRunning = $originRunning
    RedisPlayerLoginKeyCount = $redisKeyCount
    OperationIdSha256 = $operationHex.ToUpperInvariant()
    RequestHashSha256 = $requestHashHex.ToUpperInvariant()
}
if ($Mode -eq 'Status') { return $summary }
if ($state -eq 'Applied') { return $summary }
if ($state -ne 'Ready') {
    throw 'Exact test2/Jolo source state is not ready for the offline fixture.'
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environmentState $redisContainer
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development account 13 / character 2 / pet 1',
        'Set remaining rebirth attempts from 0 to exactly 10')) {
    return
}
if (-not $DisposableTest) {
    Assert-RebirthRepairOffline $environmentState $redisContainer
}

$applySql = @'
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
CREATE TEMP TABLE rebirth_attempt_result (
    changed boolean NOT NULL,
    previous_remaining smallint NOT NULL,
    current_remaining smallint NOT NULL,
    previous_revision bigint NOT NULL,
    current_revision bigint NOT NULL,
    audit_id bigint NOT NULL
);
DO $fixture$
DECLARE
    v_pet public.character_pets%ROWTYPE;
    v_after public.character_pets%ROWTYPE;
    v_audit public.command_audit%ROWTYPE;
    v_maximum smallint;
BEGIN
    PERFORM 1
    FROM public.accounts account
    JOIN public.character_base character_row
      ON character_row.account_id = account.id
    WHERE account.id = 13
      AND account.username = 'test2'
      AND account.login_status = 0
      AND character_row.id = 2
      AND character_row.name = 'test2'
      AND character_row.lifecycle_state = 'active'
      AND character_row.checkpoint_owner_id IS NULL
    FOR UPDATE OF account, character_row;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Account 13 / character 2 is not safely offline.';
    END IF;

    SELECT * INTO STRICT v_pet
    FROM public.character_pets
    WHERE id = 1
    FOR UPDATE;

    SELECT audit.* INTO v_audit
    FROM public.command_audit audit
    WHERE audit.principal_type = 'developer'
      AND audit.principal_key = '13'
      AND audit.aggregate_type = 'pet'
      AND audit.aggregate_key = '1'
      AND audit.command_family = '__FAMILY__'
      AND audit.operation_id = decode('__OPERATION_HEX__', 'hex');
    IF FOUND THEN
        IF v_audit.request_hash <> decode('__REQUEST_HASH_HEX__', 'hex')
           OR v_audit.outcome_code <> 'granted'
           OR v_audit.retention_policy <> 'permanent'
           OR (v_audit.detail_payload->>'previousRemaining')::integer <> 0
           OR (v_audit.detail_payload->>'currentRemaining')::integer <> 10
           OR (v_audit.detail_payload->>'previousPetRevision')::bigint <> 468
           OR (v_audit.detail_payload->>'currentPetRevision')::bigint <> 469
           OR v_pet.user_id <> 2
           OR v_pet.completed_rebirths <> 1
           OR v_pet.rebirths_remaining <> 10
           OR v_pet.revision <> 469 THEN
            RAISE EXCEPTION 'Existing rebirth-attempt evidence is inconsistent.';
        END IF;
        INSERT INTO rebirth_attempt_result VALUES (
            false, 10, 10, 469, 469, v_audit.id);
        RETURN;
    END IF;

    IF v_pet.user_id <> 2
       OR v_pet.name <> 'Jolo'
       OR v_pet.level <> 118
       OR v_pet.experience <> 1239780
       OR v_pet.completed_rebirths <> 1
       OR v_pet.rebirths_remaining <> 0
       OR v_pet.revision <> 468
       OR v_pet.updated_at <>
          '2026-08-13 08:39:03.35139+00'::timestamptz
       OR v_pet.activity_state <> 'owned'
       OR NOT v_pet.is_carried
       OR NOT v_pet.is_summoned
       OR v_pet.contributes_to_character
       OR NOT v_pet.has_soul_contract THEN
        RAISE EXCEPTION 'Pet 1 no longer matches the exact source state.';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.command_audit audit
        WHERE audit.id = 8155
          AND audit.principal_type = 'account'
          AND audit.principal_key = '13'
          AND audit.aggregate_type = 'character_pet_value'
          AND audit.aggregate_key = 'character:2'
          AND audit.command_family = 'pet_level_upgrade'
          AND audit.outcome_code = 'committed'
    ) OR EXISTS (
        SELECT 1 FROM public.command_audit audit
        WHERE audit.id > 8155
          AND audit.aggregate_type = 'character_pet_value'
          AND audit.aggregate_key = 'character:2'
    ) OR EXISTS (
        SELECT 1 FROM public.pet_operation_audit audit
        WHERE audit.user_id = 2 AND audit.pet_id = 1 AND audit.id > 630
    ) OR EXISTS (
        SELECT 1 FROM public.character_pet_growth_previews preview
        WHERE preview.user_id = 2 AND preview.pet_id = 1
    ) THEN
        RAISE EXCEPTION 'Later pet activity or a pending preview was found.';
    END IF;

    SELECT settings.maximum_rebirth_count INTO STRICT v_maximum
    FROM public.pet_content_publication publication
    JOIN public.pet_content_revisions revision
      ON revision.revision = publication.revision
     AND revision.sealed_at IS NOT NULL
    JOIN public.pet_content_settings settings
      ON settings.revision = publication.revision
    WHERE publication.family = 'pets';
    IF v_pet.completed_rebirths + 10 > v_maximum
       OR v_pet.completed_rebirths + 10 > 255 THEN
        RAISE EXCEPTION 'Ten remaining attempts exceed content or wire limits.';
    END IF;

    UPDATE public.character_pets pet
    SET rebirths_remaining = 10,
        revision = pet.revision + 1,
        updated_at = transaction_timestamp()
    WHERE pet.id = 1
      AND pet.user_id = 2
      AND pet.revision = 468
      AND pet.rebirths_remaining = 0
    RETURNING pet.* INTO STRICT v_after;

    INSERT INTO public.command_audit (
        principal_type, principal_key, aggregate_type, aggregate_key,
        command_family, operation_id, request_hash, outcome_code,
        detail_payload, retention_policy
    ) VALUES (
        'developer', '13', 'pet', '1', '__FAMILY__',
        decode('__OPERATION_HEX__', 'hex'),
        decode('__REQUEST_HASH_HEX__', 'hex'), 'granted',
        jsonb_build_object(
            'source', 'offline_isolated_localdevelopment_fixture',
            'accountId', 13, 'characterId', 2, 'petId', 1,
            'petName', 'Jolo',
            'previousRemaining', v_pet.rebirths_remaining,
            'currentRemaining', v_after.rebirths_remaining,
            'completedRebirths', v_after.completed_rebirths,
            'totalRebirthAllowance',
                v_after.completed_rebirths + v_after.rebirths_remaining,
            'previousPetRevision', v_pet.revision,
            'currentPetRevision', v_after.revision,
            'levelPreserved', v_after.level,
            'experiencePreserved', v_after.experience,
            'sourceCommandAuditId', 8155),
        'permanent'
    ) RETURNING * INTO STRICT v_audit;

    INSERT INTO rebirth_attempt_result VALUES (
        true, v_pet.rebirths_remaining, v_after.rebirths_remaining,
        v_pet.revision, v_after.revision, v_audit.id);
END
$fixture$;
COMMIT;
SELECT 'PET_REBIRTH_ATTEMPTS_RESULT|' || jsonb_build_object(
    'status', CASE WHEN changed THEN 'Applied' ELSE 'AlreadyApplied' END,
    'accountId', 13, 'characterId', 2, 'petId', 1,
    'previousRemaining', previous_remaining,
    'currentRemaining', current_remaining,
    'completedRebirths', 1,
    'totalRebirthAllowance', 11,
    'previousPetRevision', previous_revision,
    'currentPetRevision', current_revision,
    'auditId', audit_id
)::text
FROM rebirth_attempt_result;
'@
$applySql = $applySql.
    Replace('__FAMILY__', $family).
    Replace('__OPERATION_HEX__', $operationHex).
    Replace('__REQUEST_HASH_HEX__', $requestHashHex)
Invoke-Psql $applySql 'PET_REBIRTH_ATTEMPTS_RESULT|'
