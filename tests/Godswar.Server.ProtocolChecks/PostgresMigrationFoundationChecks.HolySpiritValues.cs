using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckHolySpiritEffectivenessMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260805_059_holy_spirit_effectiveness_values");

        for (var socket = 1; socket <= 4; socket++)
        {
            var column = $"holy_socket{socket}_value";
            Check.True(
                migration.Sql.Contains(
                    $"ADD COLUMN IF NOT EXISTS {column} smallint NULL",
                    StringComparison.Ordinal),
                $"Holy Spirit migration adds nullable {column}");
            Check.True(
                migration.Sql.Contains(
                    $"item_state ->> '{column}'",
                    StringComparison.Ordinal),
                $"Holy Spirit canonical state normalizes absent {column} " +
                "and preserves a stored value");
        }

        Check.True(
            migration.Sql.Contains(
                "public.canonical_character_item_state_v3(item_state jsonb)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "public.character_inventory_reconciliation",
                StringComparison.Ordinal),
            "Holy Spirit migration rebinds inventory reconciliation to " +
            "the extended canonical item shape");
        Check.True(
            !migration.Sql.Contains(
                "UPDATE public.character_inventory_baseline_items",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "UPDATE public.character_inventory_ledger",
                StringComparison.OrdinalIgnoreCase),
            "Holy Spirit migration does not rewrite immutable historical evidence");
    }
}
