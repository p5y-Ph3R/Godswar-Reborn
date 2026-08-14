namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetDurableEvidenceV2() =>
        new(
            "20260811_075_pet_durable_evidence_v2",
            "Expose every durable pet command family, including innate owner Merge",
            """
            CREATE OR REPLACE VIEW public.pet_durable_command_evidence AS
            SELECT
                inbox.id AS inbox_id,
                inbox.principal_key AS account_id,
                inbox.aggregate_key,
                inbox.command_family,
                encode(inbox.operation_id, 'hex') AS operation_id,
                inbox.result_code,
                inbox.duplicate_count,
                inbox.request_conflict_count,
                inbox.completed_at,
                audit.id AS audit_id,
                event.event_id,
                event.aggregate_version,
                event.delivered_at,
                event.poisoned_at
            FROM public.command_inbox inbox
            INNER JOIN public.command_audit audit
                ON audit.id = inbox.audit_id
            LEFT JOIN public.outbox_events event
                ON event.command_inbox_id = inbox.id
               AND event.consumer_key = 'pet_durable_v1'
            WHERE inbox.aggregate_type = 'character_pet_value'
              AND inbox.command_family IN (
                  'bag_item_activation',
                  'pet_level_upgrade',
                  'pet_presence_transition',
                  'pet_skill_unlearn',
                  'pet_growth_reset',
                  'pet_owner_merge_toggle'
              );
            """);
}
