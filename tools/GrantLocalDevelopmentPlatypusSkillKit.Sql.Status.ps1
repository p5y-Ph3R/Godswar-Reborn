Set-StrictMode -Version Latest

function Get-PlatypusSkillKitStatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN READ ONLY;
WITH
$(Get-PlatypusSkillKitExpectedCtesSql),
$(Get-PlatypusSkillKitContentCtesSql),
$(Get-PlatypusSkillKitStateCtesSql),
receipt AS (
 SELECT a.* FROM public.command_audit a
 WHERE a.principal_type='developer' AND a.principal_key='13'
   AND a.aggregate_type='character_inventory'
   AND a.aggregate_key='character:2'
   AND a.command_family='platypus_skill_kit_grant'
   AND a.operation_id=decode('__OPERATION_HEX__','hex')
), receipt_state AS (
 SELECT count(*) receipt_count,
   COALESCE(bool_and(r.request_hash=decode('__REQUEST_HASH_HEX__','hex')
    AND r.outcome_code='applied' AND r.retention_policy='permanent'
    AND r.detail_payload->>'fixtureVersion'='1'
    AND r.detail_payload->>'accountId'='13'
    AND r.detail_payload->>'characterId'='2'
    AND r.detail_payload->'itemIds'=
       '[11080,10530,10531,10532,10533,10534,10535]'::jsonb
    AND r.detail_payload->'slots'='[25,26,27,28,29,30,31]'::jsonb
    AND r.detail_payload->>'previousInventoryRevision'='721'
    AND r.detail_payload->>'currentInventoryRevision'='722'
    AND r.detail_payload->>'preservedItemsSha256'=
       '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    AND r.detail_payload->>'petsSha256'=
       '8497f0dc0e742fed1d4c1b43e06b17b80c3349ddedd0890cbf697b400cd4d195'
    AND r.detail_payload->>'petStatsSha256'=
       '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    AND r.detail_payload->>'petSkillsSha256'=
       '5757df22a3d5f93b06705be8436ca1a80253476ba66728c6cd38ad397abced61'
    AND r.detail_payload->>'latestPresenceInboxId'='9031'
    AND r.detail_payload->>'latestPresenceAuditId'='9050'
    AND jsonb_typeof(r.detail_payload->'itemAuditIds')='array'
    AND jsonb_array_length(r.detail_payload->'itemAuditIds')=7),false)
      receipt_fields_valid,
   min(r.id) audit_id,
   min(r.detail_payload->>'grantedItemsStateSha256') grant_hash
 FROM receipt r
), linked_audits AS (
 SELECT count(*) linked_count
 FROM receipt r
 CROSS JOIN LATERAL jsonb_array_elements_text(CASE
   WHEN jsonb_typeof(r.detail_payload->'itemAuditIds')='array'
   THEN r.detail_payload->'itemAuditIds' ELSE '[]'::jsonb END) linked(id)
 JOIN public.character_item_audit a ON a.id=linked.id::bigint
 JOIN expected_kit e ON e.slot_index=a.slot_index AND e.prop_id=a.prop_id
 WHERE a.source='localdev-platypus-skill-kit-grant-v1'
   AND a.action='add' AND a.user_id=2 AND a.item_location=1
   AND a.item_quality=1 AND a.item_grade=1 AND a.item_exp=0
   AND a.old_item IS NULL
), readiness AS (
 SELECT cs.*,ids.inventory_revision,inv.*,auth.*,
   cs.item_content_valid AND cs.activation_content_valid content_valid,
   ids.exact_rows=1 identity_ready,
   (ids.inventory_revision=721 AND inv.bag_rows=1 AND inv.bag_units=94
    AND inv.total_item_rows=20 AND inv.source_rows=1 AND inv.kit_rows=0
    AND inv.target_anywhere=0 AND inv.preserved_items_hash=
      '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    AND auth.pets_hash=
      '8497f0dc0e742fed1d4c1b43e06b17b80c3349ddedd0890cbf697b400cd4d195'
    AND auth.stats_hash=
      '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    AND auth.skills_hash=
      '5757df22a3d5f93b06705be8436ca1a80253476ba66728c6cd38ad397abced61'
    AND auth.growth_previews=0 AND auth.savvy_previews=0
    AND auth.sealed_links=0) source_ready,
   (ids.inventory_revision=722 AND inv.bag_rows=8 AND inv.bag_units=101
    AND inv.total_item_rows=27 AND inv.source_rows=1 AND inv.kit_rows=7
    AND inv.target_anywhere=7 AND inv.preserved_items_hash=
      '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    AND auth.pets_hash=
      '8497f0dc0e742fed1d4c1b43e06b17b80c3349ddedd0890cbf697b400cd4d195'
    AND auth.stats_hash=
      '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    AND auth.skills_hash=
      '5757df22a3d5f93b06705be8436ca1a80253476ba66728c6cd38ad397abced61'
    AND auth.growth_previews=0 AND auth.savvy_previews=0
    AND auth.sealed_links=0) post_ready
 FROM content_state cs CROSS JOIN identity_state ids
 CROSS JOIN inventory_state inv CROSS JOIN authority_hashes auth
)
SELECT 'PLATYPUS_SKILL_KIT_STATUS|' || jsonb_build_object(
 'itemRevision',r.item_revision,'learnedSkillRevision',r.learned_revision,
 'petRevision',r.pet_revision,'itemContentValid',r.item_content_valid,
 'activationContentValid',r.activation_content_valid,
 'contentValid',r.content_valid,'identityReady',r.identity_ready,
 'sourceReady',r.source_ready,'postReady',r.post_ready,
 'inventoryRevision',r.inventory_revision,'bagRows',r.bag_rows,
 'bagUnits',r.bag_units,'totalItemRows',r.total_item_rows,
 'sourceRows',r.source_rows,'kitRows',r.kit_rows,
 'targetAnywhere',r.target_anywhere,
 'preservedItemsSha256',r.preserved_items_hash,
 'petsSha256',r.pets_hash,'petStatsSha256',r.stats_hash,
 'petSkillsSha256',r.skills_hash,'mainPet',r.main_pet,
 'growthPreviewCount',r.growth_previews,
 'basicSavvyPreviewCount',r.savvy_previews,
 'sealedPetLinkCount',r.sealed_links,
 'receiptCount',rs.receipt_count,
 'receiptValid',rs.receipt_count=1 AND rs.receipt_fields_valid
    AND la.linked_count=7,
 'receiptAuditId',rs.audit_id,'grantedItemsStateSha256',rs.grant_hash,
 'linkedItemAuditCount',la.linked_count)::text
FROM readiness r CROSS JOIN receipt_state rs CROSS JOIN linked_audits la;
COMMIT;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
