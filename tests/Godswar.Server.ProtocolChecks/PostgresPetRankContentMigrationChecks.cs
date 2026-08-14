using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetRankContentMigrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable pet-rank content migration";
    private const string MigrationId =
        "20260812_081_pet_rank_content";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var index = migrations
            .Select((migration, index) => (migration, index))
            .Single(entry => entry.migration.Id == MigrationId)
            .index;
        Check.Equal(
            "20260812_080_pet_basic_savvy_preview",
            migrations[index - 1].Id,
            "pet-rank content follows the latest pet preview migration");

        var sql = migrations[index].Sql;
        AssertContains(
            sql,
            "maximum_rank numeric(8, 2) NOT NULL DEFAULT 655.35",
            "DO $rank_wire_preflight$",
            "rank > 655.35 OR",
            "rank * 100 <> trunc(rank * 100)",
            "rank outside native UInt16 hundredths",
            "ck_character_pets_rank_wire_range",
            "rank * 100 = trunc(rank * 100)",
            "CREATE TABLE public.pet_content_hatch_rank_steps",
            "PRIMARY KEY (revision, aptitude, outcome_order)",
            "CHECK (outcome_order BETWEEN 0 AND 2)",
            "CHECK (rank >= 0 AND rank <= 655.35)",
            "hatch_rank_step_count",
            "trg_pet_content_hatch_rank_immutable",
            "trg_pet_content_hatch_rank_insert_guard",
            "expected.hatch_rank_step_count",
            "CREATE TABLE public.pet_content_merge_rank_lookup",
            "CREATE TABLE public.pet_content_merge_rank_species_factors",
            "CREATE TABLE public.pet_content_merge_rank_spirit_steps",
            "merge_rank_lookup_count",
            "merge_rank_species_factor_count",
            "merge_rank_spirit_step_count",
            "trg_pet_content_merge_rank_lookup_immutable",
            "trg_pet_content_merge_rank_factor_insert_guard",
            "expected.merge_rank_spirit_step_count",
            "ADD COLUMN birth_rank numeric(18, 6) NULL",
            "ADD COLUMN hatch_rank_roll smallint NULL",
            "ADD COLUMN hatch_rank_outcome_order smallint NULL",
            "ADD COLUMN hatch_rank_content_revision varchar(64) NULL",
            "ck_character_pets_hatch_rank_evidence",
            "fk_character_pets_hatch_rank_revision");

        AssertPublicationGuardIncludes(
            sql,
            "pet_content_hatch_rank_steps",
            "hatch_rank_step_count");
        AssertPublicationGuardIncludes(
            sql,
            "pet_content_merge_rank_lookup",
            "merge_rank_lookup_count");
        AssertPublicationGuardIncludes(
            sql,
            "pet_content_merge_rank_species_factors",
            "merge_rank_species_factor_count");
        AssertPublicationGuardIncludes(
            sql,
            "pet_content_merge_rank_spirit_steps",
            "merge_rank_spirit_step_count");

        Check.True(
            !sql.Contains(
                "UPDATE public.character_pets",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "UPDATE character_pets",
                StringComparison.OrdinalIgnoreCase),
            "rank-content migration never rerolls or rewrites legacy pets");
        Check.True(
            sql.Contains(
                "birth_rank IS NULL AND hatch_rank_roll IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "birth_rank IS NOT NULL AND hatch_rank_roll IS NOT NULL",
                StringComparison.Ordinal),
            "hatch evidence is either wholly legacy-null or wholly managed");
        return Task.CompletedTask;
    }

    private static void AssertPublicationGuardIncludes(
        string migrationSql,
        string table,
        string declaredCount)
    {
        const string guardDeclaration =
            "CREATE OR REPLACE FUNCTION public.validate_pet_content_publication()";
        var guardOffset = migrationSql.IndexOf(
            guardDeclaration,
            StringComparison.Ordinal);
        Check.True(
            guardOffset >= 0,
            "pet-rank migration replaces the publication guard");
        var guardSql = migrationSql[guardOffset..];
        Check.True(
            guardSql.Contains(
                $"(SELECT count(*) FROM public.{table}",
                StringComparison.Ordinal) &&
            guardSql.Contains(
                $"expected.{declaredCount}",
                StringComparison.Ordinal),
            $"publication guard requires complete {table}");
    }

    private static void AssertContains(
        string value,
        params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            Check.True(
                value.Contains(fragment, StringComparison.Ordinal),
                $"pet-rank migration contains {fragment}");
        }
    }
}
