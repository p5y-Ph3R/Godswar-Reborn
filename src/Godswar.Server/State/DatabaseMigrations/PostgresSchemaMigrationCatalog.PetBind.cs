namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetBind() =>
        new(
            "20260812_087_pet_bind",
            "Authorize durable summoned-pet binding",
            """
            ALTER TABLE public.pet_operation_audit
                ADD CONSTRAINT ck_pet_operation_audit_operation_v7
                CHECK (
                    operation IN (
                        'owner_merge',
                        'pet_merge',
                        'rebirth',
                        'soul_contract',
                        'take',
                        'summon',
                        'dismiss',
                        'reveal_growth',
                        'reset_basic_savvy',
                        'change_appearance',
                        'bind',
                        'seal',
                        'unseal',
                        'hatch',
                        'level_up'
                    )
                ) NOT VALID;

            ALTER TABLE public.pet_operation_audit
                VALIDATE CONSTRAINT
                    ck_pet_operation_audit_operation_v7;

            ALTER TABLE public.pet_operation_audit
                DROP CONSTRAINT pet_operation_audit_operation_check;

            ALTER TABLE public.pet_operation_audit
                RENAME CONSTRAINT
                    ck_pet_operation_audit_operation_v7
                TO pet_operation_audit_operation_check;

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
                  'pet_basic_savvy_reset',
                  'pet_owner_merge_toggle',
                  'pet_to_pet_merge',
                  'pet_rebirth',
                  'pet_appearance_change',
                  'pet_bind'
              );
            """);
}
