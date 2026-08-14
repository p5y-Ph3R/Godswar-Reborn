Set-StrictMode -Version Latest

function Get-EmptySealJadeV3ApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $preservedHash = Get-EmptySealJadeV3PreservedHash
    $sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
CREATE TEMP TABLE empty_seal_jade_v3_result(
 audit_id bigint NOT NULL,item_audit_id bigint NOT NULL,
 item_instance_id bigint NOT NULL,item_revision text NOT NULL,
 item_hash text NOT NULL
);
DO `$grant`$
DECLARE
 v_identity integer; v_inv bigint; v_hp bigint; v_mp bigint;
 v_vitals bigint; v_bag integer; v_units bigint; v_items integer;
 v_source integer; v_granted integer; v_any integer; v_first integer;
 v_preserved text; v_pet jsonb; v_revision text; v_content boolean;
 v_immutable boolean; v_item_id bigint; v_item_audit bigint;
 v_audit bigint; v_item_hash text; v_count integer;
BEGIN
 PERFORM pg_advisory_xact_lock(13,10108);
 PERFORM id FROM public.accounts WHERE id=13 FOR UPDATE;
 PERFORM id FROM public.character_base WHERE id=2 FOR UPDATE;
 PERFORM id FROM public.character_items WHERE user_id=2
   ORDER BY item_location,slot_index,id FOR UPDATE;
 PERFORM id FROM public.character_pets WHERE user_id=2 ORDER BY id FOR UPDATE;
 PERFORM pet_id FROM public.character_pet_stat_values
   WHERE pet_id IN (SELECT id FROM character_pets WHERE user_id=2)
   ORDER BY pet_id,stat_code FOR UPDATE;
 PERFORM pet_id FROM public.character_pet_skills
   WHERE pet_id IN (SELECT id FROM character_pets WHERE user_id=2)
   ORDER BY pet_id,slot_index FOR UPDATE;
 PERFORM pet_id FROM public.character_pet_character_bonuses
   WHERE pet_id IN (SELECT id FROM character_pets WHERE user_id=2)
   ORDER BY pet_id,effect_code FOR UPDATE;
 PERFORM pet_id FROM public.character_pet_growth_previews
   WHERE user_id=2 ORDER BY pet_id FOR UPDATE;
 PERFORM pet_id FROM public.character_pet_basic_savvy_previews
   WHERE user_id=2 ORDER BY pet_id FOR UPDATE;
 PERFORM id FROM public.sealed_pet_items WHERE owner_character_id=2
   ORDER BY id FOR UPDATE;
 PERFORM character_id FROM public.pet_durable_stream_versions
   WHERE character_id=2 FOR UPDATE;
 PERFORM revision FROM public.item_template_content_publication
   WHERE family='items' FOR SHARE;
 PERFORM revision FROM public.item_template_content_revisions
   WHERE revision=(SELECT revision FROM item_template_content_publication
                   WHERE family='items') FOR SHARE;
 PERFORM id FROM public.item_template_content_definitions
   WHERE revision=(SELECT revision FROM item_template_content_publication
                   WHERE family='items') AND id=10108 FOR SHARE;
 PERFORM id FROM public.item_templates WHERE id=10108 FOR SHARE;

 IF EXISTS (SELECT 1 FROM public.command_audit
    WHERE principal_type='developer' AND principal_key='13'
      AND aggregate_type='character_inventory'
      AND aggregate_key='character:2'
      AND command_family='empty_seal_jade_grant_repeat'
      AND operation_id=decode('__OPERATION_HEX__','hex')) THEN
   RAISE EXCEPTION 'The permanent v3 Seal Jade receipt already exists.';
 END IF;

 WITH $(Get-EmptySealJadeV3StateCtesSql), immutable_audit AS (
   SELECT count(*)=1 immutable FROM pg_trigger
   WHERE tgrelid='public.command_audit'::regclass
     AND tgname='trg_command_audit_immutable' AND tgenabled<>'D')
 SELECT ids.exact_rows,ids.inventory_revision,ids.current_hp,
   ids.current_mp,ids.vitals_revision,inv.bag_rows,inv.bag_units,
   inv.total_item_rows,inv.source_rows,inv.granted_rows,
   inv.target_anywhere,inv.first_empty_slot,ps.preserved_hash,mp.state,
   cs.revision,cs.content_valid,ia.immutable
 INTO v_identity,v_inv,v_hp,v_mp,v_vitals,v_bag,v_units,v_items,
   v_source,v_granted,v_any,v_first,v_preserved,v_pet,v_revision,
   v_content,v_immutable
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN preserved_state ps CROSS JOIN main_pet mp
 CROSS JOIN content_state cs CROSS JOIN immutable_audit ia;
 IF v_identity<>1 OR v_inv<>735 OR v_hp<>134341 OR v_mp<>6047
    OR v_vitals<>9896 OR v_bag<>1 OR v_units<>94 OR v_items<>20
    OR v_source<>1 OR v_granted<>0 OR v_any<>0 OR v_first<>0
    OR v_preserved<>'__PRESERVED_HASH__'
    OR NOT COALESCE(v_content,false) OR NOT COALESCE(v_immutable,false)
    OR v_pet->>'id'<>'1' OR v_pet->>'revision'<>'1414'
    OR v_pet->>'energy'<>'100' OR v_pet->>'maximumEnergy'<>'100'
    OR v_pet->>'level'<>'120' OR (v_pet->>'rank')::numeric<>100
    OR v_pet->>'experience'<>'1254650135'
    OR (v_pet->>'bound')::boolean IS NOT TRUE
    OR v_pet->>'activity'<>'owned'
    OR (v_pet->>'carried')::boolean IS NOT TRUE
    OR (v_pet->>'summoned')::boolean IS NOT TRUE THEN
   RAISE EXCEPTION 'Character 2 no longer matches the exact repaired source state.';
 END IF;

 INSERT INTO public.character_items(
   user_id,item_location,slot_index,prop_id,item_quality,item_grade,
   bound,stack,item_exp,holy_suit_code)
 VALUES(2,1,0,10108,1,1,0,1,0,0)
 RETURNING id INTO v_item_id;

 INSERT INTO public.character_item_audit(
   source,action,user_id,item_location,slot_index,prop_id,
   item_quality,item_grade,item_exp,old_item)
 VALUES('localdev-empty-seal-jade-grant-v3','add',2,1,0,10108,
        1,1,0,NULL)
 RETURNING id INTO v_item_audit;

 UPDATE public.character_base SET inventory_revision=736
 WHERE id=2 AND account_id=13 AND inventory_revision=735;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>1 THEN
   RAISE EXCEPTION 'Inventory revision did not advance exactly once.';
 END IF;

 WITH $(Get-EmptySealJadeV3StateCtesSql)
 SELECT ids.exact_rows,ids.inventory_revision,ids.current_hp,
   ids.current_mp,ids.vitals_revision,inv.bag_rows,inv.bag_units,
   inv.total_item_rows,inv.source_rows,inv.granted_rows,
   inv.target_anywhere,inv.first_empty_slot,ps.preserved_hash,mp.state
 INTO v_identity,v_inv,v_hp,v_mp,v_vitals,v_bag,v_units,v_items,
   v_source,v_granted,v_any,v_first,v_preserved,v_pet
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN preserved_state ps CROSS JOIN main_pet mp;
 IF v_identity<>1 OR v_inv<>736 OR v_hp<>134341 OR v_mp<>6047
    OR v_vitals<>9896 OR v_bag<>2 OR v_units<>95 OR v_items<>21
    OR v_source<>1 OR v_granted<>1 OR v_any<>1 OR v_first<>1
    OR v_preserved<>'__PRESERVED_HASH__'
    OR v_pet->>'revision'<>'1414' OR v_pet->>'energy'<>'100'
    OR v_pet->>'maximumEnergy'<>'100' THEN
   RAISE EXCEPTION 'Post-grant preservation verification failed.';
 END IF;

 SELECT encode(sha256(convert_to(to_jsonb(i)::text,'UTF8')),'hex')
 INTO v_item_hash FROM public.character_items i
 WHERE i.id=v_item_id AND i.user_id=2 AND i.item_location=1
   AND i.slot_index=0 AND i.prop_id=10108 AND i.stack=1 AND i.bound=0;
 IF v_item_hash IS NULL THEN
   RAISE EXCEPTION 'Granted item state was not exact.';
 END IF;

 INSERT INTO public.command_audit(
   principal_type,principal_key,aggregate_type,aggregate_key,
   command_family,operation_id,request_hash,outcome_code,
   detail_payload,retention_policy)
 VALUES('developer','13','character_inventory','character:2',
   'empty_seal_jade_grant_repeat',decode('__OPERATION_HEX__','hex'),
   decode('__REQUEST_HASH_HEX__','hex'),'applied',jsonb_build_object(
    'fixtureVersion',3,'source','offline_isolated_localdevelopment_grant',
    'accountId',13,'characterId',2,'itemId',10108,
    'itemName','Seal Jade (Empty)','quantity',1,'slot',0,
    'previousInventoryRevision',735,'currentInventoryRevision',736,
    'preservedStateSha256',v_preserved,
    'currentHp',v_hp,'currentMp',v_mp,'vitalsRevision',v_vitals,
    'mainPetId',1,'mainPetRevision',1414,'mainPetEnergy',100,
    'publishedItemRevision',v_revision,'itemInstanceId',v_item_id,
    'grantedItemStateSha256',v_item_hash,
    'itemAuditIds',jsonb_build_array(v_item_audit)),
   'permanent') RETURNING id INTO v_audit;

 INSERT INTO empty_seal_jade_v3_result VALUES(
   v_audit,v_item_audit,v_item_id,v_revision,v_item_hash);
END
`$grant`$;
COMMIT;
SELECT 'EMPTY_SEAL_JADE_V3_RESULT|' || jsonb_build_object(
 'status','Applied','changed',true,'accountId',13,'characterId',2,
 'itemId',10108,'quantity',1,'slot',0,
 'inventoryRevisionBefore',735,'inventoryRevisionAfter',736,
 'itemRevision',item_revision,'itemInstanceId',item_instance_id,
 'grantedItemStateSha256',item_hash,'itemAuditId',item_audit_id,
 'auditId',audit_id)::text FROM empty_seal_jade_v3_result;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex).
        Replace('__PRESERVED_HASH__', $preservedHash)
}
