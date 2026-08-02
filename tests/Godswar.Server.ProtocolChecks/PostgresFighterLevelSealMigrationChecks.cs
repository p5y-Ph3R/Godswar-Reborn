using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresFighterLevelSealMigrationChecks
{
    public const string CheckName =
        "PostgreSQL fighter-level seal migration contract";

    private const string MigrationId =
        "20260801_047_fighter_level_seal";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;

        Check.True(
            sql.Contains(
                "ALTER TABLE public.character_base",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD COLUMN fighter_level_sealed boolean",
                StringComparison.Ordinal) &&
            sql.Contains(
                "NOT NULL DEFAULT false",
                StringComparison.Ordinal),
            "level sealing is durable, required, and disabled for existing characters");
        Check.True(
            sql.Contains(
                "ck_character_base_fighter_level_seal",
                StringComparison.Ordinal) &&
            sql.Contains(
                "NOT fighter_level_sealed",
                StringComparison.Ordinal) &&
            sql.Contains(
                "OR fighter_job_lv = 89",
                StringComparison.Ordinal),
            "a sealed character is constrained to the level 89 fighter cap");
        Check.True(
            sql.Contains("NOT VALID", StringComparison.Ordinal) &&
            sql.Contains(
                "VALIDATE CONSTRAINT",
                StringComparison.Ordinal) &&
            sql.LastIndexOf(
                "ck_character_base_fighter_level_seal",
                StringComparison.Ordinal) >
            sql.IndexOf("NOT VALID", StringComparison.Ordinal),
            "the level-seal invariant is staged and then fully validated");
        Check.True(
            sql.Contains(
                "COMMENT ON COLUMN",
                StringComparison.Ordinal) &&
            sql.Contains(
                "public.character_base.fighter_level_sealed",
                StringComparison.Ordinal),
            "the durable authority is documented in PostgreSQL");
        Check.True(
            !sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DROP ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase),
            "the migration neither seals nor rewrites existing character state");

        Check.True(
            SealInvariant(false, 1) &&
            SealInvariant(false, 89) &&
            SealInvariant(false, 120) &&
            SealInvariant(true, 89) &&
            !SealInvariant(true, 88) &&
            !SealInvariant(true, 90),
            "the pure implication truth table accepts only level 89 when sealed");

        return Task.CompletedTask;
    }

    private static bool SealInvariant(bool sealedLevel, int fighterLevel) =>
        !sealedLevel || fighterLevel == 89;
}
