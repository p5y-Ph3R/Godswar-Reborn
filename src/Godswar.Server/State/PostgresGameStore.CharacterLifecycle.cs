namespace Godswar.Server.State;

using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresGameStore
{
    public Task<bool> DeleteCharacterAsync(
        int accountId,
        string characterName,
        CancellationToken cancellationToken = default) =>
        DeleteCharacterAsync(
            accountId,
            RealmId.Tempest,
            characterName,
            cancellationToken);

    public async Task<bool> DeleteCharacterAsync(
        int accountId,
        RealmId realmId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        characterName = CleanCharacterName(characterName);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var ensureMembership = connection.CreateCommand())
        {
            ensureMembership.Transaction = transaction;
            ensureMembership.CommandText =
                """
                INSERT INTO account_realm (account_id, realm_id)
                SELECT account_row.id, realm.id
                FROM accounts account_row
                CROSS JOIN server realm
                WHERE account_row.id = @accountId
                  AND realm.id = @realmId
                  AND realm.enabled
                ON CONFLICT (account_id, realm_id) DO NOTHING;
                """;
            ensureMembership.Parameters.AddWithValue("accountId", accountId);
            ensureMembership.Parameters.AddWithValue("realmId", realmId.Value);
            await ensureMembership.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lockAccount = connection.CreateCommand())
        {
            lockAccount.Transaction = transaction;
            lockAccount.CommandText =
                """
                SELECT membership.character_lifecycle_version
                FROM account_realm membership
                JOIN server realm
                  ON realm.id = membership.realm_id
                 AND realm.enabled
                WHERE membership.account_id = @accountId
                  AND membership.realm_id = @realmId
                FOR UPDATE;
                """;
            lockAccount.Parameters.AddWithValue(
                "accountId",
                accountId);
            lockAccount.Parameters.AddWithValue("realmId", realmId.Value);
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
                    WHERE consumer_key = @consumerKey
                      AND aggregate_type = @aggregateType
                      AND aggregate_key = @aggregateKey
                    UNION ALL
                    SELECT 1
                    FROM outbox_events
                    WHERE consumer_key = @consumerKey
                      AND aggregate_type = @aggregateType
                      AND aggregate_key = @aggregateKey
                );
                """;
            guardStream.Parameters.AddWithValue(
                "consumerKey",
                CharacterLifecyclePersistenceCodec.ConsumerKeyFor(realmId));
            guardStream.Parameters.AddWithValue(
                "aggregateType",
                CharacterLifecyclePersistenceCodec.AggregateTypeFor(realmId));
            guardStream.Parameters.AddWithValue(
                "aggregateKey",
                CharacterLifecyclePersistenceCodec.AggregateKey(
                    accountId,
                    realmId,
                    CharacterLifecyclePolicy.SingleCharacterSlot));
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
                  AND character.server_id = @realmId
                  AND character.character_slot = @characterSlot
                  AND character.name = @name
                  AND character.lifecycle_state = 'active'
                  AND character.checkpoint_owner_id IS NULL
                FOR UPDATE
            ),
            reserved AS (
                UPDATE account_realm membership
                SET character_lifecycle_version =
                        membership.character_lifecycle_version + 1
                FROM target
                WHERE membership.account_id = @accountId
                  AND membership.realm_id = @realmId
                RETURNING membership.character_lifecycle_version
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
                  AND character.server_id = @realmId
                  AND character.lifecycle_state = 'active'
                RETURNING character.id
            )
            SELECT id
            FROM deleted;
            """;
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
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
        if (deleted && realmId == RealmId.Tempest)
        {
            await using var mirror = connection.CreateCommand();
            mirror.Transaction = transaction;
            mirror.CommandText =
                """
                UPDATE accounts account
                SET character_lifecycle_version =
                    membership.character_lifecycle_version
                FROM account_realm membership
                WHERE account.id = @accountId
                  AND membership.account_id = account.id
                  AND membership.realm_id = @realmId;
                """;
            mirror.Parameters.AddWithValue("accountId", accountId);
            mirror.Parameters.AddWithValue("realmId", realmId.Value);
            if (await mirror.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Tempest lifecycle mirror was not updated.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
