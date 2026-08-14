using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckStockHolyStoneMaterialMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260804_058_stock_holy_stone_material_templates");
        var zephyrMigration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260810_064_zephyr_holy_stone_material_templates");
        Check.Equal(
            "6C8C6D538054E575DE084EF7027F8AD9CB4DB325F69774EEA9F481508BFAD11A",
            migration.Checksum,
            "applied Holy Stone migration 058 remains immutable");
        foreach (var seed in
                 HolyStoneMaterialItemContentBaseline.ItemTemplates)
        {
            var sqlName = seed.DisplayName.Replace("'", "''");
            var owningMigration = seed.Id is 9032 or >= 9090 and <= 9093
                ? zephyrMigration
                : migration;
            Check.True(
                owningMigration.Sql.Contains(
                    $"({seed.Id}, '{seed.NameKey}', '{sqlName}'",
                    StringComparison.Ordinal) &&
                owningMigration.Sql.Contains(
                    $"'{seed.Icon}'",
                    StringComparison.Ordinal),
                $"forward-only Holy Stone migrations contain item {seed.Id}");
        }

        Check.True(
            migration.Sql.Contains(
                "'./Localization/en_us/UI/Texture/Icon.gwo'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'./Localization/en_us/UI/Texture/Icon2.gwo'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'Overlap', material.overlap",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'SpecialFlag', material.special_flag",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "jsonb_strip_nulls",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ON CONFLICT (id) DO UPDATE",
                StringComparison.Ordinal),
            "stock Holy Stone migration preserves exact variable metadata " +
            "and reconciles mutable foreign-key rows idempotently");
        Check.True(
            !migration.Sql.Contains("Zephyr", StringComparison.Ordinal) &&
            zephyrMigration.Sql.Contains(
                "'./Localization/en_us/UI/Texture/Icon5.gwo'",
                StringComparison.Ordinal) &&
            zephyrMigration.Sql.Contains(
                "'SpecialFlag', material.special_flag",
                StringComparison.Ordinal) &&
            zephyrMigration.Sql.Contains(
                "ON CONFLICT (id) DO UPDATE",
                StringComparison.Ordinal),
            "Zephyr materials use a separate idempotent forward migration");
    }
}
