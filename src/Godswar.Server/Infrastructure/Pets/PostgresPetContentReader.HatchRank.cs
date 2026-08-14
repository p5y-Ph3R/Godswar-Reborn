using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    private static async Task<
        IReadOnlyList<PetHatchRankStepContentDefinition>>
        ReadHatchRankStepsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetHatchRankStepContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT aptitude, outcome_order, rank, weight
            FROM pet_content_hatch_rank_steps
            WHERE revision = @revision
            ORDER BY aptitude, outcome_order;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetHatchRankStepContentDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetDecimal(2),
                reader.GetInt16(3)));
        }

        return values;
    }
}
