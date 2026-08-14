using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetHatchEvidenceHardeningMigrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable pet hatch-rank evidence migration";
    private const string MigrationId =
        "20260812_082_pet_hatch_evidence_hardening";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var index = migrations
            .Select((migration, order) => (migration, order))
            .Single(value => value.migration.Id == MigrationId)
            .order;
        var migration = migrations[index];
        var sql = migration.Sql;

        Check.True(
            index > 0 &&
            migrations[index - 1].Id == "20260812_081_pet_rank_content" &&
            sql.Contains(
                "character_pets contains inconsistent hatch-rank evidence",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_character_pets_birth_rank_hundredths",
                StringComparison.Ordinal) &&
            sql.Contains(
                "fk_character_pets_hatch_rank_evidence",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (revision, aptitude, outcome_order, rank)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet hatch-rank evidence is immutable",
                StringComparison.Ordinal) &&
            sql.Contains(
                "NEW.aptitude IS DISTINCT FROM OLD.aptitude",
                StringComparison.Ordinal) &&
            sql.Contains(
                "new pets require complete hatch-rank evidence",
                StringComparison.Ordinal) &&
            sql.Contains(
                "content_revision.sealed_at IS NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet hatch-rank evidence does not match published content",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE INSERT OR UPDATE ON public.character_pets",
                StringComparison.Ordinal),
            "migration 082 appends fail-closed hatch-evidence binding without rewriting migration 081");

        return Task.CompletedTask;
    }
}
