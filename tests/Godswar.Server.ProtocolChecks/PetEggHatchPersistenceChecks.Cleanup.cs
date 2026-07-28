using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchPersistenceChecks
{
    private static async Task DeleteFixtureAsync(
        string connectionString,
        int accountId,
        string username,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using (var verify = new NpgsqlCommand(
            """
            SELECT true
            FROM accounts account
            INNER JOIN character_base character
                ON character.account_id = account.id
            WHERE account.id = @accountId
              AND account.username = @username
              AND character.id = @characterId
            FOR UPDATE OF account, character;
            """,
            connection,
            transaction))
        {
            verify.Parameters.AddWithValue("accountId", accountId);
            verify.Parameters.AddWithValue("username", username);
            verify.Parameters.AddWithValue(
                "characterId",
                characterId);
            if (await verify.ExecuteScalarAsync() is not true)
            {
                throw new InvalidOperationException(
                    "Pet-egg fixture ownership changed before cleanup.");
            }
        }

        await using (var deletePetAudit = new NpgsqlCommand(
            """
            DELETE FROM pet_operation_audit
            WHERE user_id_snapshot = @characterId;
            """,
            connection,
            transaction))
        {
            deletePetAudit.Parameters.AddWithValue(
                "characterId",
                characterId);
            await deletePetAudit.ExecuteNonQueryAsync();
        }

        await using (var deleteItemAudit = new NpgsqlCommand(
            """
            DELETE FROM character_item_audit
            WHERE user_id = @characterId
              AND source = 'pet-egg-hatch';
            """,
            connection,
            transaction))
        {
            deleteItemAudit.Parameters.AddWithValue(
                "characterId",
                characterId);
            await deleteItemAudit.ExecuteNonQueryAsync();
        }

        await using (var deleteAccount = new NpgsqlCommand(
            """
            DELETE FROM accounts
            WHERE id = @accountId
              AND username = @username;
            """,
            connection,
            transaction))
        {
            deleteAccount.Parameters.AddWithValue(
                "accountId",
                accountId);
            deleteAccount.Parameters.AddWithValue(
                "username",
                username);
            if (await deleteAccount.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException(
                    "Pet-egg fixture cleanup was not exact.");
            }
        }

        await transaction.CommitAsync();
    }
}
