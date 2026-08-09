using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckFashionSlotConsistencyMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260810_060_fashion_slot_consistency");

        Check.True(
            migration.Sql.Contains(
                "SET slot_index = 12",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "legacy_item.item_location = 0",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "legacy_item.slot_index = 13",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "template.kind = 'stylish'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "template.equipment_slot = 12",
                StringComparison.Ordinal),
            "fashion migration moves only equipped stylish rows from legacy slot 13");
        Check.True(
            migration.Sql.Contains(
                "target_item.slot_index = 12",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "AND NOT EXISTS",
                StringComparison.Ordinal),
            "fashion migration preserves a legacy row when native slot 12 is occupied");
        Check.True(
            migration.Sql.Contains(
                "JOIN public.character_inventory_baseline_items",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "FROM public.character_inventory_ledger AS ledger_item",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ledger_item.item_instance_id = legacy_item.id",
                StringComparison.Ordinal),
            "fashion migration limits direct repair to baseline-only item instances");
        Check.True(
            migration.Sql.Contains(
                "UPDATE public.character_inventory_baseline_items",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'{slot_index}'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "trg_character_inventory_baseline_items_immutable",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "UPDATE public.character_inventory_ledger",
                StringComparison.OrdinalIgnoreCase),
            "fashion repair keeps baseline projection consistent without rewriting ledger history");
        Check.True(
            !migration.Sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase),
            "fashion migration never deletes an ambiguous player item");
        var rankFilterOffset = migration.Sql.IndexOf(
            "AND kind NOT IN",
            StringComparison.Ordinal);
        Check.True(
            rankFilterOffset >= 0 &&
            migration.Sql.IndexOf(
                "'stylish'",
                rankFilterOffset,
                StringComparison.Ordinal) >= 0,
            "fashion items cannot contribute to the ordinary armor-rank ladder");
    }
}
