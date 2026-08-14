Set-StrictMode -Version Latest

function Get-PetRebirthSkillBookManifestValues {
    @'
    (0::smallint,10464,'Pet Skill:Wild Bump I',3900),
    (1::smallint,10465,'Pet Skill:Wild Bump II',3904),
    (2::smallint,10466,'Pet Skill:Wild Bump III',3908),
    (3::smallint,10467,'Pet Skill:Wild Bump IV',3912),
    (4::smallint,10468,'Pet Skill:Wild Bump V',3916),
    (5::smallint,10469,'Pet Skill:Wild Bump VI',3920),
    (6::smallint,10510,'Pet Skill: Wild Strength I',4500),
    (7::smallint,10511,'Pet Skill:Wild Strength  II',4503),
    (8::smallint,10512,'Pet Skill:Wild Strength  III',4507),
    (9::smallint,10513,'Pet Skill:Wild Strength  IV',4511),
    (10::smallint,10514,'Pet Skill:Wild Strength  V',4515),
    (11::smallint,10515,'Pet Skill:Wild Strength  VI',4519),
    (12::smallint,10590,'Pet Skill: Violent Strength I',5200),
    (13::smallint,10591,'Pet Skill:Violent Strength II',5204),
    (14::smallint,10592,'Pet Skill:Violent Strength III',5208),
    (15::smallint,10593,'Pet Skill:Violent Strength IV',5212),
    (16::smallint,10594,'Pet Skill:Violent Strength V',5216),
    (17::smallint,10595,'Pet Skill:Violent Strength VI',5220),
    (18::smallint,10700,'Pet Skill: Resolute Physique I',5600),
    (19::smallint,10701,'Pet Skill: Resolute Physique II',5604),
    (20::smallint,10702,'Pet Skill: Resolute Physique III',5608),
    (21::smallint,10703,'Pet Skill: Resolute Physique IV',5612),
    (22::smallint,10704,'Pet Skill: Resolute Physique V',5616),
    (23::smallint,10705,'Pet Skill: Resolute Physique VI',5620)
'@
}

function Get-PetRebirthSkillBookExpectedStatsValues {
    @'
    (1,97.440000,663.330000,17.941523,1.080000,2282.582760,899),
    (2,112.080000,862.330000,15.269860,1.160000,1971.583200,899),
    (3,4496.640000,729.670000,16.212965,1.190000,2088.355800,899),
    (4,92.150000,928.670000,16.353703,1.150000,2100.444360,899),
    (5,98.830000,995.000000,17.296806,1.140000,2212.416720,899),
    (6,102.800000,796.000000,14.625143,0.820000,1853.417160,899)
'@
}

function Get-PetRebirthSkillBookSourceBagIdArray {
    @'
ARRAY[
41104,41105,41106,41107,41151,41152,41110,41111,41153,
41122,41123,41125,41126,41127,41142,41143,41144,41145,
41146,41149,41150,41154,41155,41156,41157,41158,41159,
41160,41161,41162,41163,41164,41165,41166,41167,41168,
41169,41170,41171,41172,41173,41174,41175,41176,41177,
41179,41180,41182,41183,41184,41185,41186,41187,41188,
41189,41190,41191,41192,41193,41194,41195,41120]::bigint[]
'@
}

function Get-PetRebirthSpiritContentPredicate {
    @'
spirit.kind = 'consume item'
AND spirit.name_key = 'Pet10104'
AND spirit.display_name = 'Rebirth Spirit'
AND spirit.equipment_slot = 0
AND spirit.class_ids = '{}'::smallint[]
AND spirit.min_level IS NULL AND spirit.max_level IS NULL
AND spirit.hand IS NULL AND spirit.skill_flag IS NULL
AND spirit.texture = './Localization/en_us/UI/Texture/Icon2.gwo'
AND spirit.icon = '792,972'
AND spirit.stats = jsonb_build_object(
    'ID','10104','Type','consume item','Texture',
    './Localization/en_us/UI/Texture/Icon2.gwo','Icon','792,972',
    'Random','0','Distribution','0,0','Money','0','Overlap','99',
    'ItemType','16')
AND to_jsonb(mutable_spirit) - 'id' =
    to_jsonb(spirit) - 'revision' - 'id'
'@
}

function Get-PetRebirthSkillBookContentPredicate {
    @'
definition.kind = 'consume item'
AND definition.name_key = 'Pet' || expected.prop_id::text
AND definition.display_name = expected.display_name
AND definition.equipment_slot = 0
AND definition.class_ids = '{}'::smallint[]
AND definition.min_level IS NULL
AND definition.max_level IS NULL
AND definition.hand IS NULL
AND definition.skill_flag IS NULL
AND definition.texture =
    './Localization/en_us/UI/Texture/Icon2.gwo'
AND definition.icon = '216,972'
AND definition.stats = jsonb_build_object(
    'ID', expected.prop_id::text,
    'Type', 'consume item',
    'Texture', './Localization/en_us/UI/Texture/Icon2.gwo',
    'Icon', '216,972',
    'Random', '0',
    'Distribution', '0,0',
    'Money', '0',
    'Overlap', '99',
    'Use', '1',
    'ItemType', CASE WHEN expected.slot_index % 6 = 0
        THEN '4' ELSE '3' END,
    'PetSkill', expected.pet_skill::text)
AND mutable.kind = definition.kind
AND mutable.name_key = definition.name_key
AND mutable.display_name = definition.display_name
AND mutable.equipment_slot = definition.equipment_slot
AND mutable.class_ids = definition.class_ids
AND mutable.min_level IS NOT DISTINCT FROM definition.min_level
AND mutable.max_level IS NOT DISTINCT FROM definition.max_level
AND mutable.hand IS NOT DISTINCT FROM definition.hand
AND mutable.skill_flag IS NOT DISTINCT FROM definition.skill_flag
AND mutable.texture = definition.texture
AND mutable.icon = definition.icon
AND mutable.stats = definition.stats
'@
}

function Get-PetRebirthSkillBookStatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @'
BEGIN TRANSACTION READ ONLY;
WITH expected(slot_index,prop_id,display_name,pet_skill) AS (VALUES
__MANIFEST_VALUES__
), expected_stats(stat_code,initial_savvy,rarity_savvy,base_rate,
                   acceleration,added_savvy,revision) AS (VALUES
__STATS_VALUES__
), desired_bag(slot_index,prop_id,stack) AS (
    SELECT slot_index,prop_id,1::smallint FROM expected
    UNION ALL SELECT 24::smallint,10104,99::smallint
), publication AS (
    SELECT pointer.revision
    FROM public.item_template_content_publication pointer
    JOIN public.item_template_content_revisions release
      ON release.revision = pointer.revision
    WHERE pointer.family = 'items' AND release.sealed_at IS NOT NULL
), content_state AS (
    SELECT publication.revision,
           count(definition.id) AS definition_count,
           COALESCE(bool_and(__CONTENT_PREDICATE__), false)
           AND EXISTS (
             SELECT 1
             FROM public.item_template_content_definitions spirit
             JOIN public.item_templates mutable_spirit
               ON mutable_spirit.id = spirit.id
             WHERE spirit.revision = publication.revision
               AND spirit.id = 10104
               AND __SPIRIT_CONTENT_PREDICATE__) AS valid
    FROM expected
    CROSS JOIN publication
    LEFT JOIN public.item_template_content_definitions definition
      ON definition.revision = publication.revision
     AND definition.id = expected.prop_id
    LEFT JOIN public.item_templates mutable
      ON mutable.id = expected.prop_id
    GROUP BY publication.revision
), source_state AS (
    SELECT
      EXISTS (
        SELECT 1 FROM public.accounts account
        JOIN public.character_base character_row
          ON character_row.account_id = account.id
        WHERE account.id = 13 AND account.username = 'test2'
          AND account.login_status = 0
          AND character_row.id = 2 AND character_row.name = 'test2'
          AND character_row.lifecycle_state = 'active'
          AND character_row.checkpoint_owner_id IS NULL
          AND character_row.inventory_revision = 690) AS identity_valid,
      EXISTS (
        SELECT 1 FROM public.character_pets pet
        WHERE pet.id = 1 AND pet.user_id = 2 AND pet.name = 'Jolo'
          AND pet.species_id = 31 AND pet.level = 120
          AND pet.experience = 7597955 AND pet.revision = 1168
          AND pet.completed_rebirths = 6 AND pet.rebirths_remaining = 5
          AND pet.completed_pet_merges = 1 AND pet.bound
          AND pet.has_soul_contract AND pet.soul_contract_stage = 6
          AND pet.activity_state = 'owned' AND pet.is_carried
          AND pet.is_summoned AND NOT pet.contributes_to_character
          AND pet.updated_at =
              '2026-08-13 11:18:26.855536+00'::timestamptz) AS pet_valid,
      6 = (SELECT count(*) FROM expected_stats expected_stat
        JOIN public.character_pet_stat_values actual
          ON actual.pet_id = 1
         AND actual.stat_code = expected_stat.stat_code
         AND actual.initial_savvy = expected_stat.initial_savvy
         AND actual.rarity_added_savvy = expected_stat.rarity_savvy
         AND actual.base_growth_rate = expected_stat.base_rate
         AND actual.growth_acceleration = expected_stat.acceleration
         AND actual.added_savvy = expected_stat.added_savvy
         AND actual.revision = expected_stat.revision) AS stats_valid,
      62 = (SELECT count(*) FROM public.character_items item
        WHERE item.user_id = 2 AND item.item_location = 1)
      AND 1806 = (SELECT COALESCE(sum(item.stack), 0)
        FROM public.character_items item
        WHERE item.user_id = 2 AND item.item_location = 1)
      AND __SOURCE_BAG_IDS__ = (SELECT array_agg(item.id ORDER BY item.slot_index)
        FROM public.character_items item
        WHERE item.user_id = 2 AND item.item_location = 1) AS bag_valid,
      0 = (SELECT count(*)
        FROM public.character_items item
        JOIN public.sealed_pet_items sealed
          ON sealed.item_instance_id = item.id
        WHERE item.user_id = 2 AND item.item_location = 1) AS links_valid,
      0 = (SELECT count(*) FROM public.character_pet_growth_previews
        WHERE user_id = 2 AND pet_id = 1)
      AND 0 = (SELECT count(*)
        FROM public.character_pet_basic_savvy_previews
        WHERE user_id = 2 AND pet_id = 1) AS previews_valid
), post_bag AS (
    SELECT count(item.id) = 25
           AND (SELECT count(*) FROM public.character_items all_item
                WHERE all_item.user_id = 2
                  AND all_item.item_location = 1) = 25
           AND COALESCE(bool_and(
                item.item_quality = 1 AND item.item_grade = 1
                AND item.bound = 0 AND item.stack = desired.stack
                AND item.item_exp = 0 AND item.holy_suit_code = 0), false)
               AS valid,
           COALESCE(jsonb_agg(jsonb_build_object(
               'itemInstanceId', item.id,
               'slot', item.slot_index,
               'propId', item.prop_id,
               'stack', item.stack) ORDER BY desired.slot_index)
               FILTER (WHERE item.id IS NOT NULL), '[]'::jsonb) AS items
    FROM desired_bag desired
    LEFT JOIN public.character_items item
      ON item.user_id = 2 AND item.item_location = 1
     AND item.slot_index = desired.slot_index
     AND item.prop_id = desired.prop_id
), receipt AS (
    SELECT audit.* FROM public.command_audit audit
    WHERE audit.principal_type = 'developer'
      AND audit.principal_key = '13'
      AND audit.aggregate_type = 'character_pet_fixture'
      AND audit.aggregate_key = 'character:2|pet:1'
      AND audit.command_family = 'pet_rebirth_skillbook_fixture'
      AND audit.operation_id = decode('__OPERATION_HEX__', 'hex')
), receipt_state AS (
    SELECT count(*) AS receipt_count,
           COALESCE(bool_and(
             receipt.request_hash = decode('__REQUEST_HASH_HEX__', 'hex')
             AND receipt.outcome_code = 'applied'
              AND receipt.retention_policy = 'permanent'
              AND (receipt.detail_payload->>'fixtureVersion')::integer = 2
              AND receipt.detail_payload->>'source' =
                  'offline_isolated_localdevelopment_fixture'
              AND (receipt.detail_payload->>'accountId')::bigint = 13
              AND (receipt.detail_payload->>'characterId')::bigint = 2
              AND (receipt.detail_payload->>'petId')::bigint = 1
              AND receipt.detail_payload->'clearScope' =
                  jsonb_build_object(
                    'itemLocation',1,'minimumSlot',0,'maximumSlot',95)
              AND (receipt.detail_payload->>'removedRows')::integer = 62
              AND (receipt.detail_payload->>'removedUnits')::bigint = 1806
              AND (receipt.detail_payload->>'grantedItemCount')::integer = 25
              AND (receipt.detail_payload->>'grantedUnits')::bigint = 123
             AND (receipt.detail_payload->>'previousInventoryRevision')::bigint = 690
             AND (receipt.detail_payload->>'currentInventoryRevision')::bigint = 691
             AND (receipt.detail_payload->>'previousPetRevision')::bigint = 1168
             AND (receipt.detail_payload->>'currentPetRevision')::bigint = 1169
             AND (receipt.detail_payload->>'previousExperience')::bigint = 7597955
             AND (receipt.detail_payload->>'experienceDelta')::bigint = 1500000000
             AND (receipt.detail_payload->>'currentExperience')::bigint = 1507597955
             AND jsonb_array_length(receipt.detail_payload->'removedItemIds') = 62
             AND jsonb_array_length(receipt.detail_payload->'addedItems') = 25
             AND jsonb_array_length(receipt.detail_payload->'itemAuditIds') = 87
              AND receipt.detail_payload->'addedItems' = post_bag.items
              AND EXISTS (SELECT 1
                  FROM public.item_template_content_revisions release
                  WHERE release.revision =
                        receipt.detail_payload->>'publishedItemRevision'
                    AND release.sealed_at IS NOT NULL)
             AND post_bag.valid
             AND EXISTS (SELECT 1 FROM public.character_base c
                 WHERE c.id = 2 AND c.account_id = 13
                   AND c.inventory_revision = 691)
             AND EXISTS (SELECT 1 FROM public.character_pets p
                 WHERE p.id = 1 AND p.user_id = 2 AND p.level = 120
                   AND p.experience = 1507597955 AND p.revision = 1169)
              AND 87 = (SELECT count(*)
                 FROM public.character_item_audit item_audit
                 JOIN LATERAL jsonb_array_elements_text(
                     receipt.detail_payload->'itemAuditIds') linked(id)
                    ON item_audit.id = linked.id::bigint)
              AND 87 = (SELECT count(DISTINCT linked.id)
                  FROM jsonb_array_elements_text(
                      receipt.detail_payload->'itemAuditIds') linked(id))
             AND 25 = (SELECT count(*) FROM desired_bag expected_item
                 JOIN public.character_item_audit item_audit
                   ON item_audit.slot_index = expected_item.slot_index
                  AND item_audit.prop_id = expected_item.prop_id
                 JOIN LATERAL jsonb_array_elements_text(
                     receipt.detail_payload->'itemAuditIds') linked(id)
                   ON item_audit.id = linked.id::bigint
                 WHERE item_audit.source =
                     'localdev-pet-rebirth-skillbook-fixture-v1'
                   AND item_audit.action = 'add'
                   AND item_audit.user_id = 2
                   AND item_audit.item_location = 1
                   AND item_audit.item_quality = 1
                   AND item_audit.item_grade = 1
                    AND item_audit.item_exp = 0
                    AND item_audit.old_item IS NULL)
              AND 62 = (SELECT count(*)
                  FROM public.character_item_audit item_audit
                  JOIN LATERAL jsonb_array_elements_text(
                      receipt.detail_payload->'itemAuditIds') linked(id)
                    ON item_audit.id = linked.id::bigint
                  WHERE item_audit.source =
                      'localdev-pet-rebirth-skillbook-fixture-v1'
                    AND item_audit.action = 'delete'
                    AND item_audit.user_id = 2
                    AND item_audit.item_location = 1
                    AND item_audit.old_item IS NOT NULL)
              AND receipt.detail_payload->'removedItemIds' =
                  (SELECT COALESCE(jsonb_agg(
                      (item_audit.old_item->>'id')::bigint ORDER BY
                      (item_audit.old_item->>'slot_index')::smallint),
                      '[]'::jsonb)
                   FROM public.character_item_audit item_audit
                   JOIN LATERAL jsonb_array_elements_text(
                       receipt.detail_payload->'itemAuditIds') linked(id)
                     ON item_audit.id = linked.id::bigint
                   WHERE item_audit.source =
                       'localdev-pet-rebirth-skillbook-fixture-v1'
                     AND item_audit.action = 'delete'
                     AND item_audit.user_id = 2
                     AND item_audit.item_location = 1)
           ), false) AS valid,
           min(receipt.id) AS audit_id
    FROM receipt CROSS JOIN post_bag
)
SELECT 'PET_REBIRTH_SKILLBOOK_FIXTURE_STATUS|' || jsonb_build_object(
    'contentValid', content_state.valid
        AND content_state.definition_count = 24,
    'contentDefinitionCount', content_state.definition_count,
    'publishedItemRevision', content_state.revision,
    'identityReady', source_state.identity_valid,
    'petReady', source_state.pet_valid,
    'petStatsReady', source_state.stats_valid,
    'bagReady', source_state.bag_valid,
    'sealedLinksReady', source_state.links_valid,
    'previewsReady', source_state.previews_valid,
    'receiptCount', receipt_state.receipt_count,
    'receiptValid', receipt_state.valid,
    'receiptAuditId', receipt_state.audit_id,
    'inventoryRevision', (SELECT inventory_revision
        FROM public.character_base WHERE id = 2),
    'petExperience', (SELECT experience
        FROM public.character_pets WHERE id = 1),
    'petRevision', (SELECT revision
        FROM public.character_pets WHERE id = 1),
    'bagRows', (SELECT count(*) FROM public.character_items
        WHERE user_id = 2 AND item_location = 1),
    'bagUnits', (SELECT COALESCE(sum(stack), 0)
        FROM public.character_items WHERE user_id = 2 AND item_location = 1)
)::text
FROM content_state CROSS JOIN source_state CROSS JOIN receipt_state;
COMMIT;
'@
    $sql.Replace(
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
