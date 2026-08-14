Set-StrictMode -Version Latest

function Get-PlatypusSkillKitApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
CREATE TEMP TABLE expected_kit(
 ordinal smallint PRIMARY KEY,slot_index smallint UNIQUE NOT NULL,
 prop_id integer UNIQUE NOT NULL,display_name text NOT NULL,
 icon text NOT NULL,item_type smallint,runtime_skill_id integer
) ON COMMIT DROP;
INSERT INTO expected_kit VALUES
$(Get-PlatypusSkillKitItemValuesSql);
CREATE TEMP TABLE expected_curve(
 priority smallint PRIMARY KEY,required_accuracy numeric NOT NULL,
 first_runtime_skill_id integer NOT NULL,runtime_ids integer[] NOT NULL,
 minimum_ranks smallint[] NOT NULL,absolute_values numeric[] NOT NULL
) ON COMMIT DROP;
INSERT INTO expected_curve VALUES
$(Get-PlatypusSkillKitCurveValuesSql);
CREATE TEMP TABLE platypus_skill_kit_result(
 audit_id bigint NOT NULL,item_revision text NOT NULL,
 learned_revision text NOT NULL,pet_revision text NOT NULL,
 granted_hash text NOT NULL
);
DO `$grant`$
DECLARE
 v_item_revision text; v_learned_revision text; v_pet_revision text;
 v_item_valid boolean; v_activation_valid boolean;
 v_identity_count integer; v_inventory_revision bigint;
 v_bag_rows integer; v_bag_units bigint; v_total_items integer;
 v_source_rows integer; v_kit_rows integer; v_target_anywhere integer;
 v_items_hash text; v_pets_hash text; v_stats_hash text; v_skills_hash text;
 v_growth_previews integer; v_savvy_previews integer; v_sealed_links integer;
 v_main_pet jsonb; v_character_hash text; v_account_hash text;
 v_item_audit_ids jsonb; v_granted_hash text; v_audit_id bigint;
 v_count integer;
BEGIN
 PERFORM pg_advisory_xact_lock(13,2);
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
 PERFORM p.revision FROM public.pet_skill_content_publication p
   WHERE p.singleton FOR SHARE;
 PERFORM p.revision FROM public.pet_content_publication p
   WHERE p.family='pets' FOR SHARE;
 PERFORM m.id FROM public.item_templates m
   JOIN expected_kit e ON e.prop_id=m.id ORDER BY m.id FOR SHARE;

 IF EXISTS (SELECT 1 FROM public.command_audit a
    WHERE a.principal_type='developer' AND a.principal_key='13'
      AND a.aggregate_type='character_inventory'
      AND a.aggregate_key='character:2'
      AND a.command_family='platypus_skill_kit_grant'
      AND a.operation_id=decode('__OPERATION_HEX__','hex')) THEN
   RAISE EXCEPTION 'The permanent Platypus skill-kit receipt already exists.';
 END IF;

 WITH
 $(Get-PlatypusSkillKitContentCtesSql)
 SELECT item_revision,learned_revision,pet_revision,
        item_content_valid,activation_content_valid
 INTO v_item_revision,v_learned_revision,v_pet_revision,
      v_item_valid,v_activation_valid
 FROM content_state;
 IF NOT COALESCE(v_item_valid,false)
    OR NOT COALESCE(v_activation_valid,false) THEN
   RAISE EXCEPTION 'Published Platypus item or activation content is not exact.';
 END IF;

 WITH
 $(Get-PlatypusSkillKitStateCtesSql)
 SELECT ids.exact_rows,ids.inventory_revision,inv.bag_rows,inv.bag_units,
        inv.total_item_rows,inv.source_rows,inv.kit_rows,
        inv.target_anywhere,inv.preserved_items_hash,auth.pets_hash,
        auth.stats_hash,auth.skills_hash,auth.growth_previews,
        auth.savvy_previews,auth.sealed_links,auth.main_pet
 INTO v_identity_count,v_inventory_revision,v_bag_rows,v_bag_units,
      v_total_items,v_source_rows,v_kit_rows,v_target_anywhere,
      v_items_hash,v_pets_hash,v_stats_hash,v_skills_hash,
      v_growth_previews,v_savvy_previews,v_sealed_links,v_main_pet
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN authority_hashes auth;
 IF v_identity_count<>1 OR v_inventory_revision<>721
    OR v_bag_rows<>1 OR v_bag_units<>94 OR v_total_items<>20
    OR v_source_rows<>1 OR v_kit_rows<>0 OR v_target_anywhere<>0
    OR v_items_hash<>'959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    OR v_pets_hash<>'8497f0dc0e742fed1d4c1b43e06b17b80c3349ddedd0890cbf697b400cd4d195'
    OR v_stats_hash<>'66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    OR v_skills_hash<>'5757df22a3d5f93b06705be8436ca1a80253476ba66728c6cd38ad397abced61'
    OR v_growth_previews<>0 OR v_savvy_previews<>0 OR v_sealed_links<>0 THEN
   RAISE EXCEPTION 'Character 2 no longer matches the exact preserve-only source.';
 END IF;
 IF v_main_pet->>'petId'<>'1' OR v_main_pet->>'speciesId'<>'40'
    OR (v_main_pet->>'rank')::numeric<>100 OR v_main_pet->>'revision'<>'1328'
    OR (v_main_pet->>'isCarried')::boolean IS NOT TRUE
    OR (v_main_pet->>'isSummoned')::boolean IS NOT TRUE
    OR (v_main_pet->>'contributes')::boolean IS NOT FALSE THEN
   RAISE EXCEPTION 'The pinned main-pet presence state is not exact.';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM public.command_inbox i
    WHERE i.id=9031 AND i.principal_key='13'
      AND i.command_family='pet_presence_transition' AND i.audit_id=9050
      AND i.result_code='pet_result'
      AND i.result_payload->>'PetId'='1'
      AND i.result_payload->>'PresenceOperation'='1'
      AND i.result_payload->>'IsCarried'='true'
      AND i.result_payload->>'IsSummoned'='true'
      AND i.result_payload->>'AuditReference'='9050')
    OR NOT EXISTS (SELECT 1 FROM public.command_audit a
      WHERE a.id=9050 AND a.principal_key='13'
        AND a.command_family='pet_presence_transition'
        AND a.outcome_code='committed' AND a.detail_payload->>'status'='4') THEN
   RAISE EXCEPTION 'The latest durable pet-presence provenance is not exact.';
 END IF;

 SELECT encode(sha256(convert_to((to_jsonb(c)-'inventory_revision')::text,
        'UTF8')),'hex') INTO v_character_hash
 FROM public.character_base c WHERE c.id=2;
 SELECT encode(sha256(convert_to(to_jsonb(a)::text,'UTF8')),'hex')
 INTO v_account_hash FROM public.accounts a WHERE a.id=13;

 INSERT INTO public.character_items(
   user_id,item_location,slot_index,prop_id,item_quality,item_grade,
   bound,stack,item_exp,holy_suit_code)
 SELECT 2,1,e.slot_index,e.prop_id,1,1,0,1,0,0
 FROM expected_kit e ORDER BY e.ordinal;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>7 THEN RAISE EXCEPTION 'Seven-item append was not exact.'; END IF;

 WITH audited AS (
  INSERT INTO public.character_item_audit(
    source,action,user_id,item_location,slot_index,prop_id,
    item_quality,item_grade,item_exp,old_item)
  SELECT 'localdev-platypus-skill-kit-grant-v1','add',2,1,
         e.slot_index,e.prop_id,1,1,0,NULL
  FROM expected_kit e ORDER BY e.ordinal RETURNING id)
 SELECT jsonb_agg(id ORDER BY id) INTO v_item_audit_ids FROM audited;
 IF jsonb_array_length(v_item_audit_ids)<>7 THEN
   RAISE EXCEPTION 'Seven permanent item audits were not appended.';
 END IF;

 UPDATE public.character_base SET inventory_revision=722
 WHERE id=2 AND account_id=13 AND inventory_revision=721;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>1 THEN RAISE EXCEPTION 'Inventory revision did not advance once.'; END IF;

 WITH
 $(Get-PlatypusSkillKitStateCtesSql)
 SELECT ids.inventory_revision,inv.bag_rows,inv.bag_units,
        inv.total_item_rows,inv.source_rows,inv.kit_rows,
        inv.target_anywhere,inv.preserved_items_hash,auth.pets_hash,
        auth.stats_hash,auth.skills_hash,auth.growth_previews,
        auth.savvy_previews,auth.sealed_links
 INTO v_inventory_revision,v_bag_rows,v_bag_units,v_total_items,
      v_source_rows,v_kit_rows,v_target_anywhere,v_items_hash,
      v_pets_hash,v_stats_hash,v_skills_hash,v_growth_previews,
      v_savvy_previews,v_sealed_links
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN authority_hashes auth;
 IF v_inventory_revision<>722 OR v_bag_rows<>8 OR v_bag_units<>101
    OR v_total_items<>27 OR v_source_rows<>1 OR v_kit_rows<>7
    OR v_target_anywhere<>7
    OR v_items_hash<>'959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    OR v_pets_hash<>'8497f0dc0e742fed1d4c1b43e06b17b80c3349ddedd0890cbf697b400cd4d195'
    OR v_stats_hash<>'66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    OR v_skills_hash<>'5757df22a3d5f93b06705be8436ca1a80253476ba66728c6cd38ad397abced61'
    OR v_growth_previews<>0 OR v_savvy_previews<>0 OR v_sealed_links<>0 THEN
   RAISE EXCEPTION 'Post-grant preservation verification failed.';
 END IF;
 IF (SELECT encode(sha256(convert_to((to_jsonb(c)-'inventory_revision')::text,
       'UTF8')),'hex') FROM public.character_base c WHERE c.id=2)
       <>v_character_hash
    OR (SELECT encode(sha256(convert_to(to_jsonb(a)::text,'UTF8')),'hex')
        FROM public.accounts a WHERE a.id=13)<>v_account_hash THEN
   RAISE EXCEPTION 'Non-inventory character/account state changed.';
 END IF;
 SELECT encode(sha256(convert_to(jsonb_agg(to_jsonb(i)
          ORDER BY i.slot_index,i.id)::text,'UTF8')),'hex')
 INTO v_granted_hash FROM public.character_items i
 WHERE i.user_id=2 AND i.item_location=1
   AND i.slot_index BETWEEN 25 AND 31;

 INSERT INTO public.command_audit(
   principal_type,principal_key,aggregate_type,aggregate_key,
   command_family,operation_id,request_hash,outcome_code,
   detail_payload,retention_policy)
 VALUES('developer','13','character_inventory','character:2',
   'platypus_skill_kit_grant',decode('__OPERATION_HEX__','hex'),
   decode('__REQUEST_HASH_HEX__','hex'),'applied',jsonb_build_object(
    'fixtureVersion',1,'source','offline_isolated_localdevelopment_grant',
    'accountId',13,'characterId',2,
    'itemIds','[11080,10530,10531,10532,10533,10534,10535]'::jsonb,
    'slots','[25,26,27,28,29,30,31]'::jsonb,
    'quantities','[1,1,1,1,1,1,1]'::jsonb,
    'previousInventoryRevision',721,'currentInventoryRevision',722,
    'preservedItemsSha256',v_items_hash,'petsSha256',v_pets_hash,
    'petStatsSha256',v_stats_hash,'petSkillsSha256',v_skills_hash,
    'grantedItemsStateSha256',v_granted_hash,
    'mainPetId',1,'mainPetSpeciesId',40,'mainPetRank',100,
    'mainPetRevision',1328,'mainPetIsCarried',true,
    'mainPetIsSummoned',true,'latestPresenceInboxId',9031,
    'latestPresenceAuditId',9050,'publishedItemRevision',v_item_revision,
    'publishedLearnedSkillRevision',v_learned_revision,
    'publishedPetRevision',v_pet_revision,'itemAuditIds',v_item_audit_ids),
   'permanent') RETURNING id INTO v_audit_id;
 INSERT INTO platypus_skill_kit_result VALUES(
   v_audit_id,v_item_revision,v_learned_revision,v_pet_revision,v_granted_hash);
END
`$grant`$;
COMMIT;
SELECT 'PLATYPUS_SKILL_KIT_RESULT|' || jsonb_build_object(
 'status','Applied','changed',true,'accountId',13,'characterId',2,
 'itemIds','[11080,10530,10531,10532,10533,10534,10535]'::jsonb,
 'slots','[25,26,27,28,29,30,31]'::jsonb,
 'inventoryRevisionBefore',721,'inventoryRevisionAfter',722,
 'itemRevision',item_revision,'learnedSkillRevision',learned_revision,
 'petRevision',pet_revision,'grantedItemsStateSha256',granted_hash,
 'auditId',audit_id)::text FROM platypus_skill_kit_result;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
