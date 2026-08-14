using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private static async Task InsertMergeRankContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_merge_rank_lookup (
                revision, minimum_rank_difference, base_increase)
            SELECT @revision,
                   (content->>'MinimumRankDifference')::integer,
                   (content->>'BaseIncrease')::integer
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, minimum_rank_difference) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.MergeRankLookup,
            connection,
            transaction,
            cancellationToken);

        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_merge_rank_species_factors (
                revision, species_id, factor)
            SELECT @revision,
                   (content->>'SpeciesId')::smallint,
                   (content->>'Factor')::numeric
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, species_id) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.MergeRankSpeciesFactors,
            connection,
            transaction,
            cancellationToken);

        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_merge_rank_spirit_steps (
                revision, spirit_count, minimum_percent, maximum_percent)
            SELECT @revision,
                   (content->>'SpiritCount')::smallint,
                   (content->>'MinimumPercent')::smallint,
                   (content->>'MaximumPercent')::smallint
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, spirit_count) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.MergeRankSpiritSteps,
            connection,
            transaction,
            cancellationToken);
    }
}
