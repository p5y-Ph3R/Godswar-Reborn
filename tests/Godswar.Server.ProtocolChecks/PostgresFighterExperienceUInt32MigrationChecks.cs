using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresFighterExperienceUInt32MigrationChecks
{
    public const string CheckName =
        "PostgreSQL UInt32 fighter-EXP migration contract";

    private const string MigrationId =
        "20260801_048_fighter_experience_uint32";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;

        Check.True(
            sql.Contains(
                "ALTER COLUMN fighter_job_exp TYPE bigint",
                StringComparison.Ordinal) &&
            sql.Contains(
                "USING fighter_job_exp::bigint",
                StringComparison.Ordinal),
            "fighter EXP is widened without truncating existing values");
        Check.True(
            sql.Contains(
                "ck_character_base_fighter_job_exp_uint32",
                StringComparison.Ordinal) &&
            sql.Contains("fighter_job_exp >= 0", StringComparison.Ordinal) &&
            sql.Contains(
                "fighter_job_exp <= 4294967295",
                StringComparison.Ordinal),
            "PostgreSQL enforces the complete unsigned 32-bit domain");
        Check.True(
            sql.Contains("NOT VALID", StringComparison.Ordinal) &&
            sql.Contains("VALIDATE CONSTRAINT", StringComparison.Ordinal),
            "the new bound is staged before validation");
        Check.True(
            !sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DROP ", StringComparison.OrdinalIgnoreCase),
            "the widening migration does not rewrite gameplay values");
        Check.Equal(
            4_294_967_295L,
            PlayerExperienceCatalog.MaximumStoredExperience,
            "authoritative fighter EXP matches the client UInt32 ceiling");

        return Task.CompletedTask;
    }
}
