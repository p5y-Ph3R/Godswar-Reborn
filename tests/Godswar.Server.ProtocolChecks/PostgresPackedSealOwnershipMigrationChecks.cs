using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPackedSealOwnershipMigrationChecks
{
    public const string CheckName =
        "PostgreSQL packed Seal Jade ownership hardening migration";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var position = migrations
            .Select((migration, index) => (migration, index))
            .Single(static value => value.migration.Id ==
                "20260814_092_packed_seal_ownership_hardening");
        var sql = position.migration.Sql;
        Check.True(
            position.index == migrations.Count - 1 &&
            migrations[position.index - 1].Id ==
                "20260813_091_pet_phoenix_rebirth_bracket" &&
            sql.Contains(
                "ADD COLUMN pet_bound_snapshot boolean",
                StringComparison.Ordinal) &&
            sql.Contains(
                "SET pet_bound_snapshot = pet.bound",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ALTER COLUMN pet_bound_snapshot SET NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "trg_sealed_pet_bound_snapshot_immutable",
                StringComparison.Ordinal) &&
            sql.Contains(
                "link_row.pet_bound_snapshot",
                StringComparison.Ordinal) &&
            sql.Contains(
                "OR OLD.bound <> 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "OR NEW.bound <> 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "AND NOT bound",
                StringComparison.Ordinal) &&
            sql.Contains(
                "(item_bound = 1) IS DISTINCT FROM",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet_bound IS DISTINCT FROM",
                StringComparison.Ordinal),
            "migration 092 immutably snapshots pet binding, rejects " +
            "bound owner changes including clear-and-transfer, and " +
            "requires item/pet binding parity");
        Check.True(
            !sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase),
            "migration 092 is forward-only and preserves packed pets");
        return Task.CompletedTask;
    }
}
