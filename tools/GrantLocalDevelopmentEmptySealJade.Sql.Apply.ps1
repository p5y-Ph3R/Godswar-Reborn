Set-StrictMode -Version Latest

function Get-EmptySealJadeApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
CREATE TEMP TABLE empty_seal_jade_result(
 audit_id bigint NOT NULL,item_audit_id bigint NOT NULL,
 item_instance_id bigint NOT NULL,item_revision text NOT NULL,
 granted_hash text NOT NULL
);
DO `$grant`$
DECLARE
 v_item_revision text; v_content_valid boolean;
 v_identity_count integer; v_inventory_revision bigint;
 v_bag_rows integer; v_bag_units bigint; v_total_items integer;
 v_source_rows integer; v_granted_rows integer; v_target_anywhere integer;
 v_first_empty integer; v_items_hash text; v_pets_hash text;
 v_stats_hash text; v_skills_hash text; v_growth_hash text;
 v_savvy_hash text; v_sealed_hash text; v_pet_count integer;
 v_main_pet jsonb; v_character_hash text; v_account_hash text;
 v_item_instance_id bigint; v_item_audit_id bigint; v_audit_id bigint;
 v_granted_hash text; v_count integer;
BEGIN
 PERFORM pg_advisory_xact_lock(13,10108);
 PERFORM a.id FROM public.accounts a WHERE a.id=13 FOR UPDATE;
 PERFORM c.id FROM public.character_base c WHERE c.id=2 FOR UPDATE;
 PERFORM i.id FROM public.character_items i WHERE i.user_id=2
   ORDER BY i.item_location,i.slot_index,i.id FOR UPDATE;
 PERFORM p.id FROM public.character_pets p WHERE p.user_id=2
   ORDER BY p.id FOR UPDATE;
 PERFORM s.pet_id FROM public.character_pet_stat_values s
   WHERE s.pet_id IN (SELECT id FROM public.character_pets WHERE user_id=2)
   ORDER BY s.pet_id,s.stat_code FOR UPDATE;
 PERFORM s.pet_id FROM public.character_pet_skills s
   WHERE s.pet_id IN (SELECT id FROM public.character_pets WHERE user_id=2)
   ORDER BY s.pet_id,s.slot_index FOR UPDATE;
 PERFORM p.pet_id FROM public.character_pet_growth_previews p
   WHERE p.user_id=2 FOR UPDATE;
 PERFORM p.pet_id FROM public.character_pet_basic_savvy_previews p
   WHERE p.user_id=2 FOR UPDATE;
 PERFORM s.id FROM public.sealed_pet_items s
   WHERE s.owner_character_id=2 ORDER BY s.id FOR UPDATE;
 PERFORM p.revision FROM public.item_template_content_publication p
   WHERE p.family='items' FOR SHARE;
 PERFORM r.revision FROM public.item_template_content_revisions r
   WHERE r.revision=(SELECT revision
     FROM public.item_template_content_publication WHERE family='items')
   FOR SHARE;
 PERFORM d.id FROM public.item_template_content_definitions d
   WHERE d.revision=(SELECT revision
     FROM public.item_template_content_publication WHERE family='items')
     AND d.id=10108 FOR SHARE;
 PERFORM m.id FROM public.item_templates m WHERE m.id=10108 FOR SHARE;

 IF EXISTS (SELECT 1 FROM public.command_audit a
    WHERE a.principal_type='developer' AND a.principal_key='13'
      AND a.aggregate_type='character_inventory'
      AND a.aggregate_key='character:2'
      AND a.command_family='empty_seal_jade_grant'
      AND a.operation_id=decode('__OPERATION_HEX__','hex')) THEN
   RAISE EXCEPTION 'The permanent empty Seal Jade receipt already exists.';
 END IF;

 WITH $(Get-EmptySealJadeContentCtesSql)
 SELECT revision,content_valid INTO v_item_revision,v_content_valid
 FROM content_state;
 IF NOT COALESCE(v_content_valid,false) THEN
   RAISE EXCEPTION 'Published empty Seal Jade content is not exact.';
 END IF;

 WITH $(Get-EmptySealJadeStateCtesSql)
 SELECT ids.exact_rows,ids.inventory_revision,inv.bag_rows,inv.bag_units,
        inv.total_item_rows,inv.source_rows,inv.granted_rows,
        inv.target_anywhere,inv.first_empty_slot,inv.preserved_items_hash,
        auth.pets_hash,auth.stats_hash,auth.skills_hash,auth.growth_hash,
        auth.savvy_hash,auth.sealed_hash,auth.pet_count,auth.main_pet
 INTO v_identity_count,v_inventory_revision,v_bag_rows,v_bag_units,
      v_total_items,v_source_rows,v_granted_rows,v_target_anywhere,
      v_first_empty,v_items_hash,v_pets_hash,v_stats_hash,v_skills_hash,
      v_growth_hash,v_savvy_hash,v_sealed_hash,v_pet_count,v_main_pet
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN authority_hashes auth;
 IF v_identity_count<>1 OR v_inventory_revision<>729
    OR v_bag_rows<>1 OR v_bag_units<>94 OR v_total_items<>20
    OR v_source_rows<>1 OR v_granted_rows<>0 OR v_target_anywhere<>0
    OR v_first_empty<>0
    OR v_items_hash<>'959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    OR v_pets_hash<>'f38ce9fe012923913b49966777a064d2ff6830daac4ef0dddc3ac58cb8f1fb94'
    OR v_stats_hash<>'66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    OR v_skills_hash<>'1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3'
    OR v_growth_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    OR v_savvy_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    OR v_sealed_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    OR v_pet_count<>3 THEN
   RAISE EXCEPTION 'Character 2 no longer matches the exact source state.';
 END IF;
 IF v_main_pet->>'petId'<>'1' OR v_main_pet->>'speciesId'<>'31'
    OR (v_main_pet->>'rank')::numeric<>100
    OR v_main_pet->>'revision'<>'1406'
    OR (v_main_pet->>'bound')::boolean IS NOT TRUE
    OR v_main_pet->>'activityState'<>'owned'
    OR (v_main_pet->>'isCarried')::boolean IS NOT TRUE
    OR (v_main_pet->>'isSummoned')::boolean IS NOT TRUE
    OR (v_main_pet->>'contributes')::boolean IS NOT FALSE
    OR (v_main_pet->>'soulContract')::boolean IS NOT TRUE
    OR v_main_pet->>'soulStage'<>'6' THEN
   RAISE EXCEPTION 'The pinned main-pet state is not exact.';
 END IF;

 SELECT encode(sha256(convert_to((to_jsonb(c)-'inventory_revision')::text,
        'UTF8')),'hex') INTO v_character_hash
 FROM public.character_base c WHERE c.id=2;
 SELECT encode(sha256(convert_to(to_jsonb(a)::text,'UTF8')),'hex')
 INTO v_account_hash FROM public.accounts a WHERE a.id=13;

 INSERT INTO public.character_items(
   user_id,item_location,slot_index,prop_id,item_quality,item_grade,
   bound,stack,item_exp,holy_suit_code)
 VALUES(2,1,0,10108,1,1,0,1,0,0)
 RETURNING id INTO v_item_instance_id;

 INSERT INTO public.character_item_audit(
   source,action,user_id,item_location,slot_index,prop_id,
   item_quality,item_grade,item_exp,old_item)
 VALUES('localdev-empty-seal-jade-grant-v1','add',2,1,0,10108,
        1,1,0,NULL)
 RETURNING id INTO v_item_audit_id;

 UPDATE public.character_base SET inventory_revision=730
 WHERE id=2 AND account_id=13 AND inventory_revision=729;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>1 THEN
   RAISE EXCEPTION 'Inventory revision did not advance exactly once.';
 END IF;

 WITH $(Get-EmptySealJadeStateCtesSql)
 SELECT ids.exact_rows,ids.inventory_revision,inv.bag_rows,inv.bag_units,
        inv.total_item_rows,inv.source_rows,inv.granted_rows,
        inv.target_anywhere,inv.first_empty_slot,inv.preserved_items_hash,
        auth.pets_hash,auth.stats_hash,auth.skills_hash,auth.growth_hash,
        auth.savvy_hash,auth.sealed_hash,auth.pet_count
 INTO v_identity_count,v_inventory_revision,v_bag_rows,v_bag_units,
      v_total_items,v_source_rows,v_granted_rows,v_target_anywhere,
      v_first_empty,v_items_hash,v_pets_hash,v_stats_hash,v_skills_hash,
      v_growth_hash,v_savvy_hash,v_sealed_hash,v_pet_count
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN authority_hashes auth;
 IF v_identity_count<>1 OR v_inventory_revision<>730
    OR v_bag_rows<>2 OR v_bag_units<>95 OR v_total_items<>21
    OR v_source_rows<>1 OR v_granted_rows<>1 OR v_target_anywhere<>1
    OR v_first_empty<>1
    OR v_items_hash<>'959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    OR v_pets_hash<>'f38ce9fe012923913b49966777a064d2ff6830daac4ef0dddc3ac58cb8f1fb94'
    OR v_stats_hash<>'66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    OR v_skills_hash<>'1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3'
    OR v_growth_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    OR v_savvy_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    OR v_sealed_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    OR v_pet_count<>3 THEN
   RAISE EXCEPTION 'Post-grant preservation verification failed.';
 END IF;
 IF (SELECT encode(sha256(convert_to((to_jsonb(c)-'inventory_revision')::text,
       'UTF8')),'hex') FROM public.character_base c WHERE c.id=2)
       <>v_character_hash
    OR (SELECT encode(sha256(convert_to(to_jsonb(a)::text,'UTF8')),'hex')
        FROM public.accounts a WHERE a.id=13)<>v_account_hash THEN
   RAISE EXCEPTION 'Non-inventory character/account state changed.';
 END IF;
 SELECT encode(sha256(convert_to(to_jsonb(i)::text,'UTF8')),'hex')
 INTO v_granted_hash FROM public.character_items i
 WHERE i.id=v_item_instance_id AND i.user_id=2
   AND i.item_location=1 AND i.slot_index=0 AND i.prop_id=10108;

 INSERT INTO public.command_audit(
   principal_type,principal_key,aggregate_type,aggregate_key,
   command_family,operation_id,request_hash,outcome_code,
   detail_payload,retention_policy)
 VALUES('developer','13','character_inventory','character:2',
   'empty_seal_jade_grant',decode('__OPERATION_HEX__','hex'),
   decode('__REQUEST_HASH_HEX__','hex'),'applied',jsonb_build_object(
    'fixtureVersion',1,'source','offline_isolated_localdevelopment_grant',
    'accountId',13,'characterId',2,'itemId',10108,
    'itemName','Seal Jade (Empty)','quantity',1,'slot',0,
    'previousInventoryRevision',729,'currentInventoryRevision',730,
    'preservedItemsSha256',v_items_hash,'petsSha256',v_pets_hash,
    'petStatsSha256',v_stats_hash,'petSkillsSha256',v_skills_hash,
    'growthPreviewSha256',v_growth_hash,'savvyPreviewSha256',v_savvy_hash,
    'sealedLinksSha256',v_sealed_hash,
    'grantedItemInstanceId',v_item_instance_id,
    'grantedItemStateSha256',v_granted_hash,
    'mainPetId',1,'mainPetSpeciesId',31,'mainPetRank',100,
    'mainPetRevision',1406,'mainPetBound',true,
    'mainPetIsCarried',true,'mainPetIsSummoned',true,
    'publishedItemRevision',v_item_revision,
    'itemAuditIds',jsonb_build_array(v_item_audit_id)),
   'permanent') RETURNING id INTO v_audit_id;
 INSERT INTO empty_seal_jade_result VALUES(
   v_audit_id,v_item_audit_id,v_item_instance_id,v_item_revision,
   v_granted_hash);
END
`$grant`$;
COMMIT;
SELECT 'EMPTY_SEAL_JADE_RESULT|' || jsonb_build_object(
 'status','Applied','changed',true,'accountId',13,'characterId',2,
 'itemId',10108,'quantity',1,'slot',0,
 'inventoryRevisionBefore',729,'inventoryRevisionAfter',730,
 'itemRevision',item_revision,'grantedItemInstanceId',item_instance_id,
 'grantedItemStateSha256',granted_hash,'itemAuditId',item_audit_id,
 'auditId',audit_id)::text FROM empty_seal_jade_result;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
