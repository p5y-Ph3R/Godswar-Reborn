using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetOwnerMergeContentMigrationChecks
{
    private const string MigrationId =
        "20260811_073_pet_owner_merge_content";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var index = catalog
            .Select((migration, position) => (migration, position))
            .Single(entry => entry.migration.Id == MigrationId)
            .position;
        var sql = catalog[index].Sql;

        Check.Equal(
            "20260811_072_pet_quality_innate_talents",
            catalog[index - 1].Id,
            "owner-Merge content follows pet talent reconciliation");
        CheckSchema(sql);
        CheckPublicationGuards(sql);
        CheckProjectionProvenance(sql);
        CheckRebornInternalChannels();
        return Task.CompletedTask;
    }

    private static void CheckSchema(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.pet_owner_merge_effect_types",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TABLE public.pet_owner_merge_savvy_types",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TABLE public.pet_owner_merge_content_revisions",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TABLE public.pet_owner_merge_effect_bases",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TABLE public.pet_owner_merge_savvy_bands",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TABLE public.pet_owner_merge_rates",
                StringComparison.Ordinal) &&
            sql.Contains(
                "band_count = 5",
                StringComparison.Ordinal) &&
            sql.Contains(
                "rate_count = 95",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (rate_per_savvy >= 0)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "source_savvy = 'agility'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "source_savvy = 'luck'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "fk_pet_owner_merge_rates_savvy",
                StringComparison.Ordinal) &&
            sql.Contains(
                "trg_pet_owner_merge_effect_types_immutable",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE VIEW public.published_pet_owner_merge_balance",
                StringComparison.Ordinal),
            "owner-Merge content preserves all exact typed mappings");
    }

    private static void CheckPublicationGuards(string sql)
    {
        Check.True(
            sql.Contains(
                "pet_owner_merge_content_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "validate_pet_owner_merge_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has non-contiguous bands",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has incomplete typed rates",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has an increasing marginal rate",
                StringComparison.Ordinal) &&
            sql.Contains(
                "trg_pet_owner_merge_publication_no_delete",
                StringComparison.Ordinal),
            "only complete immutable owner-Merge revisions can publish");
    }

    private static void CheckProjectionProvenance(string sql)
    {
        Check.True(
            sql.Contains(
                "ADD COLUMN balance_revision varchar(64)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "fk_character_pet_bonuses_balance_revision",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ix_character_pet_bonuses_balance_revision",
                StringComparison.Ordinal),
            "persisted owner-Merge projections expose stale balance revisions");
    }

    private static void CheckRebornInternalChannels()
    {
        var migration = PostgresSchemaMigrationCatalog
            .CreatePetOwnerMergeRebalance();
        Check.True(
            migration.Id ==
                "20260821_097_pet_owner_merge_rebalance" &&
            migration.Sql.Contains(
                "reborn_technique_physical_reduction",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "reborn_technique_magic_reduction",
                StringComparison.Ordinal) &&
            migration.Sql.Contains("1001, 1002", StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "pet_owner_merge_effect_bases",
                StringComparison.Ordinal),
            "Reborn Technique channels extend derived storage without " +
            "changing the sixteen native content effects");
    }
}
