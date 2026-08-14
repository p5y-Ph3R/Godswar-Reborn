using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private static Task InsertMergeSavvyStepsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_merge_savvy_steps (
                revision, aptitude, spirit_count,
                minimum_increase_per_stat, maximum_increase_per_stat)
            SELECT @revision,
                   (content->>'Aptitude')::smallint,
                   (content->>'SpiritCount')::smallint,
                   (content->>'MinimumIncreasePerStat')::numeric,
                   (content->>'MaximumIncreasePerStat')::numeric
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, aptitude, spirit_count) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.MergeSavvySteps,
            connection,
            transaction,
            cancellationToken);
}
