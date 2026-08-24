namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateWarehouseFoundation() =>
        new(
            "20260822_107_warehouse_foundation",
            "Create character warehouse storage and expansion policy authority",
            WarehouseSchemaSql + WarehouseGuardSql + WarehouseSeedSql);
}
