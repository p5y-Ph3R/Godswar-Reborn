using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresIntegrationFixtureCleanup
{
    private static readonly HashSet<string> AllowedAuditSources =
        new(StringComparer.Ordinal)
        {
            "forge-consume",
            "gear-enhancement-consume",
            "gear-mentor-consume"
        };

    public static async Task DeleteAccountAndAuditsAsync(
        string connectionString,
        int accountId,
        string username,
        int? characterId,
        string auditSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (!AllowedAuditSources.Contains(auditSource))
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditSource),
                "The PostgreSQL fixture audit source is not allowlisted.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (characterId.HasValue)
        {
            await AssertExactFixtureOwnershipAsync(
                connection,
                transaction,
                accountId,
                username,
                characterId.Value);
            await DeleteExactFixtureAuditsAsync(
                connection,
                transaction,
                characterId.Value,
                auditSource);
        }

        await using (var deleteAccount = new NpgsqlCommand("""
            DELETE FROM accounts
            WHERE id = @accountId AND username = @username;
            """, connection, transaction))
        {
            deleteAccount.Parameters.AddWithValue("accountId", accountId);
            deleteAccount.Parameters.AddWithValue("username", username);
            var deletedAccounts = await deleteAccount.ExecuteNonQueryAsync();
            if (deletedAccounts != 1)
            {
                throw new InvalidOperationException(
                    "PostgreSQL fixture account cleanup was not exact.");
            }
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertExactFixtureOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        string username,
        int characterId)
    {
        await using var command = new NpgsqlCommand("""
            SELECT true
            FROM accounts AS fixture_account
            JOIN character_base AS fixture_character
              ON fixture_character.account_id = fixture_account.id
            WHERE fixture_account.id = @accountId
              AND fixture_account.username = @username
              AND fixture_character.id = @characterId
            FOR UPDATE OF fixture_account, fixture_character;
            """, connection, transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("characterId", characterId);
        if (await command.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException(
                "PostgreSQL fixture character ownership was not exact.");
        }
    }

    private static async Task DeleteExactFixtureAuditsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        string auditSource)
    {
        await using var command = new NpgsqlCommand("""
            DELETE FROM character_item_audit
            WHERE user_id = @characterId
              AND source = @auditSource;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("auditSource", auditSource);
        await command.ExecuteNonQueryAsync();
    }
}
