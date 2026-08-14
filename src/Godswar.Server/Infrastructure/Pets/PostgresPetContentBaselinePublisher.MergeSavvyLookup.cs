using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private static Task InsertMergeSavvyLookupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_merge_savvy_lookup (
                revision, minimum_savvy_difference, base_increase)
            SELECT @revision,
                   (content->>'MinimumSavvyDifference')::integer,
                   (content->>'BaseIncrease')::integer
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, minimum_savvy_difference) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.MergeSavvyLookup,
            connection,
            transaction,
            cancellationToken);
}
