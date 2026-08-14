Set-StrictMode -Version Latest

function Get-EmptySealJadeStatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $sql = @"
BEGIN READ ONLY;
WITH
$(Get-EmptySealJadeContentCtesSql),
$(Get-EmptySealJadeStateCtesSql),
receipt AS (
 SELECT a.* FROM public.command_audit a
 WHERE a.principal_type='developer' AND a.principal_key='13'
   AND a.aggregate_type='character_inventory'
   AND a.aggregate_key='character:2'
   AND a.command_family='empty_seal_jade_grant'
   AND a.operation_id=decode('__OPERATION_HEX__','hex')
), receipt_state AS (
 SELECT count(*) receipt_count,
   COALESCE(bool_and(r.request_hash=decode('__REQUEST_HASH_HEX__','hex')
    AND r.outcome_code='applied' AND r.retention_policy='permanent'
    AND r.detail_payload->>'fixtureVersion'='1'
    AND r.detail_payload->>'accountId'='13'
    AND r.detail_payload->>'characterId'='2'
    AND r.detail_payload->>'itemId'='10108'
    AND r.detail_payload->>'quantity'='1'
    AND r.detail_payload->>'slot'='0'
    AND r.detail_payload->>'previousInventoryRevision'='729'
    AND r.detail_payload->>'currentInventoryRevision'='730'
    AND r.detail_payload->>'preservedItemsSha256'=
       '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762'
    AND r.detail_payload->>'petsSha256'=
       'f38ce9fe012923913b49966777a064d2ff6830daac4ef0dddc3ac58cb8f1fb94'
    AND r.detail_payload->>'petStatsSha256'=
       '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    AND r.detail_payload->>'petSkillsSha256'=
       '1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3'
    AND r.detail_payload->>'publishedItemRevision'=
       '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
    AND jsonb_typeof(r.detail_payload->'itemAuditIds')='array'
    AND jsonb_array_length(r.detail_payload->'itemAuditIds')=1),false)
      receipt_fields_valid,
   min(r.id) audit_id,
   min(r.detail_payload->>'grantedItemStateSha256') grant_hash
 FROM receipt r
), linked_audits AS (
 SELECT count(*) linked_count
 FROM receipt r
 CROSS JOIN LATERAL jsonb_array_elements_text(CASE
   WHEN jsonb_typeof(r.detail_payload->'itemAuditIds')='array'
   THEN r.detail_payload->'itemAuditIds' ELSE '[]'::jsonb END) linked(id)
 JOIN public.character_item_audit a ON a.id=linked.id::bigint
 WHERE a.source='localdev-empty-seal-jade-grant-v1'
   AND a.action='add' AND a.user_id=2 AND a.item_location=1
   AND a.slot_index=0 AND a.prop_id=10108 AND a.item_quality=1
   AND a.item_grade=1 AND a.item_exp=0 AND a.old_item IS NULL
), readiness AS (
 SELECT cs.*,ids.inventory_revision,inv.*,auth.*,
   cs.content_valid AND ids.exact_rows=1 content_identity_valid,
   (auth.pets_hash=
      'f38ce9fe012923913b49966777a064d2ff6830daac4ef0dddc3ac58cb8f1fb94'
    AND auth.stats_hash=
      '66b1afc73362a805a5d14f840b8488d1b5e95b1e3aad3982cff844b8b0e04a0c'
    AND auth.skills_hash=
      '1f350a198d095ba83cdcf9199ecb480609c1d4bb4dca66d50a70d07156b2a7b3'
    AND auth.growth_hash=
      '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    AND auth.savvy_hash=
      '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    AND auth.sealed_hash=
      '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    AND auth.pet_count=3
    AND auth.main_pet->>'petId'='1'
    AND auth.main_pet->>'speciesId'='31'
    AND (auth.main_pet->>'rank')::numeric=100
    AND auth.main_pet->>'revision'='1406'
    AND (auth.main_pet->>'bound')::boolean IS TRUE
    AND auth.main_pet->>'activityState'='owned'
    AND (auth.main_pet->>'isCarried')::boolean IS TRUE
    AND (auth.main_pet->>'isSummoned')::boolean IS TRUE
    AND (auth.main_pet->>'contributes')::boolean IS FALSE
    AND (auth.main_pet->>'soulContract')::boolean IS TRUE
    AND auth.main_pet->>'soulStage'='6') authority_valid
 FROM content_state cs CROSS JOIN identity_state ids
 CROSS JOIN inventory_state inv CROSS JOIN authority_hashes auth
), classified AS (
 SELECT r.*,
   (r.content_identity_valid AND r.authority_valid
    AND r.inventory_revision=729 AND r.bag_rows=1
    AND r.bag_units=94 AND r.total_item_rows=20
    AND r.source_rows=1 AND r.granted_rows=0
    AND r.target_anywhere=0 AND r.first_empty_slot=0
    AND r.preserved_items_hash=
      '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762')
      source_ready,
   (r.content_identity_valid AND r.authority_valid
    AND r.inventory_revision=730 AND r.bag_rows=2
    AND r.bag_units=95 AND r.total_item_rows=21
    AND r.source_rows=1 AND r.granted_rows=1
    AND r.target_anywhere=1 AND r.first_empty_slot=1
    AND r.preserved_items_hash=
      '959197c07aa49f251f591a155877f0c1ee4947d40ec3c20182938e72d1441762')
      post_ready
 FROM readiness r
)
SELECT 'EMPTY_SEAL_JADE_STATUS|' || jsonb_build_object(
 'itemRevision',r.revision,'contentValid',r.content_valid,
 'identityReady',r.content_identity_valid,'authorityValid',r.authority_valid,
 'sourceReady',r.source_ready,'postReady',r.post_ready,
 'inventoryRevision',r.inventory_revision,'bagRows',r.bag_rows,
 'bagUnits',r.bag_units,'totalItemRows',r.total_item_rows,
 'sourceRows',r.source_rows,'grantedRows',r.granted_rows,
 'targetAnywhere',r.target_anywhere,'firstEmptySlot',r.first_empty_slot,
 'preservedItemsSha256',r.preserved_items_hash,
 'petsSha256',r.pets_hash,'petStatsSha256',r.stats_hash,
 'petSkillsSha256',r.skills_hash,'growthPreviewSha256',r.growth_hash,
 'savvyPreviewSha256',r.savvy_hash,'sealedLinksSha256',r.sealed_hash,
 'petCount',r.pet_count,'mainPet',r.main_pet,
 'receiptCount',rs.receipt_count,
 'receiptValid',rs.receipt_count=1 AND rs.receipt_fields_valid
    AND la.linked_count=1,
 'receiptAuditId',rs.audit_id,'grantedItemStateSha256',rs.grant_hash,
 'linkedItemAuditCount',la.linked_count)::text
FROM classified r CROSS JOIN receipt_state rs CROSS JOIN linked_audits la;
COMMIT;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex)
}
