namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePackedSealOwnershipHardening() =>
        new(
            "20260814_092_packed_seal_ownership_hardening",
            "Preserve sealed-pet binding across packed-item ownership",
            """
            ALTER TABLE public.sealed_pet_items
                ADD COLUMN pet_bound_snapshot boolean;

            UPDATE public.sealed_pet_items link
            SET pet_bound_snapshot = pet.bound
            FROM public.character_pets pet
            WHERE pet.id = link.pet_id;

            ALTER TABLE public.sealed_pet_items
                ALTER COLUMN pet_bound_snapshot SET NOT NULL;

            CREATE OR REPLACE FUNCTION
                public.reject_sealed_pet_bound_snapshot_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $reject_snapshot_change$
            BEGIN
                IF NEW.pet_bound_snapshot IS DISTINCT FROM
                   OLD.pet_bound_snapshot THEN
                    RAISE EXCEPTION
                        'Cannot change packed-item binding snapshot %',
                        OLD.id
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END
            $reject_snapshot_change$;

            CREATE TRIGGER trg_sealed_pet_bound_snapshot_immutable
            BEFORE UPDATE OF pet_bound_snapshot
            ON public.sealed_pet_items
            FOR EACH ROW EXECUTE FUNCTION
                public.reject_sealed_pet_bound_snapshot_change();

            CREATE OR REPLACE FUNCTION
                public.sync_sealed_pet_item_owner()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sync_owner$
            DECLARE
                link_row public.sealed_pet_items%ROWTYPE;
            BEGIN
                IF NEW.user_id IS NOT DISTINCT FROM OLD.user_id THEN
                    RETURN NEW;
                END IF;

                SELECT link.* INTO link_row
                FROM public.sealed_pet_items link
                WHERE link.item_instance_id = OLD.id
                FOR UPDATE;
                IF NOT FOUND THEN
                    RETURN NEW;
                END IF;

                IF link_row.pet_bound_snapshot
                   OR OLD.bound <> 0
                   OR NEW.bound <> 0 THEN
                    RAISE EXCEPTION
                        'Cannot transfer bound packed item %',
                        OLD.id
                        USING ERRCODE = '23514';
                END IF;

                UPDATE public.character_pets
                SET user_id = NEW.user_id,
                    revision = revision + 1,
                    updated_at = transaction_timestamp()
                WHERE id = link_row.pet_id
                  AND user_id = OLD.user_id
                  AND activity_state = 'sealed'
                  AND NOT bound;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Cannot transfer invalid sealed pet %',
                        link_row.pet_id
                        USING ERRCODE = '23514';
                END IF;

                UPDATE public.sealed_pet_items
                SET owner_character_id = NEW.user_id
                WHERE item_instance_id = OLD.id
                  AND owner_character_id = OLD.user_id
                  AND NOT pet_bound_snapshot;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Cannot transfer invalid packed item %',
                        OLD.id
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END
            $sync_owner$;

            CREATE OR REPLACE FUNCTION
                public.validate_active_sealed_pet_link(
                    target_item_id bigint,
                    target_pet_id bigint)
            RETURNS void
            LANGUAGE plpgsql
            AS $validate$
            DECLARE
                link_row public.sealed_pet_items%ROWTYPE;
                item_owner integer;
                item_template integer;
                item_bound smallint;
                pet_owner integer;
                pet_bound boolean;
                pet_state varchar(16);
                pet_carried boolean;
                pet_summoned boolean;
                pet_contributes boolean;
            BEGIN
                SELECT * INTO link_row
                FROM public.sealed_pet_items link
                WHERE (target_item_id IS NOT NULL
                       AND link.item_instance_id = target_item_id)
                   OR (target_pet_id IS NOT NULL
                       AND link.pet_id = target_pet_id);
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT item.user_id, item.prop_id, item.bound
                INTO item_owner, item_template, item_bound
                FROM public.character_items item
                WHERE item.id = link_row.item_instance_id;

                SELECT pet.user_id, pet.bound, pet.activity_state,
                       pet.is_carried, pet.is_summoned,
                       pet.contributes_to_character
                INTO pet_owner, pet_bound, pet_state, pet_carried,
                     pet_summoned, pet_contributes
                FROM public.character_pets pet
                WHERE pet.id = link_row.pet_id;

                IF item_owner IS NULL OR pet_owner IS NULL
                   OR item_template <> 10109
                   OR item_bound NOT IN (0, 1)
                   OR (item_bound = 1) IS DISTINCT FROM
                      link_row.pet_bound_snapshot
                   OR pet_bound IS DISTINCT FROM
                      link_row.pet_bound_snapshot
                   OR item_owner <> link_row.owner_character_id
                   OR pet_owner <> link_row.owner_character_id
                   OR pet_state <> 'sealed'
                   OR pet_carried OR pet_summoned OR pet_contributes THEN
                    RAISE EXCEPTION
                        'Invalid active sealed-pet link item %, pet %',
                        link_row.item_instance_id,
                        link_row.pet_id
                        USING ERRCODE = '23514';
                END IF;
            END
            $validate$;
            """);
}
