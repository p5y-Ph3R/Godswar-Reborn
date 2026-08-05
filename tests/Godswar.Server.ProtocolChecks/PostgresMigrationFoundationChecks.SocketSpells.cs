using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckSocketSpellItemMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260804_057_socket_spell_item_templates");
        foreach (var (itemId, name) in new (int, string)[]
                 {
                     (4270, "Socket Spell I"),
                     (4271, "Socket Spell II"),
                     (4272, "Socket Spell III"),
                     (4273, "Socket Spell IV")
                 })
        {
            Check.True(
                migration.Sql.Contains(
                    $"({itemId}, 'Smithing{itemId}', '{name}')",
                    StringComparison.Ordinal),
                $"Socket Spell migration contains exact item {itemId}");
        }

        Check.True(
            migration.Sql.Contains("'consume item'", StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'./Localization/en_us/UI/Texture/Icon.gwo'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains("'108,900'", StringComparison.Ordinal) &&
            migration.Sql.Contains("'Overlap', '99'", StringComparison.Ordinal) &&
            !migration.Sql.Contains("'BindType'", StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ON CONFLICT (id) DO UPDATE",
                StringComparison.Ordinal),
            "Socket Spell migration preserves stock stack, icon, binding, " +
            "and idempotent mutable-FK compatibility");
    }
}
