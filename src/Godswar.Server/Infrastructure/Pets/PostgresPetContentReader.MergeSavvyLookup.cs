using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    private static async Task<
        IReadOnlyList<PetMergeSavvyLookupContentDefinition>>
        ReadMergeSavvyLookupAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetMergeSavvyLookupContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT minimum_savvy_difference, base_increase
            FROM pet_content_merge_savvy_lookup
            WHERE revision = @revision
            ORDER BY minimum_savvy_difference;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetMergeSavvyLookupContentDefinition(
                reader.GetInt32(0),
                checked((ushort)reader.GetInt32(1))));
        }

        return values;
    }
}
