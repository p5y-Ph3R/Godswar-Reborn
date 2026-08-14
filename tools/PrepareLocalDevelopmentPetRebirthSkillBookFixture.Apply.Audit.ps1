Set-StrictMode -Version Latest

function Get-PetRebirthSkillBookAuditSql {
    @'
    WITH audited AS (
        INSERT INTO public.character_item_audit (
            source,action,user_id,item_location,slot_index,prop_id,
            item_quality,item_grade,item_exp,old_item)
        SELECT 'localdev-pet-rebirth-skillbook-fixture-v1','delete',2,1,
               (old_item->>'slot_index')::smallint,
               (old_item->>'prop_id')::integer,
               (old_item->>'item_quality')::smallint,
               (old_item->>'item_grade')::smallint,
               (old_item->>'item_exp')::integer,old_item
        FROM fixture_deleted_items
        ORDER BY (old_item->>'slot_index')::smallint RETURNING id)
    INSERT INTO fixture_item_audit_ids
    SELECT id,'delete' FROM audited;
    WITH audited AS (
        INSERT INTO public.character_item_audit (
            source,action,user_id,item_location,slot_index,prop_id,
            item_quality,item_grade,item_exp,old_item)
        SELECT 'localdev-pet-rebirth-skillbook-fixture-v1','add',2,1,
               slot_index,prop_id,1,1,0,NULL
        FROM fixture_added_items ORDER BY slot_index RETURNING id)
    INSERT INTO fixture_item_audit_ids
    SELECT id,'add' FROM audited;
    IF (SELECT count(*) FROM fixture_item_audit_ids) <> 87 THEN
        RAISE EXCEPTION 'Item-audit append was not exact.';
    END IF;

    SELECT jsonb_agg((old_item->>'id')::bigint
             ORDER BY (old_item->>'slot_index')::smallint)
      INTO v_removed_ids FROM fixture_deleted_items;
    SELECT jsonb_agg(jsonb_build_object(
             'itemInstanceId',item_instance_id,'slot',slot_index,
             'propId',prop_id,'stack',stack) ORDER BY slot_index)
      INTO v_added_items FROM fixture_added_items;
    SELECT jsonb_agg(id ORDER BY id) INTO v_item_audit_ids
    FROM fixture_item_audit_ids;
    INSERT INTO public.command_audit (
        principal_type,principal_key,aggregate_type,aggregate_key,
        command_family,operation_id,request_hash,outcome_code,
        detail_payload,retention_policy)
    VALUES (
        'developer','13','character_pet_fixture','character:2|pet:1',
        'pet_rebirth_skillbook_fixture',
        decode('__OPERATION_HEX__','hex'),
        decode('__REQUEST_HASH_HEX__','hex'),'applied',
        jsonb_build_object(
          'fixtureVersion',2,
          'source','offline_isolated_localdevelopment_fixture',
          'accountId',13,'characterId',2,'petId',1,
          'clearScope',jsonb_build_object(
              'itemLocation',1,'minimumSlot',0,'maximumSlot',95),
          'removedRows',62,'removedUnits',1806,
          'removedItemIds',v_removed_ids,
          'grantedItemCount',25,'grantedUnits',123,
          'addedItems',v_added_items,
          'previousInventoryRevision',690,
          'currentInventoryRevision',691,
          'previousExperience',7597955,
          'experienceDelta',1500000000,
          'currentExperience',1507597955,
          'previousPetRevision',1168,'currentPetRevision',1169,
          'publishedItemRevision',v_publication,
          'itemAuditIds',v_item_audit_ids),
        'permanent') RETURNING id INTO v_audit_id;
    INSERT INTO fixture_result VALUES (
        true,62,25,7597955,1507597955,1168,1169,690,691,
        v_publication,v_audit_id);
'@
}
