Set-StrictMode -Version Latest

function Get-PetUnsealVitalsEnergyApplySql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $snapshotSql = @"
INSERT INTO pet_unseal_vitals_energy_snapshot
WITH
$(Get-PetUnsealVitalsEnergyCalculatedMaximaCtesSql),
$(Get-PetUnsealVitalsEnergyStateCtesSql)
SELECT pins.item_revision,pins.gameplay_revision,
  pins.pet_skill_revision,pins.owner_merge_revision,pins.pins_valid,
  maxima.maximum_hp,maxima.maximum_mp,maxima.derived_max_hp,
  maxima.derived_max_mp,identity.exact_rows,identity.current_hp,
  identity.current_mp,identity.vitals_revision,identity.base_maximum_hp,
  identity.base_maximum_mp,pet.exact_rows,pet.current_energy,
  pet.maximum_energy,pet.pet_revision,evidence.energy_source_valid,
  evidence.seal_valid,evidence.unseal_valid,evidence.rejection_valid,
  evidence.pet_unseal_valid,evidence.pet_rejection_valid,
  hashes.account_hash,hashes.character_hash,hashes.pet_hash,
  hashes.other_pets_hash,hashes.items_hash,hashes.skills_hash,
  hashes.pet_stats_hash,hashes.owner_bonuses_hash,
  hashes.sealed_links_hash,
  pins.pins_valid
    AND maxima.maximum_hp=134341 AND maxima.maximum_mp=6047
    AND maxima.derived_max_hp=132840.5714285716
    AND maxima.derived_max_mp=5870.2857143
    AND identity.exact_rows=1 AND identity.base_maximum_hp=1500
    AND identity.base_maximum_mp=177
    AND pet.exact_rows=1 AND pet.maximum_energy=100
    AND evidence.energy_source_valid AND evidence.seal_valid
    AND evidence.unseal_valid AND evidence.rejection_valid
    AND evidence.pet_unseal_valid AND evidence.pet_rejection_valid
    AND hashes.account_hash=
      '2453e5b1896c660db23acf914400c5653ab8e2b867e7994510d548c419670f4f'
    AND hashes.character_hash=
      '92e83c1a986d6838ab8ffd93d312e0b08112a2278c36eceea0b1217ea1414c0d'
    AND hashes.pet_hash=
      'b14fb2d2b389618178e1716589c51f8119daf544bf73a785e1001f821d20156b'
    AND hashes.other_pets_hash=
      'c9bcf38cb8651b4357b9915954307f2e3d8ad3aac382084efa7f6e4f4a826be3'
    AND hashes.items_hash=
      '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    AND hashes.skills_hash=
      '1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3'
    AND hashes.pet_stats_hash=
      '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    AND hashes.owner_bonuses_hash=
      '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    AND hashes.sealed_links_hash=
      '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
FROM content_pins pins CROSS JOIN calculated_maxima maxima
CROSS JOIN identity_state identity CROSS JOIN pet_state pet
CROSS JOIN source_evidence evidence CROSS JOIN preservation_hashes hashes;
"@

    $sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
CREATE TEMP TABLE pet_unseal_vitals_energy_snapshot(
 item_revision text,gameplay_revision text,pet_skill_revision text,
 owner_merge_revision text,pins_valid boolean,maximum_hp integer,
 maximum_mp integer,derived_max_hp numeric,derived_max_mp numeric,
 identity_exact_rows bigint,current_hp integer,current_mp integer,
 vitals_revision bigint,base_maximum_hp integer,base_maximum_mp integer,
 pet_exact_rows bigint,current_energy integer,maximum_energy integer,
 pet_revision bigint,energy_source_valid boolean,seal_valid boolean,
 unseal_valid boolean,rejection_valid boolean,pet_unseal_valid boolean,
 pet_rejection_valid boolean,account_hash text,character_hash text,
 pet_hash text,other_pets_hash text,items_hash text,skills_hash text,
 pet_stats_hash text,owner_bonuses_hash text,sealed_links_hash text,
 authority_valid boolean
) ON COMMIT DROP;
CREATE TEMP TABLE pet_unseal_vitals_energy_result(
 status text NOT NULL,changed boolean NOT NULL,audit_id bigint NOT NULL,
 hp_before integer NOT NULL,hp_after integer NOT NULL,
 mp_before integer NOT NULL,mp_after integer NOT NULL,
 vitals_before bigint NOT NULL,vitals_after bigint NOT NULL,
 energy_before integer NOT NULL,energy_after integer NOT NULL,
 pet_revision_before bigint NOT NULL,pet_revision_after bigint NOT NULL
);
DO `$repair`$
DECLARE
 v_state record; v_snapshot_rows integer; v_count integer;
 v_receipt_count integer; v_receipt_id bigint;
 v_receipt_valid boolean; v_audit_id bigint;
 v_expected_payload jsonb;
BEGIN
 PERFORM pg_advisory_xact_lock(13,1413);
 IF NOT EXISTS (SELECT 1 FROM pg_trigger
      WHERE tgrelid='public.command_audit'::regclass
        AND tgname='trg_command_audit_immutable' AND tgenabled<>'D') THEN
   RAISE EXCEPTION 'The permanent command-audit immutability guard is absent.';
 END IF;

 PERFORM account.id FROM public.accounts account
   WHERE account.id=13 FOR UPDATE;
 PERFORM owner.id FROM public.character_base owner
   WHERE owner.id=2 AND owner.account_id=13 FOR UPDATE;
 PERFORM pet.id FROM public.character_pets pet WHERE pet.user_id=2
   ORDER BY pet.id FOR UPDATE;
 PERFORM item.id FROM public.character_items item WHERE item.user_id=2
   ORDER BY item.item_location,item.slot_index,item.id FOR SHARE;
 PERFORM skill.pet_id FROM public.character_pet_skills skill
   WHERE skill.pet_id IN
     (SELECT id FROM public.character_pets WHERE user_id=2)
   ORDER BY skill.pet_id,skill.slot_index FOR SHARE;
 PERFORM stat.pet_id FROM public.character_pet_stat_values stat
   WHERE stat.pet_id IN
     (SELECT id FROM public.character_pets WHERE user_id=2)
   ORDER BY stat.pet_id,stat.stat_code FOR SHARE;
 PERFORM bonus.pet_id FROM public.character_pet_character_bonuses bonus
   WHERE bonus.pet_id IN
     (SELECT id FROM public.character_pets WHERE user_id=2)
   ORDER BY bonus.pet_id,bonus.effect_code FOR SHARE;
 PERFORM talent.user_id FROM public.character_talents talent
   WHERE talent.user_id=2 ORDER BY talent.talent_id FOR SHARE;
 PERFORM link.id FROM public.sealed_pet_items link
   WHERE link.owner_character_id=2 ORDER BY link.id FOR SHARE;

 PERFORM publication.revision
   FROM public.item_template_content_publication publication
   WHERE publication.family='items' FOR SHARE;
 PERFORM publication.revision
   FROM public.gameplay_content_publication publication
   WHERE publication.family='gameplay' FOR SHARE;
 PERFORM publication.revision
   FROM public.pet_skill_content_publication publication
   WHERE publication.singleton FOR SHARE;
 PERFORM publication.revision
   FROM public.pet_owner_merge_content_publication publication
   WHERE publication.family='pet-owner-merge' FOR SHARE;
 PERFORM revision.revision FROM public.item_template_content_revisions revision
   WHERE revision.revision=
     '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
   FOR SHARE;
 PERFORM revision.revision FROM public.gameplay_content_revisions revision
   WHERE revision.revision=
     '897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62'
   FOR SHARE;
 PERFORM revision.revision FROM public.pet_skill_content_revisions revision
   WHERE revision.revision=
     '64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473'
   FOR SHARE;
 PERFORM revision.revision
   FROM public.pet_owner_merge_content_revisions revision
   WHERE revision.revision=
     'E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929'
   FOR SHARE;
 PERFORM definition.id
   FROM public.item_template_content_definitions definition
   WHERE definition.revision=
     '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
   ORDER BY definition.id FOR SHARE;
 PERFORM definition.id
   FROM public.item_attribute_content_definitions definition
   WHERE definition.revision=
     '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
   ORDER BY definition.id FOR SHARE;
 PERFORM definition.unlock_points
   FROM public.holy_suit_effect_content_definitions definition
   WHERE definition.revision=
     '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
   ORDER BY definition.unlock_points,definition.effect_key FOR SHARE;
 PERFORM definition.id FROM public.gameplay_talent_definitions definition
   WHERE definition.revision=
     '897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62'
   ORDER BY definition.id FOR SHARE;
 PERFORM definition.id
   FROM public.gameplay_talent_effect_definitions definition
   WHERE definition.revision=
     '897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62'
   ORDER BY definition.id FOR SHARE;
 PERFORM curve.family_type FROM public.pet_skill_curve_definitions curve
   WHERE curve.revision=
     '64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473'
   ORDER BY curve.family_type,curve.priority FOR SHARE;
 PERFORM step.family_type FROM public.pet_skill_curve_steps step
   WHERE step.revision=
     '64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473'
   ORDER BY step.family_type,step.priority,step.minimum_pet_rank FOR SHARE;
 PERFORM audit.id FROM public.pet_operation_audit audit
   WHERE audit.id IN (711,728,729) ORDER BY audit.id FOR SHARE;
 PERFORM audit.id FROM public.command_audit audit
   WHERE audit.id IN (9263,9266,9267)
      OR (audit.principal_type='developer' AND audit.principal_key='13'
       AND audit.aggregate_type='character_pet_value'
       AND audit.aggregate_key='character:2'
       AND audit.command_family='pet_unseal_vitals_energy_repair'
       AND audit.operation_id=decode('__OPERATION_HEX__','hex'))
   ORDER BY audit.id FOR SHARE;

 $snapshotSql
 SELECT count(*) INTO v_snapshot_rows
   FROM pet_unseal_vitals_energy_snapshot;
 IF v_snapshot_rows<>1 THEN
   RAISE EXCEPTION 'The exact repair authority snapshot is not singular.';
 END IF;
 SELECT * INTO v_state FROM pet_unseal_vitals_energy_snapshot;
 IF v_state.authority_valid IS NOT TRUE THEN
   RAISE EXCEPTION 'Pinned content, calculated maxima, evidence, or preserved state changed.';
 END IF;

 v_expected_payload := jsonb_build_object(
   'repairVersion',1,
   'source','offline_isolated_localdevelopment_unseal_repair',
   'accountId',13,'characterId',2,'petId',1,
   'previousCurrentHp',29350,'currentHp',134341,
   'previousCurrentMp',1287,'currentMp',6047,
   'previousVitalsRevision',9895,'currentVitalsRevision',9896,
   'previousEnergy',31,'currentEnergy',100,'maximumEnergy',100,
   'previousPetRevision',1413,'currentPetRevision',1414,
   'itemContentRevision',
     '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF',
   'gameplayContentRevision',
     '897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62',
   'petSkillContentRevision',
     '64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473',
   'petOwnerMergeContentRevision',
     'E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929',
   'calculatedMaximumHp',134341,'calculatedMaximumMp',6047,
   'derivedMaximumHp',132840.5714285716,
   'derivedMaximumMp',5870.2857143,
   'accountStateSha256',
     '2453e5b1896c660db23acf914400c5653ab8e2b867e7994510d548c419670f4f',
   'preservedCharacterStateSha256',
     '92e83c1a986d6838ab8ffd93d312e0b08112a2278c36eceea0b1217ea1414c0d',
   'preservedPetStateSha256',
     'b14fb2d2b389618178e1716589c51f8119daf544bf73a785e1001f821d20156b',
   'otherPetsSha256',
     'c9bcf38cb8651b4357b9915954307f2e3d8ad3aac382084efa7f6e4f4a826be3',
   'itemsSha256',
     '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762',
   'petSkillsSha256',
     '1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3',
   'petStatsSha256',
     '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c',
   'ownerBonusesSha256',
     '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945',
   'sealedLinksSha256',
     '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945',
   'sourceCommandAuditIds','[9263,9266,9267]'::jsonb,
   'sourcePetOperationAuditIds','[711,728,729]'::jsonb,
   'lastLoginTime','2026-08-14T03:54:27.136774Z',
   'lastLogoutTime','2026-08-14T03:55:32.027884Z',
   'checkpointOwnerGeneration',207,
   'petUpdatedAt','2026-08-14T03:55:19.547448Z');

 SELECT count(*),min(audit.id),COALESCE(bool_and(
     audit.request_hash=decode('__REQUEST_HASH_HEX__','hex')
     AND audit.outcome_code='repaired'
     AND audit.retention_policy='permanent'
     AND audit.detail_payload=v_expected_payload),false)
 INTO v_receipt_count,v_receipt_id,v_receipt_valid
 FROM public.command_audit audit
 WHERE audit.principal_type='developer' AND audit.principal_key='13'
   AND audit.aggregate_type='character_pet_value'
   AND audit.aggregate_key='character:2'
   AND audit.command_family='pet_unseal_vitals_energy_repair'
   AND audit.operation_id=decode('__OPERATION_HEX__','hex');
 IF v_receipt_count>1 THEN
   RAISE EXCEPTION 'More than one repair receipt exists.';
 END IF;
 IF v_receipt_count=1 THEN
   IF v_receipt_valid IS NOT TRUE
      OR v_state.current_hp<>v_state.maximum_hp
      OR v_state.current_mp<>v_state.maximum_mp
      OR v_state.vitals_revision<>9896
      OR v_state.current_energy<>v_state.maximum_energy
      OR v_state.pet_revision<>1414 THEN
     RAISE EXCEPTION 'The existing receipt or repaired state is inconsistent.';
   END IF;
   INSERT INTO pet_unseal_vitals_energy_result VALUES(
     'AlreadyApplied',false,v_receipt_id,29350,v_state.current_hp,
     1287,v_state.current_mp,9895,v_state.vitals_revision,
     31,v_state.current_energy,1413,v_state.pet_revision);
   RETURN;
 END IF;

 IF v_state.current_hp<>29350 OR v_state.current_mp<>1287
    OR v_state.vitals_revision<>9895 OR v_state.current_energy<>31
    OR v_state.pet_revision<>1413 THEN
   RAISE EXCEPTION 'Character 2 and pet 1 no longer match the exact source values.';
 END IF;

 UPDATE public.character_base
 SET "curHP"=v_state.maximum_hp,"curMP"=v_state.maximum_mp,
     vitals_revision=9896
 WHERE id=2 AND account_id=13 AND "curHP"=29350 AND "curMP"=1287
   AND vitals_revision=9895 AND checkpoint_owner_id IS NULL
   AND checkpoint_owner_generation=207;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>1 THEN
   RAISE EXCEPTION 'Character vitals did not advance exactly once.';
 END IF;
 UPDATE public.character_pets
 SET current_energy=maximum_energy,revision=1414
 WHERE id=1 AND user_id=2 AND current_energy=31 AND maximum_energy=100
   AND revision=1413 AND activity_state='owned' AND is_carried
   AND is_summoned AND NOT contributes_to_character;
 GET DIAGNOSTICS v_count=ROW_COUNT;
 IF v_count<>1 THEN
   RAISE EXCEPTION 'Pet energy did not advance exactly once.';
 END IF;
 SET CONSTRAINTS ALL IMMEDIATE;

 TRUNCATE pet_unseal_vitals_energy_snapshot;
 $snapshotSql
 SELECT count(*) INTO v_snapshot_rows
   FROM pet_unseal_vitals_energy_snapshot;
 IF v_snapshot_rows<>1 THEN
   RAISE EXCEPTION 'The post-repair authority snapshot is not singular.';
 END IF;
 SELECT * INTO v_state FROM pet_unseal_vitals_energy_snapshot;
 IF v_state.authority_valid IS NOT TRUE
    OR v_state.current_hp<>v_state.maximum_hp
    OR v_state.current_mp<>v_state.maximum_mp
    OR v_state.vitals_revision<>9896
    OR v_state.current_energy<>v_state.maximum_energy
    OR v_state.pet_revision<>1414 THEN
   RAISE EXCEPTION 'Post-repair readback or preservation validation failed.';
 END IF;

 INSERT INTO public.command_audit(
   principal_type,principal_key,aggregate_type,aggregate_key,
   command_family,operation_id,request_hash,outcome_code,
   detail_payload,retention_policy)
 VALUES('developer','13','character_pet_value','character:2',
   'pet_unseal_vitals_energy_repair',decode('__OPERATION_HEX__','hex'),
   decode('__REQUEST_HASH_HEX__','hex'),'repaired',v_expected_payload,
   'permanent') RETURNING id INTO v_audit_id;

 SELECT count(*),min(audit.id),COALESCE(bool_and(
     audit.request_hash=decode('__REQUEST_HASH_HEX__','hex')
     AND audit.outcome_code='repaired'
     AND audit.retention_policy='permanent'
     AND audit.detail_payload=v_expected_payload),false)
 INTO v_receipt_count,v_receipt_id,v_receipt_valid
 FROM public.command_audit audit
 WHERE audit.principal_type='developer' AND audit.principal_key='13'
   AND audit.aggregate_type='character_pet_value'
   AND audit.aggregate_key='character:2'
   AND audit.command_family='pet_unseal_vitals_energy_repair'
   AND audit.operation_id=decode('__OPERATION_HEX__','hex');
 IF v_receipt_count<>1 OR v_receipt_id<>v_audit_id
    OR v_receipt_valid IS NOT TRUE
    OR NOT EXISTS (SELECT 1 FROM pg_trigger
      WHERE tgrelid='public.command_audit'::regclass
        AND tgname='trg_command_audit_immutable' AND tgenabled<>'D') THEN
   RAISE EXCEPTION 'Permanent repair receipt readback failed.';
 END IF;
 INSERT INTO pet_unseal_vitals_energy_result VALUES(
   'Applied',true,v_audit_id,29350,v_state.current_hp,
   1287,v_state.current_mp,9895,v_state.vitals_revision,
   31,v_state.current_energy,1413,v_state.pet_revision);
END
`$repair`$;
COMMIT;
SELECT 'PET_UNSEAL_VITALS_ENERGY_RESULT|' || jsonb_build_object(
 'status',result.status,'changed',result.changed,
 'accountId',13,'characterId',2,'petId',1,
 'currentHpBefore',result.hp_before,'currentHpAfter',result.hp_after,
 'currentMpBefore',result.mp_before,'currentMpAfter',result.mp_after,
 'vitalsRevisionBefore',result.vitals_before,
 'vitalsRevisionAfter',result.vitals_after,
 'energyBefore',result.energy_before,'energyAfter',result.energy_after,
 'petRevisionBefore',result.pet_revision_before,
 'petRevisionAfter',result.pet_revision_after,
 'calculatedMaximumHp',134341,'calculatedMaximumMp',6047,
 'auditId',result.audit_id)::text
FROM pet_unseal_vitals_energy_result result;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
