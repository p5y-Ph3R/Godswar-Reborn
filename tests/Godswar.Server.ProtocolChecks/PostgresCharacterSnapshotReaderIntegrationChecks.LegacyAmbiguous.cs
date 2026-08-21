using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task
        AssertLegacyAdditionalCharacterRejectedAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO character_base (account_id, server_id, name)
            VALUES (@accountId, 1, @name)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        try
        {
            _ = await command.ExecuteScalarAsync();
            throw new InvalidOperationException(
                "A second active character bypassed slot uniqueness.");
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.UniqueViolation)
        {
        }
    }
}
