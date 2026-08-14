namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetSoulContract() =>
        new(
            "20260813_088_pet_soul_contract",
            "Persist native Soul Contract stage and durable command evidence",
            """
            ALTER TABLE public.character_pets
                ADD COLUMN soul_contract_stage smallint;

            UPDATE public.character_pets
            SET soul_contract_stage =
                CASE WHEN has_soul_contract THEN 1 ELSE 0 END;

            -- Existing deferred pet-state guards fire for the backfill. Drain
            -- them before the next ALTER TABLE so populated databases do not
            -- retain pending trigger events across the schema change.
            SET CONSTRAINTS ALL IMMEDIATE;

            ALTER TABLE public.character_pets
                ALTER COLUMN soul_contract_stage SET DEFAULT 0,
                ALTER COLUMN soul_contract_stage SET NOT NULL,
                ADD CONSTRAINT ck_character_pets_soul_contract_stage
                    CHECK (soul_contract_stage BETWEEN 0 AND 6);

            CREATE OR REPLACE FUNCTION
                public.sync_character_pet_soul_contract()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sync$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    IF NEW.has_soul_contract AND
                       NEW.soul_contract_stage = 0 THEN
                        NEW.soul_contract_stage := 1;
                    ELSE
                        NEW.has_soul_contract :=
                            NEW.soul_contract_stage > 0;
                    END IF;
                ELSIF NEW.soul_contract_stage IS DISTINCT FROM
                      OLD.soul_contract_stage THEN
                    NEW.has_soul_contract :=
                        NEW.soul_contract_stage > 0;
                ELSIF NEW.has_soul_contract IS DISTINCT FROM
                      OLD.has_soul_contract THEN
                    NEW.soul_contract_stage :=
                        CASE WHEN NEW.has_soul_contract THEN 1 ELSE 0 END;
                END IF;
                RETURN NEW;
            END
            $sync$;

            CREATE TRIGGER trg_character_pets_soul_contract_sync
            BEFORE INSERT OR UPDATE OF
                soul_contract_stage, has_soul_contract
            ON public.character_pets
            FOR EACH ROW
            EXECUTE FUNCTION public.sync_character_pet_soul_contract();

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
                  'pet_bind',
                  'pet_soul_contract'
              );
            """);
}
