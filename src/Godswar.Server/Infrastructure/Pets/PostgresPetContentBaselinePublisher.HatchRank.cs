using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private static Task InsertHatchRankStepsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_hatch_rank_steps (
                revision, aptitude, outcome_order, rank, weight)
            SELECT @revision,
                   (content->>'Aptitude')::smallint,
                   (content->>'OutcomeOrder')::smallint,
                   (content->>'Rank')::numeric,
                   (content->>'Weight')::smallint
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, aptitude, outcome_order) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.HatchRankSteps,
            connection,
            transaction,
            cancellationToken);
}
