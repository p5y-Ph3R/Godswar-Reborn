Set-StrictMode -Version Latest

function Get-PetRank100SkillPredicate {
    @'
        SELECT count(*) = 4
           AND COALESCE(bool_and(
               (skill.slot_index, skill.skill_id, skill.skill_rank) IN (
                   (0,3920,6),(1,4519,6),
                   (2,5220,6),(3,5620,6))
               AND skill.skill_experience = 0
               AND skill.is_active), false)
        FROM public.character_pet_skills skill
        WHERE skill.pet_id = 1
'@
}

function Get-PetRank100StatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $skillPredicate = Get-PetRank100SkillPredicate
    @"
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '30s';
SELECT 'PET_RANK100_FIXTURE_STATUS|' || jsonb_build_object(
  'identityReady', EXISTS (
      SELECT 1 FROM public.accounts account
      JOIN public.character_base character_row
        ON character_row.account_id = account.id
      WHERE account.id = 13 AND account.username = 'test2'
        AND account.login_status = 0
        AND character_row.id = 2 AND character_row.name = 'test2'
        AND character_row.lifecycle_state = 'active'
        AND character_row.checkpoint_owner_id IS NULL),
  'sourceReady', EXISTS (
      SELECT 1 FROM public.character_pets pet
      WHERE pet.id = 1 AND pet.user_id = 2 AND pet.name = 'Jolo'
        AND pet.species_id = 40 AND pet.aptitude = 16
        AND pet.level = 120 AND pet.experience = 1507597955
        AND pet.rank = 5.590000 AND pet.revision = 1202
        AND pet.activity_state = 'owned' AND pet.is_carried
        AND NOT pet.is_summoned AND NOT pet.contributes_to_character
        AND pet.bound AND pet.has_soul_contract
        AND pet.soul_contract_stage = 6
        AND pet.completed_rebirths = 6
        AND pet.rebirths_remaining = 5
        AND pet.completed_pet_merges = 1
        AND (SELECT count(*) FROM public.character_pets owned
             WHERE owned.user_id = 2 AND owned.is_carried) = 1
        AND (SELECT count(*) FROM public.character_pets owned
             WHERE owned.user_id = 2 AND owned.is_summoned) = 0
        AND (SELECT count(*) FROM public.character_pets owned
             WHERE owned.user_id = 2
               AND owned.contributes_to_character) = 0
        AND (SELECT count(*) FROM public.character_pet_stat_values stat
             WHERE stat.pet_id = pet.id) = 6
        AND ($skillPredicate)),
  'pendingPreviewCount',
      (SELECT count(*) FROM public.character_pet_growth_previews
       WHERE user_id = 2 AND pet_id = 1) +
      (SELECT count(*) FROM public.character_pet_basic_savvy_previews
       WHERE user_id = 2 AND pet_id = 1),
  'petRank', (SELECT rank FROM public.character_pets WHERE id = 1),
  'petRevision', (SELECT revision FROM public.character_pets WHERE id = 1),
  'speciesId', (SELECT species_id FROM public.character_pets WHERE id = 1),
  'skillState', COALESCE((
      SELECT jsonb_agg(jsonb_build_object(
          'slot', skill.slot_index,
          'skillId', skill.skill_id,
          'tier', skill.skill_rank) ORDER BY skill.slot_index)
      FROM public.character_pet_skills skill WHERE skill.pet_id = 1),
      '[]'::jsonb),
  'receiptCount', (SELECT count(*) FROM public.command_audit audit
      WHERE audit.principal_type = 'developer'
        AND audit.principal_key = '13'
        AND audit.aggregate_type = 'pet'
        AND audit.aggregate_key = '1'
        AND audit.command_family = 'localdev_pet_rank_fixture'
        AND audit.operation_id = decode('$OperationHex','hex')),
  'receiptAuditId', (SELECT max(audit.id) FROM public.command_audit audit
      WHERE audit.principal_type = 'developer'
        AND audit.principal_key = '13'
        AND audit.aggregate_type = 'pet'
        AND audit.aggregate_key = '1'
        AND audit.command_family = 'localdev_pet_rank_fixture'
        AND audit.operation_id = decode('$OperationHex','hex')),
  'receiptValid', COALESCE((SELECT bool_and(
        audit.request_hash = decode('$RequestHashHex','hex')
        AND audit.outcome_code = 'applied'
        AND audit.retention_policy = 'permanent'
        AND (audit.detail_payload->>'fixtureVersion')::integer = 1
        AND audit.detail_payload->>'source' =
            'offline_isolated_localdevelopment_fixture'
        AND (audit.detail_payload->>'previousRank')::numeric = 5.590000
        AND (audit.detail_payload->>'currentRank')::numeric = 100.000000
        AND (audit.detail_payload->>'previousPetRevision')::bigint = 1202
        AND (audit.detail_payload->>'currentPetRevision')::bigint = 1203)
      FROM public.command_audit audit
      WHERE audit.principal_type = 'developer'
        AND audit.principal_key = '13'
        AND audit.aggregate_type = 'pet'
        AND audit.aggregate_key = '1'
        AND audit.command_family = 'localdev_pet_rank_fixture'
        AND audit.operation_id = decode('$OperationHex','hex')), false),
  'postReady', EXISTS (
      SELECT 1 FROM public.character_pets pet
      WHERE pet.id = 1 AND pet.user_id = 2
        AND pet.rank = 100.000000 AND pet.revision = 1203
        AND ($skillPredicate)))::text;
COMMIT;
"@
}

function Get-PetRank100ApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $skillPredicate = Get-PetRank100SkillPredicate
    @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
SELECT pg_advisory_xact_lock(13, 2);
CREATE TEMP TABLE fixture_result (
    changed boolean NOT NULL,
    previous_rank numeric(18,6) NOT NULL,
    current_rank numeric(18,6) NOT NULL,
    previous_revision bigint NOT NULL,
    current_revision bigint NOT NULL,
    audit_id bigint NOT NULL);
DO `$fixture`$
DECLARE
    v_account public.accounts%ROWTYPE;
    v_character public.character_base%ROWTYPE;
    v_pet public.character_pets%ROWTYPE;
    v_after public.character_pets%ROWTYPE;
    v_receipt public.command_audit%ROWTYPE;
    v_before_json jsonb;
    v_after_json jsonb;
    v_stats_before jsonb;
    v_stats_after jsonb;
    v_skills_before jsonb;
    v_skills_after jsonb;
    v_audit_id bigint;
BEGIN
    SELECT * INTO STRICT v_account
    FROM public.accounts WHERE id = 13 FOR UPDATE;
    SELECT * INTO STRICT v_character
    FROM public.character_base WHERE id = 2 FOR UPDATE;
    SELECT * INTO STRICT v_pet
    FROM public.character_pets WHERE id = 1 FOR UPDATE;
    PERFORM stat.pet_id FROM public.character_pet_stat_values stat
    WHERE stat.pet_id = 1 ORDER BY stat.stat_code FOR UPDATE;
    PERFORM skill.pet_id FROM public.character_pet_skills skill
    WHERE skill.pet_id = 1 ORDER BY skill.slot_index FOR UPDATE;
    PERFORM owned.id FROM public.character_pets owned
    WHERE owned.user_id = 2 ORDER BY owned.id FOR UPDATE;

    SELECT * INTO v_receipt FROM public.command_audit audit
    WHERE audit.principal_type = 'developer'
      AND audit.principal_key = '13'
      AND audit.aggregate_type = 'pet'
      AND audit.aggregate_key = '1'
      AND audit.command_family = 'localdev_pet_rank_fixture'
      AND audit.operation_id = decode('$OperationHex','hex');
    IF v_receipt.id IS NOT NULL THEN
        IF v_receipt.request_hash <> decode('$RequestHashHex','hex')
           OR v_receipt.outcome_code <> 'applied'
           OR v_receipt.retention_policy <> 'permanent'
           OR (v_receipt.detail_payload->>'fixtureVersion')::integer <> 1
           OR v_receipt.detail_payload->>'source' <>
              'offline_isolated_localdevelopment_fixture'
           OR (v_receipt.detail_payload->>'accountId')::integer <> 13
           OR (v_receipt.detail_payload->>'characterId')::integer <> 2
           OR (v_receipt.detail_payload->>'petId')::bigint <> 1
           OR (v_receipt.detail_payload->>'previousRank')::numeric <>
              5.590000
           OR (v_receipt.detail_payload->>'currentRank')::numeric <>
              100.000000
           OR (v_receipt.detail_payload->>'previousPetRevision')::bigint <>
              1202
           OR (v_receipt.detail_payload->>'currentPetRevision')::bigint <>
              1203
           OR v_pet.user_id <> 2 OR v_pet.rank <> 100.000000
           OR v_pet.revision <> 1203 OR NOT ($skillPredicate) THEN
            RAISE EXCEPTION
                'Existing pet-rank fixture receipt or post-state is inconsistent.';
        END IF;
        INSERT INTO fixture_result VALUES (
            false, 5.590000, v_pet.rank, 1202, v_pet.revision,
            v_receipt.id);
        RETURN;
    END IF;

    IF v_account.username <> 'test2' OR v_account.login_status <> 0
       OR v_character.account_id <> 13 OR v_character.name <> 'test2'
       OR v_character.lifecycle_state <> 'active'
       OR v_character.checkpoint_owner_id IS NOT NULL THEN
        RAISE EXCEPTION
            'Account 13 / character 2 is not the exact offline identity.';
    END IF;
    IF v_pet.user_id <> 2 OR v_pet.name <> 'Jolo'
       OR v_pet.species_id <> 40 OR v_pet.aptitude <> 16
       OR v_pet.level <> 120 OR v_pet.experience <> 1507597955
       OR v_pet.rank <> 5.590000 OR v_pet.revision <> 1202
       OR v_pet.activity_state <> 'owned' OR NOT v_pet.is_carried
       OR v_pet.is_summoned OR v_pet.contributes_to_character
       OR NOT v_pet.bound OR NOT v_pet.has_soul_contract
       OR v_pet.soul_contract_stage <> 6
       OR v_pet.completed_rebirths <> 6
       OR v_pet.rebirths_remaining <> 5
       OR v_pet.completed_pet_merges <> 1
       OR (SELECT count(*) FROM public.character_pets owned
           WHERE owned.user_id = 2 AND owned.is_carried) <> 1
       OR (SELECT count(*) FROM public.character_pets owned
           WHERE owned.user_id = 2 AND owned.is_summoned) <> 0
       OR (SELECT count(*) FROM public.character_pets owned
           WHERE owned.user_id = 2
             AND owned.contributes_to_character) <> 0 THEN
        RAISE EXCEPTION 'Pet 1 is not the pinned source main pet.';
    END IF;
    IF (SELECT count(*) FROM public.character_pet_stat_values
        WHERE pet_id = 1) <> 6 OR NOT ($skillPredicate) THEN
        RAISE EXCEPTION 'Pet 1 stats or learned skills changed.';
    END IF;
    IF EXISTS (SELECT 1 FROM public.character_pet_growth_previews
               WHERE user_id = 2 AND pet_id = 1)
       OR EXISTS (SELECT 1 FROM public.character_pet_basic_savvy_previews
                  WHERE user_id = 2 AND pet_id = 1) THEN
        RAISE EXCEPTION 'A pending pet preview blocks the rank fixture.';
    END IF;

    v_before_json := to_jsonb(v_pet);
    SELECT jsonb_agg(to_jsonb(stat) ORDER BY stat.stat_code)
      INTO v_stats_before
    FROM public.character_pet_stat_values stat WHERE stat.pet_id = 1;
    SELECT jsonb_agg(to_jsonb(skill) ORDER BY skill.slot_index)
      INTO v_skills_before
    FROM public.character_pet_skills skill WHERE skill.pet_id = 1;

    UPDATE public.character_pets pet
    SET rank = 100.000000,
        revision = pet.revision + 1,
        updated_at = transaction_timestamp()
    WHERE pet.id = 1 AND pet.user_id = 2
      AND pet.rank = 5.590000 AND pet.revision = 1202
    RETURNING pet.* INTO STRICT v_after;

    v_after_json := to_jsonb(v_after);
    SELECT jsonb_agg(to_jsonb(stat) ORDER BY stat.stat_code)
      INTO v_stats_after
    FROM public.character_pet_stat_values stat WHERE stat.pet_id = 1;
    SELECT jsonb_agg(to_jsonb(skill) ORDER BY skill.slot_index)
      INTO v_skills_after
    FROM public.character_pet_skills skill WHERE skill.pet_id = 1;
    IF v_after.rank <> 100.000000 OR v_after.revision <> 1203
       OR (v_before_json - ARRAY['rank','revision','updated_at']) <>
          (v_after_json - ARRAY['rank','revision','updated_at'])
       OR v_stats_before IS DISTINCT FROM v_stats_after
       OR v_skills_before IS DISTINCT FROM v_skills_after THEN
        RAISE EXCEPTION 'Pet-rank mutation exceeded its exact scope.';
    END IF;

    INSERT INTO public.command_audit (
        principal_type, principal_key, aggregate_type, aggregate_key,
        command_family, operation_id, request_hash, outcome_code,
        detail_payload, retention_policy)
    VALUES (
        'developer','13','pet','1','localdev_pet_rank_fixture',
        decode('$OperationHex','hex'),decode('$RequestHashHex','hex'),
        'applied',jsonb_build_object(
            'fixtureVersion',1,
            'source','offline_isolated_localdevelopment_fixture',
            'accountId',13,'characterId',2,'petId',1,
            'previousRank',5.590000,'currentRank',100.000000,
            'previousPetRevision',1202,'currentPetRevision',1203,
            'beforePet',v_before_json,'afterPet',v_after_json,
            'learnedSkills',v_skills_after),
        'permanent') RETURNING id INTO v_audit_id;
    INSERT INTO fixture_result VALUES (
        true,5.590000,100.000000,1202,1203,v_audit_id);
END
`$fixture`$;
COMMIT;
SELECT 'PET_RANK100_FIXTURE_RESULT|' || jsonb_build_object(
    'status', CASE WHEN changed THEN 'Applied' ELSE 'AlreadyApplied' END,
    'previousRank', previous_rank,
    'currentRank', current_rank,
    'previousPetRevision', previous_revision,
    'currentPetRevision', current_revision,
    'auditId', audit_id)::text
FROM fixture_result;
"@
}
