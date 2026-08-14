Set-StrictMode -Version Latest

function Get-PetUnsealVitalsEnergyStatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN READ ONLY;
WITH
$(Get-PetUnsealVitalsEnergyCalculatedMaximaCtesSql),
$(Get-PetUnsealVitalsEnergyStateCtesSql),
repair_receipt AS (
 SELECT audit.* FROM public.command_audit audit
 WHERE audit.principal_type='developer' AND audit.principal_key='13'
   AND audit.aggregate_type='character_pet_value'
   AND audit.aggregate_key='character:2'
   AND audit.command_family='pet_unseal_vitals_energy_repair'
   AND audit.operation_id=decode('__OPERATION_HEX__','hex')
),
receipt_state AS (
 SELECT count(*) receipt_count,min(receipt.id) receipt_audit_id,
   COALESCE(bool_and(
     receipt.request_hash=decode('__REQUEST_HASH_HEX__','hex')
     AND receipt.outcome_code='repaired'
     AND receipt.retention_policy='permanent'
     AND receipt.detail_payload->>'repairVersion'='1'
     AND receipt.detail_payload->>'source'=
       'offline_isolated_localdevelopment_unseal_repair'
     AND receipt.detail_payload->>'accountId'='13'
     AND receipt.detail_payload->>'characterId'='2'
     AND receipt.detail_payload->>'petId'='1'
     AND receipt.detail_payload->>'previousCurrentHp'='29350'
     AND receipt.detail_payload->>'currentHp'='134341'
     AND receipt.detail_payload->>'previousCurrentMp'='1287'
     AND receipt.detail_payload->>'currentMp'='6047'
     AND receipt.detail_payload->>'previousVitalsRevision'='9895'
     AND receipt.detail_payload->>'currentVitalsRevision'='9896'
     AND receipt.detail_payload->>'previousEnergy'='31'
     AND receipt.detail_payload->>'currentEnergy'='100'
     AND receipt.detail_payload->>'maximumEnergy'='100'
     AND receipt.detail_payload->>'previousPetRevision'='1413'
     AND receipt.detail_payload->>'currentPetRevision'='1414'
     AND receipt.detail_payload->>'itemContentRevision'=
       '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
     AND receipt.detail_payload->>'gameplayContentRevision'=
       '897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62'
     AND receipt.detail_payload->>'petSkillContentRevision'=
       '64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473'
     AND receipt.detail_payload->>'petOwnerMergeContentRevision'=
       'E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929'
     AND receipt.detail_payload->>'accountStateSha256'=
       '2453e5b1896c660db23acf914400c5653ab8e2b867e7994510d548c419670f4f'
     AND receipt.detail_payload->>'preservedCharacterStateSha256'=
       '92e83c1a986d6838ab8ffd93d312e0b08112a2278c36eceea0b1217ea1414c0d'
     AND receipt.detail_payload->>'preservedPetStateSha256'=
       'b14fb2d2b389618178e1716589c51f8119daf544bf73a785e1001f821d20156b'
     AND receipt.detail_payload->>'otherPetsSha256'=
       'c9bcf38cb8651b4357b9915954307f2e3d8ad3aac382084efa7f6e4f4a826be3'
     AND receipt.detail_payload->>'itemsSha256'=
       '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
     AND receipt.detail_payload->>'petSkillsSha256'=
       '1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3'
     AND receipt.detail_payload->>'petStatsSha256'=
       '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
     AND receipt.detail_payload->>'ownerBonusesSha256'=
       '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
     AND receipt.detail_payload->>'sealedLinksSha256'=
       '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
     AND receipt.detail_payload->'sourceCommandAuditIds'=
       '[9263,9266,9267]'::jsonb
     AND receipt.detail_payload->'sourcePetOperationAuditIds'=
       '[711,728,729]'::jsonb),false) receipt_fields_valid
 FROM repair_receipt receipt
),
readiness AS (
 SELECT pins.*,maxima.*,identity.*,pet.*,evidence.*,hashes.*,
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
     authority_valid
 FROM content_pins pins CROSS JOIN calculated_maxima maxima
 CROSS JOIN identity_state identity CROSS JOIN pet_state pet
 CROSS JOIN source_evidence evidence CROSS JOIN preservation_hashes hashes
)
SELECT 'PET_UNSEAL_VITALS_ENERGY_STATUS|' || jsonb_build_object(
 'pinsValid',ready.pins_valid,'authorityValid',ready.authority_valid,
 'itemContentRevision',ready.item_revision,
 'gameplayContentRevision',ready.gameplay_revision,
 'petSkillContentRevision',ready.pet_skill_revision,
 'petOwnerMergeContentRevision',ready.owner_merge_revision,
 'calculatedMaximumHp',ready.maximum_hp,
 'calculatedMaximumMp',ready.maximum_mp,
 'derivedMaximumHp',ready.derived_max_hp,
 'derivedMaximumMp',ready.derived_max_mp,
 'currentHp',ready.current_hp,'currentMp',ready.current_mp,
 'vitalsRevision',ready.vitals_revision,
 'currentEnergy',ready.current_energy,
 'maximumEnergy',ready.maximum_energy,'petRevision',ready.pet_revision,
 'sourceReady',ready.authority_valid
    AND ready.current_hp=29350 AND ready.current_mp=1287
    AND ready.vitals_revision=9895 AND ready.current_energy=31
    AND ready.pet_revision=1413,
 'postReady',ready.authority_valid
    AND ready.current_hp=ready.maximum_hp
    AND ready.current_mp=ready.maximum_mp
    AND ready.vitals_revision=9896
    AND ready.current_energy=ready.maximum_energy
    AND ready.pet_revision=1414,
 'accountStateSha256',ready.account_hash,
 'preservedCharacterStateSha256',ready.character_hash,
 'preservedPetStateSha256',ready.pet_hash,
 'otherPetsSha256',ready.other_pets_hash,
 'itemsSha256',ready.items_hash,'petSkillsSha256',ready.skills_hash,
 'petStatsSha256',ready.pet_stats_hash,
 'ownerBonusesSha256',ready.owner_bonuses_hash,
 'sealedLinksSha256',ready.sealed_links_hash,
 'receiptCount',receipt.receipt_count,
 'receiptValid',receipt.receipt_count=1
    AND receipt.receipt_fields_valid
    AND ready.current_hp=ready.maximum_hp
    AND ready.current_mp=ready.maximum_mp
    AND ready.vitals_revision=9896
    AND ready.current_energy=ready.maximum_energy
    AND ready.pet_revision=1414,
 'receiptAuditId',receipt.receipt_audit_id,
 'immutableAuditTrigger',EXISTS (SELECT 1 FROM pg_trigger
    WHERE tgrelid='public.command_audit'::regclass
      AND tgname='trg_command_audit_immutable' AND tgenabled<>'D'))::text
FROM readiness ready CROSS JOIN receipt_state receipt;
COMMIT;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
