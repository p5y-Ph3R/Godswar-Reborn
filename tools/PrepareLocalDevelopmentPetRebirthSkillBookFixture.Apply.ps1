Set-StrictMode -Version Latest

function Get-PetRebirthSkillBookApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @'
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE expected_fixture_items (
    slot_index smallint PRIMARY KEY,
    prop_id integer UNIQUE NOT NULL,
    display_name text NOT NULL,
    pet_skill integer NOT NULL
) ON COMMIT DROP;
INSERT INTO expected_fixture_items VALUES
__MANIFEST_VALUES__;

CREATE TEMP TABLE desired_fixture_items (
    slot_index smallint PRIMARY KEY, prop_id integer UNIQUE NOT NULL,
    stack smallint NOT NULL) ON COMMIT DROP;
INSERT INTO desired_fixture_items
SELECT slot_index,prop_id,1 FROM expected_fixture_items
UNION ALL SELECT 24,10104,99;

CREATE TEMP TABLE expected_fixture_stats (
    stat_code smallint PRIMARY KEY, initial_savvy numeric NOT NULL,
    rarity_savvy numeric NOT NULL, base_rate numeric NOT NULL,
    acceleration numeric NOT NULL, added_savvy numeric NOT NULL,
    revision bigint NOT NULL
) ON COMMIT DROP;
INSERT INTO expected_fixture_stats VALUES
__STATS_VALUES__;

CREATE TEMP TABLE fixture_deleted_items (old_item jsonb NOT NULL)
    ON COMMIT DROP;
CREATE TEMP TABLE fixture_added_items (
    item_instance_id bigint PRIMARY KEY, slot_index smallint NOT NULL,
    prop_id integer NOT NULL, stack smallint NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE fixture_item_audit_ids (
    id bigint PRIMARY KEY, action text NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE fixture_result (
    changed boolean NOT NULL, removed_rows integer NOT NULL,
    added_rows integer NOT NULL, previous_experience bigint NOT NULL,
    current_experience bigint NOT NULL, previous_pet_revision bigint NOT NULL,
    current_pet_revision bigint NOT NULL,
    previous_inventory_revision bigint NOT NULL,
    current_inventory_revision bigint NOT NULL,
    publication_revision text NOT NULL, audit_id bigint NOT NULL
    );

DO $fixture$
DECLARE
    v_account public.accounts%ROWTYPE;
    v_character public.character_base%ROWTYPE;
    v_pet public.character_pets%ROWTYPE;
    v_receipt public.command_audit%ROWTYPE;
    v_publication text;
    v_content_count integer;
    v_content_valid boolean;
    v_count integer;
    v_units bigint;
    v_bag_ids bigint[];
    v_nonbag_before jsonb;
    v_nonbag_after jsonb;
    v_removed_ids jsonb;
    v_removed_audit_ids jsonb;
    v_added_items jsonb;
    v_item_audit_ids jsonb;
    v_post_valid boolean;
    v_audit_id bigint;
BEGIN
    SELECT * INTO STRICT v_account FROM public.accounts
    WHERE id = 13 FOR UPDATE;
    SELECT * INTO STRICT v_character FROM public.character_base
    WHERE id = 2 FOR UPDATE;
    SELECT * INTO STRICT v_pet FROM public.character_pets
    WHERE id = 1 FOR UPDATE;
    PERFORM item.id FROM public.character_items item
    WHERE item.user_id = 2 ORDER BY item.id FOR UPDATE;
    PERFORM sealed.id FROM public.sealed_pet_items sealed
    WHERE sealed.owner_character_id = 2
       OR sealed.pet_id IN (
          SELECT id FROM public.character_pets WHERE user_id = 2)
       OR sealed.item_instance_id IN (
          SELECT id FROM public.character_items WHERE user_id = 2)
    ORDER BY sealed.id FOR UPDATE;

    SELECT pointer.revision INTO STRICT v_publication
    FROM public.item_template_content_publication pointer
    JOIN public.item_template_content_revisions release
      ON release.revision = pointer.revision
    WHERE pointer.family = 'items' AND release.sealed_at IS NOT NULL
    FOR SHARE OF pointer, release;
    PERFORM definition.id
    FROM public.item_template_content_definitions definition
    JOIN desired_fixture_items desired
      ON desired.prop_id = definition.id
    WHERE definition.revision = v_publication
    ORDER BY definition.id FOR SHARE OF definition;
    PERFORM mutable.id FROM public.item_templates mutable
    JOIN desired_fixture_items desired
      ON desired.prop_id = mutable.id
    ORDER BY mutable.id FOR SHARE OF mutable;

    SELECT count(definition.id),
           COALESCE(bool_and(__CONTENT_PREDICATE__), false)
      INTO v_content_count, v_content_valid
    FROM expected_fixture_items expected
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = v_publication
     AND definition.id = expected.prop_id
    LEFT JOIN public.item_templates mutable
      ON mutable.id = expected.prop_id;
    IF v_content_count <> 24 OR NOT v_content_valid OR NOT EXISTS (
        SELECT 1
        FROM public.item_template_content_definitions spirit
        JOIN public.item_templates mutable_spirit
          ON mutable_spirit.id = spirit.id
        WHERE spirit.revision = v_publication AND spirit.id = 10104
          AND __SPIRIT_CONTENT_PREDICATE__) THEN
        RAISE EXCEPTION 'The 24 reviewed skill books are not published.';
    END IF;

    SELECT * INTO v_receipt FROM public.command_audit audit
    WHERE audit.principal_type = 'developer'
      AND audit.principal_key = '13'
      AND audit.aggregate_type = 'character_pet_fixture'
      AND audit.aggregate_key = 'character:2|pet:1'
      AND audit.command_family = 'pet_rebirth_skillbook_fixture'
      AND audit.operation_id = decode('__OPERATION_HEX__', 'hex');
    IF v_receipt.id IS NOT NULL THEN
        SELECT count(item.id) = 25
               AND (SELECT count(*) FROM public.character_items all_item
                    WHERE all_item.user_id = 2
                      AND all_item.item_location = 1) = 25
               AND COALESCE(bool_and(
                    item.item_quality = 1 AND item.item_grade = 1
                    AND item.bound = 0 AND item.stack = desired.stack
                    AND item.item_exp = 0 AND item.holy_suit_code = 0), false),
               COALESCE(jsonb_agg(jsonb_build_object(
                    'itemInstanceId', item.id,
                    'slot', item.slot_index,
                    'propId', item.prop_id,
                    'stack', item.stack) ORDER BY desired.slot_index)
                    FILTER (WHERE item.id IS NOT NULL), '[]'::jsonb)
          INTO v_post_valid, v_added_items
        FROM desired_fixture_items desired
        LEFT JOIN public.character_items item
          ON item.user_id = 2 AND item.item_location = 1
         AND item.slot_index = desired.slot_index
         AND item.prop_id = desired.prop_id;
        SELECT count(*) INTO v_count
        FROM public.character_item_audit item_audit
        JOIN LATERAL jsonb_array_elements_text(
            v_receipt.detail_payload->'itemAuditIds') linked(id)
          ON item_audit.id = linked.id::bigint;
        IF v_receipt.request_hash <>
               decode('__REQUEST_HASH_HEX__', 'hex')
           OR v_receipt.outcome_code <> 'applied'
           OR v_receipt.retention_policy <> 'permanent'
           OR (v_receipt.detail_payload->>'fixtureVersion')::integer <> 2
           OR v_receipt.detail_payload->>'source' <>
              'offline_isolated_localdevelopment_fixture'
           OR (v_receipt.detail_payload->>'accountId')::bigint <> 13
           OR (v_receipt.detail_payload->>'characterId')::bigint <> 2
           OR (v_receipt.detail_payload->>'petId')::bigint <> 1
           OR v_receipt.detail_payload->'clearScope' <> jsonb_build_object(
              'itemLocation',1,'minimumSlot',0,'maximumSlot',95)
           OR (v_receipt.detail_payload->>'removedRows')::integer <> 62
           OR (v_receipt.detail_payload->>'removedUnits')::bigint <> 1806
           OR (v_receipt.detail_payload->>'grantedItemCount')::integer <> 25
           OR (v_receipt.detail_payload->>'grantedUnits')::bigint <> 123
           OR (v_receipt.detail_payload->>'previousInventoryRevision')::bigint
              <> 690
           OR (v_receipt.detail_payload->>'currentExperience')::bigint <>
               1507597955
           OR (v_receipt.detail_payload->>'previousExperience')::bigint <>
              7597955
           OR (v_receipt.detail_payload->>'experienceDelta')::bigint <>
              1500000000
           OR (v_receipt.detail_payload->>'previousPetRevision')::bigint <>
              1168
           OR (v_receipt.detail_payload->>'currentPetRevision')::bigint <>
               1169
           OR (v_receipt.detail_payload->>'currentInventoryRevision')::bigint
               <> 691
           OR jsonb_array_length(
               v_receipt.detail_payload->'removedItemIds') <> 62
           OR jsonb_array_length(
               v_receipt.detail_payload->'addedItems') <> 25
           OR jsonb_array_length(
               v_receipt.detail_payload->'itemAuditIds') <> 87
           OR v_receipt.detail_payload->'addedItems' <> v_added_items
           OR NOT EXISTS (SELECT 1
              FROM public.item_template_content_revisions release
              WHERE release.revision =
                    v_receipt.detail_payload->>'publishedItemRevision'
                AND release.sealed_at IS NOT NULL)
           OR NOT v_post_valid OR v_count <> 87
           OR v_character.inventory_revision <> 691
           OR v_pet.user_id <> 2 OR v_pet.level <> 120
           OR v_pet.experience <> 1507597955 OR v_pet.revision <> 1169
           OR EXISTS (SELECT 1
               FROM public.character_pet_growth_previews
               WHERE user_id = 2 AND pet_id = 1)
           OR EXISTS (SELECT 1
               FROM public.character_pet_basic_savvy_previews
               WHERE user_id = 2 AND pet_id = 1)
           OR EXISTS (SELECT 1 FROM public.character_items item
               JOIN public.sealed_pet_items sealed
                 ON sealed.item_instance_id = item.id
               WHERE item.user_id = 2 AND item.item_location = 1) THEN
            RAISE EXCEPTION 'Fixture receipt or post-state is inconsistent.';
        END IF;
        SELECT count(*) INTO v_count
        FROM desired_fixture_items expected
        JOIN public.character_item_audit item_audit
          ON item_audit.slot_index = expected.slot_index
         AND item_audit.prop_id = expected.prop_id
        JOIN LATERAL jsonb_array_elements_text(
            v_receipt.detail_payload->'itemAuditIds') linked(id)
          ON item_audit.id = linked.id::bigint
        WHERE item_audit.source =
                'localdev-pet-rebirth-skillbook-fixture-v1'
          AND item_audit.action = 'add' AND item_audit.user_id = 2
          AND item_audit.item_location = 1
          AND item_audit.item_quality = 1 AND item_audit.item_grade = 1
          AND item_audit.item_exp = 0 AND item_audit.old_item IS NULL;
        SELECT COALESCE(jsonb_agg(
                 (item_audit.old_item->>'id')::bigint
                 ORDER BY (item_audit.old_item->>'slot_index')::smallint),
                 '[]'::jsonb)
          INTO v_removed_audit_ids
        FROM public.character_item_audit item_audit
        JOIN LATERAL jsonb_array_elements_text(
            v_receipt.detail_payload->'itemAuditIds') linked(id)
          ON item_audit.id = linked.id::bigint
        WHERE item_audit.source =
                'localdev-pet-rebirth-skillbook-fixture-v1'
          AND item_audit.action = 'delete' AND item_audit.user_id = 2
          AND item_audit.item_location = 1;
        IF v_count <> 25 OR v_removed_audit_ids <>
                v_receipt.detail_payload->'removedItemIds' THEN
            RAISE EXCEPTION 'Fixture item audits are inconsistent.';
        END IF;
        INSERT INTO fixture_result VALUES (
            false, 62, 25, 1507597955, 1507597955, 1169, 1169,
            691, 691, v_publication, v_receipt.id);
        RETURN;
    END IF;

    IF v_account.username <> 'test2' OR v_account.login_status <> 0
       OR v_character.account_id <> 13 OR v_character.name <> 'test2'
       OR v_character.lifecycle_state <> 'active'
       OR v_character.checkpoint_owner_id IS NOT NULL
       OR v_character.inventory_revision <> 690 THEN
        RAISE EXCEPTION 'Character 2 is not the exact offline source.';
    END IF;
    IF v_pet.user_id <> 2 OR v_pet.name <> 'Jolo'
       OR v_pet.species_id <> 31 OR v_pet.level <> 120
       OR v_pet.experience <> 7597955 OR v_pet.revision <> 1168
       OR v_pet.completed_rebirths <> 6 OR v_pet.rebirths_remaining <> 5
       OR v_pet.completed_pet_merges <> 1 OR NOT v_pet.bound
       OR NOT v_pet.has_soul_contract OR v_pet.soul_contract_stage <> 6
       OR v_pet.activity_state <> 'owned' OR NOT v_pet.is_carried
       OR NOT v_pet.is_summoned OR v_pet.contributes_to_character
       OR v_pet.updated_at <>
          '2026-08-13 11:18:26.855536+00'::timestamptz THEN
        RAISE EXCEPTION 'Pet 1 is not the exact source row.';
    END IF;
    SELECT count(*) INTO v_count
    FROM expected_fixture_stats expected
    JOIN public.character_pet_stat_values actual
      ON actual.pet_id = 1 AND actual.stat_code = expected.stat_code
     AND actual.initial_savvy = expected.initial_savvy
     AND actual.rarity_added_savvy = expected.rarity_savvy
     AND actual.base_growth_rate = expected.base_rate
     AND actual.growth_acceleration = expected.acceleration
     AND actual.added_savvy = expected.added_savvy
     AND actual.revision = expected.revision;
    IF v_count <> 6 OR 6 <> (SELECT count(*)
        FROM public.character_pet_stat_values WHERE pet_id = 1) THEN
        RAISE EXCEPTION 'Pet 1 stats do not match.';
    END IF;
    SELECT count(*), COALESCE(sum(item.stack), 0),
           array_agg(item.id ORDER BY item.slot_index)
      INTO v_count, v_units, v_bag_ids
    FROM public.character_items item
    WHERE item.user_id = 2 AND item.item_location = 1;
    IF v_count <> 62 OR v_units <> 1806
       OR v_bag_ids <> __SOURCE_BAG_IDS__ THEN
        RAISE EXCEPTION 'The kit bag does not match revision 690.';
    END IF;
    IF EXISTS (SELECT 1 FROM public.character_items item
        JOIN public.sealed_pet_items sealed
          ON sealed.item_instance_id = item.id
        WHERE item.user_id = 2 AND item.item_location = 1)
       OR EXISTS (SELECT 1 FROM public.character_pet_growth_previews
          WHERE user_id = 2 AND pet_id = 1)
       OR EXISTS (SELECT 1
          FROM public.character_pet_basic_savvy_previews
          WHERE user_id = 2 AND pet_id = 1) THEN
        RAISE EXCEPTION 'A sealed link or pet preview blocks the fixture.';
    END IF;
    IF 1507597955::bigint > 4294967295::bigint THEN
        RAISE EXCEPTION 'Pet EXP exceeds uint32.';
    END IF;

    SELECT COALESCE(jsonb_agg(to_jsonb(item)
             ORDER BY item.item_location, item.slot_index, item.id),
             '[]'::jsonb)
      INTO v_nonbag_before
    FROM public.character_items item
    WHERE item.user_id = 2 AND item.item_location <> 1;
    WITH deleted AS (
        DELETE FROM public.character_items item
        WHERE item.user_id = 2 AND item.item_location = 1
          AND item.slot_index BETWEEN 0 AND 95
        RETURNING item.*)
    INSERT INTO fixture_deleted_items(old_item)
    SELECT to_jsonb(deleted) FROM deleted ORDER BY slot_index;
    IF (SELECT count(*) FROM fixture_deleted_items) <> 62 THEN
        RAISE EXCEPTION 'Bag clear was not exact.';
    END IF;

    WITH inserted AS (
        INSERT INTO public.character_items (
            user_id,item_location,slot_index,prop_id,item_quality,item_grade,
            bound,stack,item_exp,holy_suit_code)
        SELECT 2,1,expected.slot_index,expected.prop_id,1,1,0,
               expected.stack,0,0
        FROM desired_fixture_items expected ORDER BY expected.slot_index
        RETURNING id,slot_index,prop_id,stack)
    INSERT INTO fixture_added_items
    SELECT id,slot_index,prop_id,stack FROM inserted;
    IF (SELECT count(*) FROM fixture_added_items) <> 25 THEN
        RAISE EXCEPTION 'Skill-book grant was not exact.';
    END IF;

    UPDATE public.character_base
    SET inventory_revision = 691
    WHERE id = 2 AND account_id = 13 AND inventory_revision = 690;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Inventory revision did not advance.';
    END IF;
    UPDATE public.character_pets
    SET experience = 1507597955, revision = 1169,
        updated_at = transaction_timestamp()
    WHERE id = 1 AND user_id = 2 AND level = 120
      AND experience = 7597955 AND revision = 1168;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Pet EXP did not advance.';
    END IF;

    SELECT COALESCE(jsonb_agg(to_jsonb(item)
             ORDER BY item.item_location, item.slot_index, item.id),
             '[]'::jsonb)
      INTO v_nonbag_after
    FROM public.character_items item
    WHERE item.user_id = 2 AND item.item_location <> 1;
    SELECT count(item.id) = 25
           AND (SELECT count(*) FROM public.character_items all_item
                WHERE all_item.user_id = 2
                  AND all_item.item_location = 1) = 25
           AND COALESCE(bool_and(
                item.item_quality = 1 AND item.item_grade = 1
                AND item.bound = 0 AND item.stack = expected.stack
                AND item.item_exp = 0 AND item.holy_suit_code = 0), false)
      INTO v_post_valid
    FROM desired_fixture_items expected
    LEFT JOIN public.character_items item
      ON item.user_id = 2 AND item.item_location = 1
     AND item.slot_index = expected.slot_index
     AND item.prop_id = expected.prop_id;
    IF v_nonbag_before <> v_nonbag_after OR NOT v_post_valid
       OR NOT EXISTS (SELECT 1 FROM public.character_pets
           WHERE id = 1 AND experience = 1507597955 AND revision = 1169)
       OR EXISTS (SELECT 1 FROM public.character_items item
           JOIN public.sealed_pet_items sealed
             ON sealed.item_instance_id = item.id
           WHERE item.user_id = 2 AND item.item_location = 1) THEN
        RAISE EXCEPTION 'Post-state verification failed.';
    END IF;

__AUDIT_SQL__
END
$fixture$;
COMMIT;
SELECT 'PET_REBIRTH_SKILLBOOK_FIXTURE_RESULT|' || jsonb_build_object(
    'status',CASE WHEN changed THEN 'Applied' ELSE 'AlreadyApplied' END,
    'removedRows',removed_rows,'addedRows',added_rows,
    'previousExperience',previous_experience,
    'currentExperience',current_experience,
    'previousPetRevision',previous_pet_revision,
    'currentPetRevision',current_pet_revision,
    'previousInventoryRevision',previous_inventory_revision,
    'currentInventoryRevision',current_inventory_revision,
    'publishedItemRevision',publication_revision,'auditId',audit_id
)::text FROM fixture_result;
'@
    $sql.Replace(
        '__AUDIT_SQL__',
        (Get-PetRebirthSkillBookAuditSql)).Replace(
        '__MANIFEST_VALUES__',
        (Get-PetRebirthSkillBookManifestValues)).Replace(
        '__STATS_VALUES__',
        (Get-PetRebirthSkillBookExpectedStatsValues)).Replace(
        '__SOURCE_BAG_IDS__',
        (Get-PetRebirthSkillBookSourceBagIdArray)).Replace(
        '__SPIRIT_CONTENT_PREDICATE__',
        (Get-PetRebirthSpiritContentPredicate)).Replace(
        '__CONTENT_PREDICATE__',
        (Get-PetRebirthSkillBookContentPredicate)).Replace(
        '__OPERATION_HEX__', $OperationHex).Replace(
        '__REQUEST_HASH_HEX__', $RequestHashHex)
}
