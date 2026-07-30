namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<bool> DeleteCharacterAsync(
        int accountId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        characterName = CleanCharacterName(characterName);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockAccount = connection.CreateCommand())
        {
            lockAccount.Transaction = transaction;
            lockAccount.CommandText =
                """
                SELECT id
                FROM accounts
                WHERE id = @accountId
                FOR UPDATE;
                """;
            lockAccount.Parameters.AddWithValue(
                "accountId",
                accountId);
            if (await lockAccount.ExecuteScalarAsync(
                    cancellationToken) is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        await using (var guardStream = connection.CreateCommand())
        {
            guardStream.Transaction = transaction;
            guardStream.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM outbox_consumer_positions
                    WHERE consumer_key = 'character_lifecycle_v1'
                      AND aggregate_type =
                          'account_character_slot'
                      AND aggregate_key =
                          @accountId::text || ':0'
                    UNION ALL
                    SELECT 1
                    FROM outbox_events
                    WHERE consumer_key = 'character_lifecycle_v1'
                      AND aggregate_type =
                          'account_character_slot'
                      AND aggregate_key =
                          @accountId::text || ':0'
                );
                """;
            guardStream.Parameters.AddWithValue(
                "accountId",
                accountId);
            if (await guardStream.ExecuteScalarAsync(
                    cancellationToken) is true)
            {
                throw new
                    CharacterLifecycleDurableStreamActiveException();
            }
        }

        var deletedAt = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH target AS MATERIALIZED (
                SELECT character.id
                FROM character_base character
                WHERE character.account_id = @accountId
                  AND character.character_slot = @characterSlot
                  AND character.name = @name
                  AND character.lifecycle_state = 'active'
                  AND character.checkpoint_owner_id IS NULL
                FOR UPDATE
            ),
            reserved AS (
                UPDATE accounts account
                SET character_lifecycle_version =
                        account.character_lifecycle_version + 1
                FROM target
                WHERE account.id = @accountId
                RETURNING account.character_lifecycle_version
            ),
            deleted AS (
                UPDATE character_base character
                SET lifecycle_state = 'deleted',
                    lifecycle_version =
                        reserved.character_lifecycle_version,
                    deleted_at = @deletedAt,
                    restore_until = @restoreUntil,
                    purge_after = @purgeAfter
                FROM target, reserved
                WHERE character.id = target.id
                  AND character.account_id = @accountId
                  AND character.lifecycle_state = 'active'
                RETURNING character.id
            )
            SELECT id
            FROM deleted;
            """;
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue(
            "characterSlot",
            CharacterLifecyclePolicy.SingleCharacterSlot);
        command.Parameters.AddWithValue("name", characterName);
        command.Parameters.AddWithValue(
            "deletedAt",
            deletedAt.UtcDateTime);
        command.Parameters.AddWithValue(
            "restoreUntil",
            (deletedAt +
                CharacterLifecyclePolicy.DefaultRestoreWindow).UtcDateTime);
        command.Parameters.AddWithValue(
            "purgeAfter",
            (deletedAt +
                CharacterLifecyclePolicy.DefaultRestoreWindow +
                CharacterLifecyclePolicy.DefaultPurgeDelay).UtcDateTime);
        var deleted = await command.ExecuteScalarAsync(
            cancellationToken) is not null;
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
