using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetLevelMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_021_pet_savvy_semantics_hardening";
    private const string PreviousMigrationChecksum =
        "309A8A24F8F02D17D87D93E623319BCA2834F151976095624C0753FA77F60019";
    private const string MigrationId =
        "20260729_022_pet_level_progression";
    private const string MigrationChecksum =
        "86C581294D06B00E64AA8C7F84C79019521BCA2E3B860B09FBA77942E5BD288D";
    private const string NextMigrationId =
        "20260729_023_npc_content_release";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var index = catalog
            .Select((migration, migrationIndex) =>
                (migration, migrationIndex))
            .Single(entry => entry.migration.Id == MigrationId)
            .migrationIndex;
        var migration = catalog[index];

        Check.Equal(
            MigrationChecksum,
            migration.Checksum,
            "applied pet level migration checksum is immutable");
        Check.Equal(
            PreviousMigrationId,
            catalog[index - 1].Id,
            "pet level migration follows savvy hardening");
        Check.Equal(
            PreviousMigrationChecksum,
            catalog[index - 1].Checksum,
            "pet level migration preserves its applied predecessor");
        Check.Equal(
            NextMigrationId,
            catalog[index + 1].Id,
            "pet level migration has the expected forward-only successor");

        var sql = migration.Sql;
        Check.True(
            sql.Contains(
                "ck_pet_operation_audit_operation_v4",
                StringComparison.Ordinal) &&
            sql.Contains("'level_up'", StringComparison.Ordinal) &&
            sql.Contains(
                "VALIDATE CONSTRAINT",
                StringComparison.Ordinal) &&
            sql.Contains(
                "DROP CONSTRAINT",
                StringComparison.Ordinal),
            "migration safely replaces and validates the audit operation constraint");
        Check.True(
            sql.Contains("10285", StringComparison.Ordinal) &&
            sql.Contains(
                "'C2S'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'PetLevelUpgradeRequest'",
                StringComparison.Ordinal) &&
            sql.Contains("10286", StringComparison.Ordinal) &&
            sql.Contains(
                "'S2C'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'PetLevelUpgrade'",
                StringComparison.Ordinal),
            "migration records both exact native pet level opcodes");
        Check.True(
            !sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DELETE FROM",
                StringComparison.OrdinalIgnoreCase),
            "pet level migration contains no destructive data operation");
        return Task.CompletedTask;
    }
}
