namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateWarehouseNineBoxCapacity() =>
        new(
            "20260824_110_warehouse_nine_box_capacity",
            "Expand the database-owned warehouse policy to nine boxes",
            """
            ALTER TABLE public.character_base
                DROP CONSTRAINT ck_character_base_warehouse_capacity,
                ADD CONSTRAINT ck_character_base_warehouse_capacity CHECK (
                    warehouse_capacity BETWEEN 40 AND 360
                    AND warehouse_capacity % 40 = 0
                );

            ALTER TABLE public.character_items
                DROP CONSTRAINT ck_character_items_location_slot_domain,
                ADD CONSTRAINT ck_character_items_location_slot_domain CHECK (
                    (item_location = 0 AND slot_index BETWEEN 0 AND 23)
                    OR (item_location = 1
                        AND slot_index BETWEEN 0 AND 32767)
                    OR (item_location = 2
                        AND slot_index BETWEEN -32768 AND -1)
                    OR (item_location = 3
                        AND slot_index BETWEEN 0 AND 359)
                );

            ALTER TABLE public.warehouse_expansion_policy_revisions
                DROP CONSTRAINT ck_warehouse_policy_level_count,
                ADD CONSTRAINT ck_warehouse_policy_level_count
                    CHECK (level_count BETWEEN 1 AND 9);

            ALTER TABLE public.warehouse_expansion_policy_levels
                DROP CONSTRAINT ck_warehouse_policy_level_capacity,
                DROP CONSTRAINT ck_warehouse_policy_level_stock_shape,
                ADD CONSTRAINT ck_warehouse_policy_level_capacity CHECK (
                    capacity BETWEEN 40 AND 360
                    AND capacity % 40 = 0
                ),
                ADD CONSTRAINT ck_warehouse_policy_level_key_shape CHECK (
                    key_item_id > 0
                    AND (
                        (capacity = 40 AND key_cost = 0)
                        OR (capacity > 40 AND key_cost BETWEEN 1 AND 99)
                    )
                );

            ALTER TABLE public.warehouse_expansion_settlements
                DROP CONSTRAINT ck_warehouse_expansion_capacities,
                ADD CONSTRAINT ck_warehouse_expansion_capacities CHECK (
                    previous_capacity BETWEEN 40 AND 320
                    AND previous_capacity % 40 = 0
                    AND current_capacity = previous_capacity + 40
                );

            CREATE OR REPLACE FUNCTION public.guard_warehouse_policy_revision()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $guard_warehouse_policy_revision$
            DECLARE
                actual_count integer;
                actual_sha256 varchar(64);
                actual_capacities smallint[];
                expected_capacities smallint[];
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION
                        'Warehouse policy revisions are append-only.'
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
                    SELECT array_agg((ordinal * 40)::smallint ORDER BY ordinal)
                      INTO expected_capacities
                    FROM generate_series(1, NEW.level_count)
                        AS series(ordinal);
                    SELECT upper(encode(sha256(convert_to(
                               public.warehouse_expansion_policy_canonical(
                                   NEW.revision),
                               'UTF8')), 'hex'))
                      INTO actual_sha256;
                    IF actual_count <> NEW.level_count
                       OR actual_capacities <> expected_capacities
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

            INSERT INTO public.warehouse_expansion_policy_revisions (
                revision, sha256, level_count, source, created_by)
            VALUES (
                2,
                '417B28788C5BFA91341E2E7818C999002FD79B4B46988BC5DB2793C5F7BD37C8',
                9,
                'reborn-nine-box-warehouse-expansion-v2',
                'server-baseline-v2');

            INSERT INTO public.warehouse_expansion_policy_levels (
                revision, capacity, key_cost, key_item_id)
            VALUES
                (2,  40, 0, 4102),
                (2,  80, 1, 4102),
                (2, 120, 2, 4102),
                (2, 160, 3, 4102),
                (2, 200, 4, 4102),
                (2, 240, 5, 4102),
                (2, 280, 6, 4102),
                (2, 320, 7, 4102),
                (2, 360, 8, 4102);

            DO $publish_warehouse_policy_v2$
            BEGIN
                UPDATE public.warehouse_expansion_policy_publication
                SET revision = 2,
                    policy_sha256 =
                        '417B28788C5BFA91341E2E7818C999002FD79B4B46988BC5DB2793C5F7BD37C8',
                    publication_version = 2,
                    updated_by = 'server-baseline-v2'
                WHERE family = 'warehouse-expansion'
                  AND revision = 1
                  AND policy_sha256 =
                      '05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC'
                  AND publication_version = 1;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Warehouse policy publication is not the expected v1 predecessor.'
                        USING ERRCODE = '55000';
                END IF;
            END;
            $publish_warehouse_policy_v2$;

            COMMENT ON COLUMN public.character_base.warehouse_capacity IS
                'Character-owned accessible warehouse cells in contiguous 40-cell boxes, up to the audited 360-cell client ceiling.';
            """);
}
