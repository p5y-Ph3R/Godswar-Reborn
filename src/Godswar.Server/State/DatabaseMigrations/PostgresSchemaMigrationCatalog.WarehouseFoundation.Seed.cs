namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string WarehouseSeedSql =
        """
        INSERT INTO public.warehouse_expansion_policy_revisions (
            revision, sha256, level_count, source, created_by)
        VALUES (
            1,
            '05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC',
            4,
            'reviewed-stock-warehouse-expansion-v1',
            'server-baseline-v1');

        INSERT INTO public.warehouse_expansion_policy_levels (
            revision, capacity, key_cost, key_item_id)
        VALUES
            (1, 40, 0, 4102),
            (1, 80, 1, 4102),
            (1, 120, 2, 4102),
            (1, 160, 3, 4102);

        INSERT INTO public.warehouse_expansion_policy_publication (
            family, revision, policy_sha256,
            publication_version, updated_by)
        VALUES (
            'warehouse-expansion', 1,
            '05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC',
            1, 'server-baseline-v1');

        COMMENT ON COLUMN public.character_base.warehouse_capacity IS
            'Character-owned accessible warehouse cells: 40, 80, 120, or 160.';
        COMMENT ON COLUMN public.character_base.warehouse_revision IS
            'Warehouse aggregate revision; inventory_revision also owns every warehouse item-row mutation.';
        COMMENT ON TABLE public.warehouse_expansion_policy_publication IS
            'Audited singleton CAS pointer; workers pin it until restart.';
        COMMENT ON TABLE public.warehouse_expansion_settlements IS
            'Immutable evidence joining consumed keys to one capacity revision.';
        """;
}
