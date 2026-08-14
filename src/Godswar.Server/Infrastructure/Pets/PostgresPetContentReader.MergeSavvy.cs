using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    private static async Task<
        IReadOnlyList<PetMergeSavvyStepContentDefinition>>
        ReadMergeSavvyStepsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetMergeSavvyStepContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT aptitude, spirit_count,
                   minimum_increase_per_stat, maximum_increase_per_stat
            FROM pet_content_merge_savvy_steps
            WHERE revision = @revision
            ORDER BY aptitude, spirit_count;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetMergeSavvyStepContentDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3)));
        }

        return values;
    }
}
