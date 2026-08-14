using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetConsumableProjectionMigrationChecks
{
    private const string MigrationId =
        "20260811_076_pet_consumable_mutable_projection";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            value => value.Id == MigrationId);
        var sql = migration.Sql;

        Check.True(
            sql.Contains(
                "template.equipment_slot NOT IN (-1, 0)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "template.stats IS DISTINCT FROM expected.stats",
                StringComparison.Ordinal) &&
            sql.Contains(
                "RAISE EXCEPTION",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE id IN (10103, 10104, 10105, 10107)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "AND equipment_slot = -1",
                StringComparison.Ordinal),
            "pet-consumable migration accepts only the exact legacy fingerprint and normalizes only its slot marker");
        Check.True(
            !sql.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase),
            "pet-consumable migration cannot overwrite conflicts or delete inventory content");
        return Task.CompletedTask;
    }
}
