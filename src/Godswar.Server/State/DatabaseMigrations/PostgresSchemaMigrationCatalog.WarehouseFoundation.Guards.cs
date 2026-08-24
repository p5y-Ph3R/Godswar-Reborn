namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string WarehouseGuardSql =
        """
        CREATE FUNCTION public.guard_character_warehouse_item_slot()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_character_warehouse_item_slot$
        DECLARE
            owner_capacity smallint;
        BEGIN
            IF NEW.item_location <> 3 THEN
                RETURN NEW;
            END IF;
            SELECT character.warehouse_capacity
              INTO owner_capacity
            FROM public.character_base character
            WHERE character.id = NEW.user_id
            FOR UPDATE;
            IF owner_capacity IS NULL
               OR NEW.slot_index < 0
               OR NEW.slot_index >= owner_capacity THEN
                RAISE EXCEPTION
                    'Warehouse slot % exceeds character % capacity %.',
                    NEW.slot_index, NEW.user_id, owner_capacity
                    USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                SELECT 1
                FROM public.sealed_pet_items link
                WHERE link.item_instance_id = NEW.id
            ) THEN
                RAISE EXCEPTION
                    'Packed sealed-pet items cannot enter warehouse storage.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $guard_character_warehouse_item_slot$;

        CREATE FUNCTION public.guard_warehouse_sealed_pet_link()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_warehouse_sealed_pet_link$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.character_items item
                WHERE item.id = NEW.item_instance_id
                  AND item.item_location = 3
            ) THEN
                RAISE EXCEPTION
                    'Warehouse items cannot acquire sealed-pet links.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $guard_warehouse_sealed_pet_link$;

        CREATE TRIGGER trg_character_warehouse_item_slot_guard
            BEFORE INSERT OR UPDATE OF user_id, item_location, slot_index
            ON public.character_items
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_character_warehouse_item_slot();
        CREATE TRIGGER trg_warehouse_sealed_pet_link_guard
            BEFORE INSERT OR UPDATE OF item_instance_id
            ON public.sealed_pet_items
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_warehouse_sealed_pet_link();

        CREATE FUNCTION public.guard_character_warehouse_capacity()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_character_warehouse_capacity$
        BEGIN
            IF NEW.warehouse_capacity <> OLD.warehouse_capacity THEN
                IF NEW.warehouse_capacity < OLD.warehouse_capacity THEN
                    RAISE EXCEPTION
                        'Warehouse capacity decreases are prohibited.'
                        USING ERRCODE = '23514';
                END IF;
                IF NEW.warehouse_revision <> OLD.warehouse_revision + 1 THEN
                    RAISE EXCEPTION
                        'Warehouse capacity changes must advance its revision exactly once.'
                        USING ERRCODE = '23514';
                END IF;
            ELSIF NEW.warehouse_revision <> OLD.warehouse_revision THEN
                RAISE EXCEPTION
                    'Warehouse revisions may advance only with capacity.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $guard_character_warehouse_capacity$;

        CREATE FUNCTION public.require_warehouse_capacity_evidence()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $require_warehouse_capacity_evidence$
        BEGIN
            IF NEW.warehouse_capacity <> OLD.warehouse_capacity
               AND NOT EXISTS (
                   SELECT 1
                   FROM public.warehouse_expansion_settlements settlement
                   WHERE settlement.account_id = NEW.account_id
                     AND settlement.character_id = NEW.id
                     AND settlement.previous_capacity =
                        OLD.warehouse_capacity
                     AND settlement.current_capacity =
                        NEW.warehouse_capacity
                     AND settlement.warehouse_revision =
                        NEW.warehouse_revision
               ) THEN
                RAISE EXCEPTION
                    'Warehouse capacity changes require durable expansion evidence.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NULL;
        END;
        $require_warehouse_capacity_evidence$;

        CREATE TRIGGER trg_character_warehouse_capacity_guard
            BEFORE UPDATE OF warehouse_capacity, warehouse_revision
            ON public.character_base
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_character_warehouse_capacity();
        CREATE CONSTRAINT TRIGGER
            trg_character_warehouse_capacity_evidence
            AFTER UPDATE OF warehouse_capacity, warehouse_revision
            ON public.character_base
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION
                public.require_warehouse_capacity_evidence();

        CREATE FUNCTION public.warehouse_expansion_policy_canonical(
            target_revision bigint)
        RETURNS text
        LANGUAGE sql
        STABLE
        AS $warehouse_expansion_policy_canonical$
            SELECT 'warehouse-expansion-policy-v1' || E'\n' || COALESCE(
                string_agg(
                    format('level:%s,%s,%s',
                        capacity, key_cost, key_item_id) || E'\n',
                    '' ORDER BY capacity),
                '')
            FROM public.warehouse_expansion_policy_levels
            WHERE revision = target_revision;
        $warehouse_expansion_policy_canonical$;

        CREATE FUNCTION public.guard_warehouse_policy_revision()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_warehouse_policy_revision$
        DECLARE
            actual_count integer;
            actual_sha256 varchar(64);
            actual_capacities smallint[];
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Warehouse policy revisions are append-only.'
                    USING ERRCODE = '55000';
            END IF;
            IF OLD.sealed_at IS NULL AND NEW.sealed_at IS NOT NULL
               AND (NEW.revision, NEW.sha256, NEW.level_count,
                    NEW.source, NEW.created_by, NEW.created_at)
                   IS NOT DISTINCT FROM
                   (OLD.revision, OLD.sha256, OLD.level_count,
                    OLD.source, OLD.created_by, OLD.created_at) THEN
                SELECT count(*)::integer,
                       array_agg(capacity ORDER BY capacity)
                  INTO actual_count, actual_capacities
                FROM public.warehouse_expansion_policy_levels
                WHERE revision = NEW.revision;
                SELECT upper(encode(sha256(convert_to(
                           public.warehouse_expansion_policy_canonical(
                               NEW.revision),
                           'UTF8')), 'hex'))
                  INTO actual_sha256;
                IF actual_count <> NEW.level_count
                   OR actual_capacities <>
                        ARRAY[40,80,120,160]::smallint[]
                   OR actual_sha256 <> NEW.sha256 THEN
                    RAISE EXCEPTION
                        'Warehouse policy revision % is incomplete or has an invalid hash.',
                        NEW.revision
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;
            RAISE EXCEPTION
                'Warehouse policy revisions are immutable after insert.'
                USING ERRCODE = '55000';
        END;
        $guard_warehouse_policy_revision$;

        CREATE FUNCTION public.guard_warehouse_policy_level()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_warehouse_policy_level$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                PERFORM 1
                FROM public.warehouse_expansion_policy_revisions revision
                WHERE revision.revision = NEW.revision
                  AND revision.sealed_at IS NULL
                FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Warehouse policy levels require an unsealed revision.'
                        USING ERRCODE = '55000';
                END IF;
            ELSE
                RAISE EXCEPTION 'Warehouse policy levels are append-only.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        $guard_warehouse_policy_level$;

        CREATE FUNCTION public.guard_warehouse_policy_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_warehouse_policy_publication$
        DECLARE
            release_sha256 varchar(64);
            release_sealed_at timestamptz;
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.publication_version <> 1 THEN
                    RAISE EXCEPTION
                        'Warehouse policy publication must start at version one.'
                        USING ERRCODE = '23514';
                END IF;
            ELSIF NEW.publication_version <> OLD.publication_version + 1
               OR NEW.revision <> OLD.revision + 1
               OR NEW.policy_sha256 = OLD.policy_sha256
               OR NEW.updated_at < OLD.updated_at THEN
                RAISE EXCEPTION
                    'Warehouse policy publication requires the exact CAS successor.'
                    USING ERRCODE = '23514';
            END IF;

            UPDATE public.warehouse_expansion_policy_revisions
            SET sealed_at = transaction_timestamp()
            WHERE revision = NEW.revision
              AND sha256 = NEW.policy_sha256
              AND sealed_at IS NULL;
            SELECT sha256, sealed_at
              INTO release_sha256, release_sealed_at
            FROM public.warehouse_expansion_policy_revisions
            WHERE revision = NEW.revision;
            IF release_sealed_at IS NULL
               OR release_sha256 <> NEW.policy_sha256 THEN
                RAISE EXCEPTION
                    'Warehouse publication requires one sealed exact revision.'
                    USING ERRCODE = '23514';
            END IF;
            NEW.updated_at := transaction_timestamp();
            RETURN NEW;
        END;
        $guard_warehouse_policy_publication$;

        CREATE FUNCTION public.audit_warehouse_policy_publication()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $audit_warehouse_policy_publication$
        BEGIN
            INSERT INTO public.warehouse_expansion_policy_audit (
                publication_version, previous_revision, revision,
                previous_sha256, policy_sha256, changed_at, changed_by)
            VALUES (
                NEW.publication_version,
                CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.revision END,
                NEW.revision,
                CASE WHEN TG_OP = 'INSERT'
                    THEN NULL ELSE OLD.policy_sha256 END,
                NEW.policy_sha256, NEW.updated_at, NEW.updated_by);
            RETURN NEW;
        END;
        $audit_warehouse_policy_publication$;

        CREATE FUNCTION public.guard_warehouse_policy_audit()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_warehouse_policy_audit$
        BEGIN
            IF TG_OP = 'INSERT' AND EXISTS (
                SELECT 1
                FROM public.warehouse_expansion_policy_publication publication
                WHERE publication.family = 'warehouse-expansion'
                  AND publication.publication_version =
                      NEW.publication_version
                  AND publication.revision = NEW.revision
                  AND publication.policy_sha256 = NEW.policy_sha256
                  AND publication.updated_at = NEW.changed_at
                  AND publication.updated_by = NEW.changed_by
            ) AND (
                NEW.previous_revision IS NULL
                OR EXISTS (
                    SELECT 1
                    FROM public.warehouse_expansion_policy_audit prior
                    WHERE prior.publication_version =
                        NEW.publication_version - 1
                      AND prior.revision = NEW.previous_revision
                      AND prior.policy_sha256 = NEW.previous_sha256
                )
            ) THEN
                RETURN NEW;
            END IF;
            RAISE EXCEPTION
                'Warehouse policy audit is trigger-owned and append-only.'
                USING ERRCODE = '55000';
        END;
        $guard_warehouse_policy_audit$;

        CREATE FUNCTION public.reject_warehouse_immutable_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $reject_warehouse_immutable_mutation$
        BEGIN
            RAISE EXCEPTION '% is append-only', TG_TABLE_NAME
                USING ERRCODE = '55000';
        END;
        $reject_warehouse_immutable_mutation$;

        CREATE FUNCTION public.guard_warehouse_expansion_settlement()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard_warehouse_expansion_settlement$
        DECLARE
            expected_cost smallint;
            expected_item integer;
        BEGIN
            SELECT level.key_cost, level.key_item_id
              INTO expected_cost, expected_item
            FROM public.warehouse_expansion_policy_levels level
            WHERE level.revision = NEW.policy_revision
              AND level.capacity = NEW.current_capacity;
            IF expected_cost IS NULL
               OR NEW.keys_consumed <> expected_cost
               OR NEW.key_item_id <> expected_item THEN
                RAISE EXCEPTION
                    'Warehouse expansion settlement does not match its policy.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $guard_warehouse_expansion_settlement$;

        CREATE TRIGGER trg_warehouse_policy_revision_guard
            BEFORE UPDATE OR DELETE
            ON public.warehouse_expansion_policy_revisions
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_warehouse_policy_revision();
        CREATE TRIGGER trg_warehouse_policy_level_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON public.warehouse_expansion_policy_levels
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_warehouse_policy_level();
        CREATE TRIGGER trg_warehouse_policy_publication_guard
            BEFORE INSERT OR UPDATE
            ON public.warehouse_expansion_policy_publication
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_warehouse_policy_publication();
        CREATE TRIGGER trg_warehouse_policy_publication_audit
            AFTER INSERT OR UPDATE
            ON public.warehouse_expansion_policy_publication
            FOR EACH ROW EXECUTE FUNCTION
                public.audit_warehouse_policy_publication();
        CREATE TRIGGER trg_warehouse_policy_revision_no_truncate
            BEFORE TRUNCATE
            ON public.warehouse_expansion_policy_revisions
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_warehouse_immutable_mutation();
        CREATE TRIGGER trg_warehouse_policy_level_no_truncate
            BEFORE TRUNCATE
            ON public.warehouse_expansion_policy_levels
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_warehouse_immutable_mutation();
        CREATE TRIGGER trg_warehouse_policy_audit_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON public.warehouse_expansion_policy_audit
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_warehouse_policy_audit();
        CREATE TRIGGER trg_warehouse_policy_audit_no_truncate
            BEFORE TRUNCATE
            ON public.warehouse_expansion_policy_audit
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_warehouse_immutable_mutation();
        CREATE TRIGGER trg_warehouse_policy_publication_no_delete
            BEFORE DELETE OR TRUNCATE
            ON public.warehouse_expansion_policy_publication
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_warehouse_immutable_mutation();
        CREATE TRIGGER trg_warehouse_expansion_settlement_guard
            BEFORE INSERT
            ON public.warehouse_expansion_settlements
            FOR EACH ROW EXECUTE FUNCTION
                public.guard_warehouse_expansion_settlement();
        CREATE TRIGGER trg_warehouse_expansion_settlements_immutable
            BEFORE UPDATE OR DELETE OR TRUNCATE
            ON public.warehouse_expansion_settlements
            FOR EACH STATEMENT EXECUTE FUNCTION
                public.reject_warehouse_immutable_mutation();
        """;
}
