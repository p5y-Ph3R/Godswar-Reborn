namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateWarehouseManagerStorageKeyCap() =>
        new(
            "20260824_111_warehouse_manager_storage_key_cap",
            "Limit Warehouse Manager Storage Box Keys to four boxes",
            """
            INSERT INTO public.warehouse_expansion_policy_revisions (
                revision, sha256, level_count, source, created_by)
            VALUES (
                3,
                '05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC',
                4,
                'warehouse-manager-storage-key-cap-v3',
                'server-baseline-v3');

            INSERT INTO public.warehouse_expansion_policy_levels (
                revision, capacity, key_cost, key_item_id)
            VALUES
                (3,  40, 0, 4102),
                (3,  80, 1, 4102),
                (3, 120, 2, 4102),
                (3, 160, 3, 4102);

            DO $publish_warehouse_policy_v3$
            BEGIN
                UPDATE public.warehouse_expansion_policy_publication
                SET revision = 3,
                    policy_sha256 =
                        '05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC',
                    publication_version = 3,
                    updated_by = 'server-baseline-v3'
                WHERE family = 'warehouse-expansion'
                  AND revision = 2
                  AND policy_sha256 =
                      '417B28788C5BFA91341E2E7818C999002FD79B4B46988BC5DB2793C5F7BD37C8'
                  AND publication_version = 2;
                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Warehouse policy publication is not the expected v2 predecessor.'
                        USING ERRCODE = '55000';
                END IF;
            END;
            $publish_warehouse_policy_v3$;
            """);
}
