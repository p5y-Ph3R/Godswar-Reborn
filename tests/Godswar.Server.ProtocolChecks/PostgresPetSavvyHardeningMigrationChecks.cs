using System.Text.RegularExpressions;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetSavvyHardeningMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_020_pet_savvy_semantics";
    private const string PreviousMigrationChecksum =
        "847BD78F4792AB9EC28DEFE3E94EB2FB4FDCDBC931FB92FF6DBF35FC98D1BED6";
    private const string MigrationId =
        "20260729_021_pet_savvy_semantics_hardening";
    private const string MigrationChecksum =
        "309A8A24F8F02D17D87D93E623319BCA2834F151976095624C0753FA77F60019";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var migrationIndex = catalog
            .Select((migration, index) => (migration, index))
            .Single(entry => entry.migration.Id == MigrationId)
            .index;
        var migration = catalog[migrationIndex];
        var sql = migration.Sql;

        Check.Equal(
            MigrationChecksum,
            migration.Checksum,
            "pet savvy hardening migration checksum is pinned");
        Check.Equal(
            catalog.Count - 4,
            migrationIndex,
            "pet savvy hardening remains immediately before pet leveling");
        Check.Equal(
            "20260729_022_pet_level_progression",
            catalog[migrationIndex + 1].Id,
            "pet savvy hardening has the expected forward-only successor");
        Check.True(
            migrationIndex > 0,
            "pet savvy hardening has an immutable predecessor");
        var previous = catalog[migrationIndex - 1];
        Check.Equal(
            PreviousMigrationId,
            previous.Id,
            "pet savvy hardening follows migration 020");
        Check.Equal(
            PreviousMigrationChecksum,
            previous.Checksum,
            "migration 020 remains byte-for-byte immutable");

        CheckProvenanceConstraint(sql);
        CheckGrowthAndProgressionConstraints(sql);
        CheckPreflightValidation(sql);
        return Task.CompletedTask;
    }

    private static void CheckProvenanceConstraint(string sql)
    {
        Check.True(
            sql.Contains(
                "DROP CONSTRAINT ck_character_pets_savvy_provenance",
                StringComparison.Ordinal) &&
            Count(
                sql,
                "rarity_added_savvy_policy_version IS NOT NULL") >= 2 &&
            Count(
                sql,
                "initial_savvy_source_version IS NOT NULL") >= 2 &&
            sql.Contains(
                "VALIDATE CONSTRAINT\n        ck_character_pets_savvy_provenance",
                StringComparison.Ordinal),
            "migration replaces and validates the nullable provenance constraint");
    }

    private static void CheckGrowthAndProgressionConstraints(string sql)
    {
        Check.True(
            sql.Contains(
                "ADD CONSTRAINT ck_pet_stat_growth_x1_birth_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "birth_initial_savvy = base_growth_rate",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD CONSTRAINT ck_pet_stat_initial_savvy_progression",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy >= birth_initial_savvy",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD CONSTRAINT ck_pet_stat_added_savvy_progression",
                StringComparison.Ordinal) &&
            sql.Contains(
                "added_savvy >= rarity_added_savvy",
                StringComparison.Ordinal),
            "migration adds persistent birth and progression constraints");
    }

    private static void CheckPreflightValidation(string sql)
    {
        Check.True(
            Regex.IsMatch(
                sql,
                @"initial_savvy_source_version\s*=\s*'growth-x1-v1'",
                RegexOptions.CultureInvariant) &&
            Regex.IsMatch(
                sql,
                @"stat\.birth_initial_savvy\s+IS DISTINCT FROM\s+stat\.base_growth_rate",
                RegexOptions.CultureInvariant) &&
            sql.Contains(
                "count(DISTINCT stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            Regex.IsMatch(
                sql,
                @"count\(\s*DISTINCT stat\.rarity_added_savvy\s*\)\s*<\s*2",
                RegexOptions.CultureInvariant) &&
            sql.Contains(
                "stat.rarity_added_savvy IS NULL",
                StringComparison.Ordinal),
            "migration preflight rejects equal, incomplete, or inconsistent growth-x1 baselines");
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(
                   fragment,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }
}
