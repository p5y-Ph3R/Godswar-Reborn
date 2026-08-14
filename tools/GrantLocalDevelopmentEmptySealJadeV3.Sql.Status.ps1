Set-StrictMode -Version Latest

function Get-EmptySealJadeV3StatusSql(
    [string]$OperationHex,
    [string]$RequestHashHex
) {
    $preservedHash = Get-EmptySealJadeV3PreservedHash
    $sql = @"
BEGIN READ ONLY;
WITH
$(Get-EmptySealJadeV3StateCtesSql),
receipt AS (
 SELECT a.* FROM public.command_audit a
 WHERE a.principal_type='developer' AND a.principal_key='13'
   AND a.aggregate_type='character_inventory'
   AND a.aggregate_key='character:2'
   AND a.command_family='empty_seal_jade_grant_repeat'
   AND a.operation_id=decode('__OPERATION_HEX__','hex')
), receipt_state AS (
 SELECT count(*) receipt_count,min(r.id) audit_id,
   COALESCE(bool_and(r.request_hash=decode('__REQUEST_HASH_HEX__','hex')
    AND r.outcome_code='applied' AND r.retention_policy='permanent'
    AND r.detail_payload->>'fixtureVersion'='3'
    AND r.detail_payload->>'accountId'='13'
    AND r.detail_payload->>'characterId'='2'
    AND r.detail_payload->>'itemId'='10108'
    AND r.detail_payload->>'quantity'='1'
    AND r.detail_payload->>'slot'='0'
    AND r.detail_payload->>'previousInventoryRevision'='735'
    AND r.detail_payload->>'currentInventoryRevision'='736'
    AND r.detail_payload->>'preservedStateSha256'='__PRESERVED_HASH__'
    AND r.detail_payload->>'publishedItemRevision'=
      '1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF'
    AND jsonb_typeof(r.detail_payload->'itemAuditIds')='array'
    AND jsonb_array_length(r.detail_payload->'itemAuditIds')=1),false)
      fields_valid
 FROM receipt r
), linked_audits AS (
 SELECT count(*) linked_count
 FROM receipt r
 CROSS JOIN LATERAL jsonb_array_elements_text(CASE
   WHEN jsonb_typeof(r.detail_payload->'itemAuditIds')='array'
   THEN r.detail_payload->'itemAuditIds' ELSE '[]'::jsonb END) linked(id)
 JOIN public.character_item_audit a ON a.id=linked.id::bigint
 WHERE a.source='localdev-empty-seal-jade-grant-v3'
   AND a.action='add' AND a.user_id=2 AND a.item_location=1
   AND a.slot_index=0 AND a.prop_id=10108 AND a.item_quality=1
   AND a.item_grade=1 AND a.item_exp=0 AND a.old_item IS NULL
), immutable_audit AS (
 SELECT count(*)=1 immutable
 FROM pg_trigger WHERE tgrelid='public.command_audit'::regclass
   AND tgname='trg_command_audit_immutable' AND tgenabled<>'D'
), classified AS (
 SELECT ids.*,inv.*,ps.preserved_hash,mp.state main_pet,
   cs.revision item_revision,cs.content_valid,ia.immutable,
   (cs.content_valid AND ia.immutable AND ids.exact_rows=1
    AND ids.inventory_revision=735 AND ids.current_hp=134341
    AND ids.current_mp=6047 AND ids.vitals_revision=9896
    AND inv.bag_rows=1 AND inv.bag_units=94 AND inv.total_item_rows=20
    AND inv.source_rows=1 AND inv.granted_rows=0
    AND inv.target_anywhere=0 AND inv.first_empty_slot=0
    AND ps.preserved_hash='__PRESERVED_HASH__'
    AND mp.state->>'id'='1' AND mp.state->>'revision'='1414'
    AND mp.state->>'energy'='100' AND mp.state->>'maximumEnergy'='100'
    AND mp.state->>'level'='120'
    AND (mp.state->>'rank')::numeric=100
    AND mp.state->>'experience'='1254650135'
    AND (mp.state->>'bound')::boolean IS TRUE
    AND mp.state->>'activity'='owned'
    AND (mp.state->>'carried')::boolean IS TRUE
    AND (mp.state->>'summoned')::boolean IS TRUE) source_ready,
   (cs.content_valid AND ia.immutable AND ids.exact_rows=1
    AND ids.inventory_revision=736 AND ids.current_hp=134341
    AND ids.current_mp=6047 AND ids.vitals_revision=9896
    AND inv.bag_rows=2 AND inv.bag_units=95 AND inv.total_item_rows=21
    AND inv.source_rows=1 AND inv.granted_rows=1
    AND inv.target_anywhere=1 AND inv.first_empty_slot=1
    AND ps.preserved_hash='__PRESERVED_HASH__'
    AND mp.state->>'revision'='1414' AND mp.state->>'energy'='100'
    AND mp.state->>'maximumEnergy'='100') post_ready
 FROM identity_state ids CROSS JOIN inventory_state inv
 CROSS JOIN preserved_state ps CROSS JOIN main_pet mp
 CROSS JOIN content_state cs CROSS JOIN immutable_audit ia
)
SELECT 'EMPTY_SEAL_JADE_V3_STATUS|' || jsonb_build_object(
 'identityReady',c.exact_rows=1,'contentValid',c.content_valid,
 'immutableAudit',c.immutable,'sourceReady',c.source_ready,
 'postReady',c.post_ready,'inventoryRevision',c.inventory_revision,
 'currentHp',c.current_hp,'currentMp',c.current_mp,
 'vitalsRevision',c.vitals_revision,'bagRows',c.bag_rows,
 'bagUnits',c.bag_units,'totalItemRows',c.total_item_rows,
 'sourceRows',c.source_rows,'grantedRows',c.granted_rows,
 'targetAnywhere',c.target_anywhere,'firstEmptySlot',c.first_empty_slot,
 'preservedStateSha256',c.preserved_hash,'mainPet',c.main_pet,
 'itemRevision',c.item_revision,'receiptCount',rs.receipt_count,
 'receiptValid',rs.receipt_count=1 AND rs.fields_valid
    AND la.linked_count=1,
 'receiptAuditId',rs.audit_id,'linkedItemAuditCount',la.linked_count)::text
FROM classified c CROSS JOIN receipt_state rs CROSS JOIN linked_audits la;
COMMIT;
"@
    $sql.Replace('__OPERATION_HEX__', $OperationHex).
        Replace('__REQUEST_HASH_HEX__', $RequestHashHex).
        Replace('__PRESERVED_HASH__', $preservedHash)
}
