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
        foreach (var seed in
                 HolyStoneMaterialItemContentBaseline.ItemTemplates)
        {
            var sqlName = seed.DisplayName.Replace("'", "''");
            Check.True(
                migration.Sql.Contains(
                    $"({seed.Id}, '{seed.NameKey}', '{sqlName}'",
                    StringComparison.Ordinal) &&
                migration.Sql.Contains(
                    $"'{seed.Icon}'",
                    StringComparison.Ordinal),
                $"stock Holy Stone migration contains item {seed.Id}");
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
    }
}
