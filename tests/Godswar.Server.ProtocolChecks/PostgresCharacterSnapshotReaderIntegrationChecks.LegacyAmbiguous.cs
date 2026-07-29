using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task<int> InsertLegacyAdditionalCharacterAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO character_base (account_id, name)
            VALUES (@accountId, @name)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        return (int)(await command.ExecuteScalarAsync()
                     ?? throw new InvalidOperationException(
                         "Legacy ambiguous-slot insert returned no ID."));
    }
}
