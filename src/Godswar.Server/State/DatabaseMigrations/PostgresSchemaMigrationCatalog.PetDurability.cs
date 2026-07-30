namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetDurabilityFoundation() => new(
            "20260731_034_pet_durability_foundation",
            "Add the bounded per-character pet durable command stream",
            """
            CREATE TABLE public.pet_durable_stream_versions (
                character_id integer PRIMARY KEY
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                current_version bigint NOT NULL DEFAULT 0,
                updated_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT ck_pet_durable_stream_version
                    CHECK (current_version >= 0)
            );

            CREATE INDEX ix_pet_durable_stream_updated
                ON public.pet_durable_stream_versions (
                    updated_at,
                    character_id
                );

            CREATE VIEW public.pet_durable_command_evidence AS
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
                  'pet_presence_transition'
              );
            """);
}
