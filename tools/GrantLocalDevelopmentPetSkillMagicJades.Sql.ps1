Set-StrictMode -Version Latest

function Get-PetSkillMagicJadeSetupSql {
    @'
CREATE TEMP TABLE expected_pet_skill_jades (
    ordinal smallint PRIMARY KEY,
    slot_index smallint UNIQUE NOT NULL,
    prop_id integer UNIQUE NOT NULL,
    species_id smallint UNIQUE NOT NULL,
    appearance_name text NOT NULL
) ON COMMIT DROP;
INSERT INTO expected_pet_skill_jades VALUES
    (1,25,11074,25,'Cretan Bull'),
    (2,26,11078,29,'Totoro'),
    (3,27,11086,37,'King Lion'),
    (4,28,11089,40,'Kratortle');

CREATE TEMP TABLE expected_pet_skill_source_bag (
    slot_index smallint PRIMARY KEY,
    prop_id integer UNIQUE NOT NULL,
    stack smallint NOT NULL
) ON COMMIT DROP;
INSERT INTO expected_pet_skill_source_bag
SELECT value::smallint,(10464 + value)::integer,1::smallint
FROM generate_series(0,5) value
UNION ALL
SELECT (6 + value)::smallint,(10510 + value)::integer,1::smallint
FROM generate_series(0,5) value
UNION ALL
SELECT (12 + value)::smallint,(10590 + value)::integer,1::smallint
FROM generate_series(0,5) value
UNION ALL
SELECT (18 + value)::smallint,(10700 + value)::integer,1::smallint
FROM generate_series(0,5) value
UNION ALL SELECT 24::smallint,10104,99::smallint;
'@
}

function Get-PetSkillMagicJadeValidationSql {
    @'
WITH item_release AS (
    SELECT publication.revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions release
      ON release.revision = publication.revision
     AND release.sealed_at IS NOT NULL
    WHERE publication.family = 'items'
), pet_release AS (
    SELECT revision FROM public.pet_content_publication WHERE family = 'pets'
), content AS (
    SELECT
      (SELECT revision FROM item_release) AS item_revision,
      (SELECT revision FROM pet_release) AS pet_revision,
      (SELECT count(*) FROM expected_pet_skill_jades expected
       JOIN public.item_template_content_definitions definition
         ON definition.revision = (SELECT revision FROM item_release)
        AND definition.id = expected.prop_id
       JOIN public.item_templates mutable ON mutable.id = definition.id
       WHERE definition.kind = 'consume item'
         AND definition.name_key = 'Pet' || expected.prop_id::text
         AND definition.display_name = 'Magic Jade: ' || expected.appearance_name
         AND definition.equipment_slot = 0
         AND cardinality(definition.class_ids) = 0
         AND definition.min_level IS NULL
         AND definition.max_level IS NULL
         AND definition.hand IS NULL
         AND definition.skill_flag IS NULL
         AND definition.texture = './Localization/en_us/UI/Texture/Icon2.gwo'
         AND definition.icon = '396,756'
         AND definition.stats = jsonb_build_object(
             'ID',expected.prop_id::text,'Type','consume item',
             'Texture','./Localization/en_us/UI/Texture/Icon2.gwo',
             'Icon','396,756','Random','0','Distribution','0,0',
             'Money','0','Overlap','99')
         AND to_jsonb(mutable) - 'created_at' - 'updated_at' =
             to_jsonb(definition) - 'revision') AS item_count,
      (SELECT count(*) FROM expected_pet_skill_jades expected
       JOIN public.current_pet_magic_jade_appearance_groups jade
         ON jade.magic_jade_item_id = expected.prop_id
        AND jade.species_id = expected.species_id
        AND jade.appearance_name = expected.appearance_name
        AND jade.revision = (SELECT revision FROM pet_release)
        AND jade.merge_cap = 7.80) AS pet_count
), identity_state AS (
    SELECT account.login_status,character_row.inventory_revision,
           character_row.checkpoint_owner_id,
           account.username,character_row.name
    FROM public.accounts account
    JOIN public.character_base character_row
      ON character_row.account_id = account.id
    WHERE account.id = 13 AND character_row.id = 2
      AND character_row.lifecycle_state = 'active'
), bag_state AS (
    SELECT
      count(*) FILTER (WHERE item.item_location = 1) AS bag_rows,
      count(*) FILTER (
        WHERE item.item_location = 1
          AND expected.slot_index IS NOT NULL
          AND item.prop_id = expected.prop_id
          AND item.stack = expected.stack
          AND item.bound = 0 AND item.item_quality = 1
          AND item.item_grade = 1 AND item.item_exp = 0
          AND item.holy_suit_code = 0 AND item.holy_socket_count = 0
          AND item.attribute1 IS NULL AND item.attribute2 IS NULL
          AND item.attribute3 IS NULL AND item.attribute4 IS NULL
          AND item.attribute5 IS NULL) AS exact_source_rows,
      count(*) FILTER (
        WHERE item.item_location = 1
          AND jade.prop_id IS NOT NULL
          AND item.prop_id = jade.prop_id
          AND item.stack = 1 AND item.bound = 0
          AND item.item_quality = 1 AND item.item_grade = 1
          AND item.item_exp = 0 AND item.holy_suit_code = 0
          AND item.holy_socket_count = 0) AS exact_jade_rows
    FROM public.character_items item
    LEFT JOIN expected_pet_skill_source_bag expected
      ON expected.slot_index = item.slot_index
    LEFT JOIN expected_pet_skill_jades jade
      ON jade.slot_index = item.slot_index
    WHERE item.user_id = 2
), receipt AS (
    SELECT audit.* FROM public.command_audit audit
    WHERE audit.principal_type = 'developer'
      AND audit.principal_key = '13'
      AND audit.aggregate_type = 'character_inventory'
      AND audit.aggregate_key = 'character:2'
      AND audit.command_family = 'pet_skill_magic_jade_grant'
      AND audit.operation_id = decode('__OPERATION_HEX__','hex')
), receipt_state AS (
    SELECT count(*) AS receipt_count,
      COALESCE(bool_and(
        receipt.request_hash = decode('__REQUEST_HASH_HEX__','hex')
        AND receipt.outcome_code = 'applied'
        AND receipt.retention_policy = 'permanent'
        AND receipt.detail_payload->>'previousInventoryRevision' = '691'
        AND receipt.detail_payload->>'currentInventoryRevision' = '692'
        AND receipt.detail_payload->'itemIds' = '[11074,11078,11086,11089]'::jsonb
        AND receipt.detail_payload->'slots' = '[25,26,27,28]'::jsonb),false)
        AS receipt_fields_valid,
      min(receipt.id) AS audit_id
    FROM receipt
), audit_state AS (
    SELECT count(*) AS linked_item_audits
    FROM receipt
    CROSS JOIN LATERAL jsonb_array_elements_text(
        receipt.detail_payload->'itemAuditIds') linked(id)
    JOIN public.character_item_audit item_audit
      ON item_audit.id = linked.id::bigint
    JOIN expected_pet_skill_jades expected
      ON expected.slot_index = item_audit.slot_index
     AND expected.prop_id = item_audit.prop_id
    WHERE item_audit.source = 'localdev-pet-skill-magic-jade-grant-v1'
      AND item_audit.action = 'add' AND item_audit.user_id = 2
      AND item_audit.item_location = 1 AND item_audit.old_item IS NULL
)
SELECT content.*,identity_state.*,bag_state.*,receipt_state.*,
       audit_state.linked_item_audits
FROM content CROSS JOIN identity_state CROSS JOIN bag_state
CROSS JOIN receipt_state CROSS JOIN audit_state
'@
}

function Get-PetSkillMagicJadeStatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN READ ONLY;
WITH expected_pet_skill_jades(
    ordinal,slot_index,prop_id,species_id,appearance_name) AS (VALUES
    (1,25,11074,25,'Cretan Bull'),
    (2,26,11078,29,'Totoro'),
    (3,27,11086,37,'King Lion'),
    (4,28,11089,40,'Kratortle')
), expected_pet_skill_source_bag(slot_index,prop_id,stack) AS (
    SELECT value::smallint,(10464 + value)::integer,1::smallint
    FROM generate_series(0,5) value
    UNION ALL SELECT (6 + value)::smallint,(10510 + value)::integer,
                     1::smallint FROM generate_series(0,5) value
    UNION ALL SELECT (12 + value)::smallint,(10590 + value)::integer,
                     1::smallint FROM generate_series(0,5) value
    UNION ALL SELECT (18 + value)::smallint,(10700 + value)::integer,
                     1::smallint FROM generate_series(0,5) value
    UNION ALL SELECT 24::smallint,10104,99::smallint
), state AS (
$(Get-PetSkillMagicJadeValidationSql)
)
SELECT 'PET_SKILL_MAGIC_JADE_GRANT_STATUS|' || jsonb_build_object(
  'itemRevision',item_revision,'petRevision',pet_revision,
  'contentValid',item_count = 4 AND pet_count = 4,
  'identityReady',username = 'test2' AND name = 'test2'
      AND login_status = 0 AND checkpoint_owner_id IS NULL,
  'sourceBagReady',inventory_revision = 691 AND bag_rows = 25
      AND exact_source_rows = 25 AND exact_jade_rows = 0,
  'postBagReady',inventory_revision = 692 AND bag_rows = 29
      AND exact_source_rows = 25 AND exact_jade_rows = 4,
  'inventoryRevision',inventory_revision,'bagRows',bag_rows,
  'sourceRows',exact_source_rows,'jadeRows',exact_jade_rows,
  'receiptCount',receipt_count,
  'receiptValid',receipt_count = 1 AND receipt_fields_valid
      AND linked_item_audits = 4 AND inventory_revision = 692
      AND bag_rows = 29 AND exact_source_rows = 25
      AND exact_jade_rows = 4,
  'receiptAuditId',audit_id)::text
FROM state;
COMMIT;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}

function Get-PetSkillMagicJadeApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
$(Get-PetSkillMagicJadeSetupSql)
CREATE TEMP TABLE pet_skill_jade_result (
    changed boolean NOT NULL,item_revision text NOT NULL,
    pet_revision text NOT NULL,audit_id bigint NOT NULL
);
DO `$grant`$
DECLARE
    v_item_revision text;
    v_pet_revision text;
    v_audit_id bigint;
    v_item_audit_ids jsonb;
    v_count integer;
BEGIN
    PERFORM pg_advisory_xact_lock(13,2);
    PERFORM account.id FROM public.accounts account
    WHERE account.id = 13 FOR UPDATE;
    PERFORM character_row.id FROM public.character_base character_row
    WHERE character_row.id = 2 FOR UPDATE;
    PERFORM item.id FROM public.character_items item
    WHERE item.user_id = 2 ORDER BY item.item_location,item.slot_index
    FOR UPDATE;
    PERFORM sealed.id FROM public.sealed_pet_items sealed
    WHERE sealed.owner_character_id = 2 ORDER BY sealed.id FOR UPDATE;

    SELECT publication.revision INTO v_item_revision
    FROM public.item_template_content_publication publication
    JOIN public.item_template_content_revisions release
      ON release.revision = publication.revision
     AND release.sealed_at IS NOT NULL
    WHERE publication.family = 'items';
    SELECT revision INTO v_pet_revision
    FROM public.pet_content_publication WHERE family = 'pets';

    IF EXISTS (
        SELECT 1 FROM public.command_audit audit
        WHERE audit.principal_type = 'developer'
          AND audit.principal_key = '13'
          AND audit.aggregate_type = 'character_inventory'
          AND audit.aggregate_key = 'character:2'
          AND audit.command_family = 'pet_skill_magic_jade_grant'
          AND audit.operation_id = decode('__OPERATION_HEX__','hex'))
    THEN
        RAISE EXCEPTION 'The permanent grant receipt already exists.';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM public.accounts account
        JOIN public.character_base character_row
          ON character_row.account_id = account.id
        WHERE account.id = 13 AND account.username = 'test2'
          AND account.login_status = 0 AND character_row.id = 2
          AND character_row.name = 'test2'
          AND character_row.lifecycle_state = 'active'
          AND character_row.checkpoint_owner_id IS NULL
          AND character_row.inventory_revision = 691)
    THEN RAISE EXCEPTION 'Character 2 is not the exact offline source.';
    END IF;

    SELECT count(*) INTO v_count FROM public.character_items item
    JOIN expected_pet_skill_source_bag expected
      ON expected.slot_index = item.slot_index
     AND expected.prop_id = item.prop_id AND expected.stack = item.stack
    WHERE item.user_id = 2 AND item.item_location = 1
      AND item.bound = 0 AND item.item_quality = 1 AND item.item_grade = 1
      AND item.item_exp = 0 AND item.holy_suit_code = 0
      AND item.holy_socket_count = 0
      AND item.attribute1 IS NULL AND item.attribute2 IS NULL
      AND item.attribute3 IS NULL AND item.attribute4 IS NULL
      AND item.attribute5 IS NULL;
    IF v_count <> 25 OR
       (SELECT count(*) FROM public.character_items
        WHERE user_id = 2 AND item_location = 1) <> 25 OR
       EXISTS (SELECT 1 FROM public.sealed_pet_items sealed
               JOIN public.character_items item
                 ON item.id = sealed.item_instance_id
               WHERE item.user_id = 2 AND item.item_location = 1)
    THEN RAISE EXCEPTION 'The exact 25-row source bag is not intact.';
    END IF;

    SELECT count(*) INTO v_count FROM expected_pet_skill_jades expected
    JOIN public.item_template_content_definitions definition
      ON definition.revision = v_item_revision
     AND definition.id = expected.prop_id
    JOIN public.item_templates mutable ON mutable.id = definition.id
    JOIN public.current_pet_magic_jade_appearance_groups jade
      ON jade.magic_jade_item_id = expected.prop_id
     AND jade.species_id = expected.species_id
     AND jade.appearance_name = expected.appearance_name
     AND jade.revision = v_pet_revision AND jade.merge_cap = 7.80
    WHERE definition.kind = 'consume item'
      AND definition.name_key = 'Pet' || expected.prop_id::text
      AND definition.display_name = 'Magic Jade: ' || expected.appearance_name
      AND definition.texture = './Localization/en_us/UI/Texture/Icon2.gwo'
      AND definition.icon = '396,756'
      AND definition.stats->>'Overlap' = '99'
      AND definition.stats->>'Type' = 'consume item'
      AND mutable.kind = definition.kind
      AND mutable.display_name = definition.display_name
      AND mutable.stats = definition.stats;
    IF v_count <> 4 THEN
        RAISE EXCEPTION 'The four published Magic Jades are not exact.';
    END IF;

    INSERT INTO public.character_items (
        user_id,item_location,slot_index,prop_id,item_quality,item_grade,
        bound,stack,item_exp,holy_suit_code)
    SELECT 2,1,slot_index,prop_id,1,1,0,1,0,0
    FROM expected_pet_skill_jades ORDER BY ordinal;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    IF v_count <> 4 THEN RAISE EXCEPTION 'Jade append was not exact.'; END IF;

    WITH audited AS (
      INSERT INTO public.character_item_audit (
          source,action,user_id,item_location,slot_index,prop_id,
          item_quality,item_grade,item_exp,old_item)
      SELECT 'localdev-pet-skill-magic-jade-grant-v1','add',2,1,
             slot_index,prop_id,1,1,0,NULL
      FROM expected_pet_skill_jades ORDER BY ordinal RETURNING id)
    SELECT jsonb_agg(id ORDER BY id) INTO v_item_audit_ids FROM audited;
    IF jsonb_array_length(v_item_audit_ids) <> 4 THEN
        RAISE EXCEPTION 'Jade item-audit append was not exact.';
    END IF;

    UPDATE public.character_base SET inventory_revision = 692
    WHERE id = 2 AND account_id = 13 AND inventory_revision = 691;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    IF v_count <> 1 THEN
        RAISE EXCEPTION 'Inventory revision did not advance exactly once.';
    END IF;

    INSERT INTO public.command_audit (
        principal_type,principal_key,aggregate_type,aggregate_key,
        command_family,operation_id,request_hash,outcome_code,
        detail_payload,retention_policy)
    VALUES ('developer','13','character_inventory','character:2',
        'pet_skill_magic_jade_grant',decode('__OPERATION_HEX__','hex'),
        decode('__REQUEST_HASH_HEX__','hex'),'applied',jsonb_build_object(
          'fixtureVersion',1,'source','offline_isolated_localdevelopment_grant',
          'accountId',13,'characterId',2,
          'itemIds','[11074,11078,11086,11089]'::jsonb,
          'speciesIds','[25,29,37,40]'::jsonb,
          'slots','[25,26,27,28]'::jsonb,
          'previousInventoryRevision',691,'currentInventoryRevision',692,
          'publishedItemRevision',v_item_revision,
          'publishedPetRevision',v_pet_revision,
          'itemAuditIds',v_item_audit_ids),'permanent')
    RETURNING id INTO v_audit_id;

    IF (SELECT count(*) FROM public.character_items item
        JOIN expected_pet_skill_jades expected
          ON expected.slot_index = item.slot_index
         AND expected.prop_id = item.prop_id
        WHERE item.user_id = 2 AND item.item_location = 1
          AND item.stack = 1 AND item.bound = 0
          AND item.item_quality = 1 AND item.item_grade = 1
          AND item.item_exp = 0 AND item.holy_suit_code = 0) <> 4 OR
       (SELECT count(*) FROM public.character_items
        WHERE user_id = 2 AND item_location = 1) <> 29
    THEN RAISE EXCEPTION 'The post-grant bag is not exact.'; END IF;
    INSERT INTO pet_skill_jade_result
    VALUES (true,v_item_revision,v_pet_revision,v_audit_id);
END
`$grant`$;
COMMIT;
SELECT 'PET_SKILL_MAGIC_JADE_GRANT_RESULT|' || jsonb_build_object(
  'changed',changed,'accountId',13,'characterId',2,
  'itemIds','[11074,11078,11086,11089]'::jsonb,
  'slots','[25,26,27,28]'::jsonb,
  'inventoryRevisionBefore',691,'inventoryRevisionAfter',692,
  'itemRevision',item_revision,'petRevision',pet_revision,
  'auditId',audit_id)::text FROM pet_skill_jade_result;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
