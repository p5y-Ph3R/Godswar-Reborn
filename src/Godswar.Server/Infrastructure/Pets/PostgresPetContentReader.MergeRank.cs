using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    private static async Task<
        IReadOnlyList<PetMergeRankLookupContentDefinition>>
        ReadMergeRankLookupAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetMergeRankLookupContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT minimum_rank_difference, base_increase
            FROM pet_content_merge_rank_lookup
            WHERE revision = @revision
            ORDER BY minimum_rank_difference;
            """,
            connection,
            transaction,
            revision);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetMergeRankLookupContentDefinition(
                reader.GetInt32(0),
                checked((ushort)reader.GetInt32(1))));
        }
        return values;
    }

    private static async Task<
        IReadOnlyList<PetMergeRankSpeciesFactorContentDefinition>>
        ReadMergeRankSpeciesFactorsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetMergeRankSpeciesFactorContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT species_id, factor
            FROM pet_content_merge_rank_species_factors
            WHERE revision = @revision
            ORDER BY species_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetMergeRankSpeciesFactorContentDefinition(
                reader.GetInt16(0),
                reader.GetDecimal(1)));
        }
        return values;
    }

    private static async Task<
        IReadOnlyList<PetMergeRankSpiritStepContentDefinition>>
        ReadMergeRankSpiritStepsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetMergeRankSpiritStepContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT spirit_count, minimum_percent, maximum_percent
            FROM pet_content_merge_rank_spirit_steps
            WHERE revision = @revision
            ORDER BY spirit_count;
            """,
            connection,
            transaction,
            revision);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetMergeRankSpiritStepContentDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt16(2)));
        }
        return values;
    }
}
