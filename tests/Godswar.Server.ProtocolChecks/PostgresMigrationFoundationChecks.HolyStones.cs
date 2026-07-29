using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckHolyStoneMaterialMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate =>
                candidate.Id ==
                "20260730_029_holy_stone_material_templates");
        foreach (var itemId in new[]
                 {
                     9030, 9060, 9061, 9062, 9063, 9064,
                     9065, 9066, 9067, 9088, 9089
                 })
        {
            Check.True(
                migration.Sql.Contains(
                    itemId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal),
                $"Holy Stone migration includes item {itemId}");
        }

        Check.True(
            migration.Sql.Contains(
                "'./Localization/en_us/UI/Texture/Icon2.gwo'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'252,0'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'864,36'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'PreStone'",
                StringComparison.Ordinal),
            "Holy Stone migration preserves client-authored metadata");
    }
}
