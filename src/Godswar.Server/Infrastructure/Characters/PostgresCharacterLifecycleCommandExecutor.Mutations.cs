using Godswar.Server.State;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task<StoredCharacter?> ReadActiveCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id,
                name,
                lifecycle_state,
                lifecycle_version,
                restore_until,
                purge_after,
                COALESCE(
                    restore_until > transaction_timestamp(),
                    false
                ),
                COALESCE(
                    purge_after <= transaction_timestamp(),
                    false
                ),
                checkpoint_owner_id IS NOT NULL
            FROM public.character_base
            WHERE account_id = @accountId
              AND server_id = @realmId
              AND character_slot = 0
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        return await ReadStoredCharacterAsync(command, cancellationToken);
    }

    private async Task<StoredCharacter?> ReadCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id,
                name,
                lifecycle_state,
                lifecycle_version,
                restore_until,
                purge_after,
                COALESCE(
                    restore_until > transaction_timestamp(),
                    false
                ),
                COALESCE(
                    purge_after <= transaction_timestamp(),
                    false
                ),
                checkpoint_owner_id IS NOT NULL
            FROM public.character_base
            WHERE account_id = @accountId
              AND server_id = @realmId
              AND character_slot = 0
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("characterId", characterId);
        return await ReadStoredCharacterAsync(command, cancellationToken);
    }

    private static async Task<StoredCharacter?> ReadStoredCharacterAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var state = reader.GetString(2);
        if (state is not ("active" or "deleted"))
        {
            throw new InvalidDataException(
                "The stored character lifecycle state is invalid.");
        }

        return new StoredCharacter(
            reader.GetInt32(0),
            reader.GetString(1),
            state == "deleted",
            reader.GetInt64(3),
            reader.IsDBNull(4)
                ? null
                : new DateTimeOffset(
                    reader.GetDateTime(4).ToUniversalTime()),
            reader.IsDBNull(5)
                ? null
                : new DateTimeOffset(
                    reader.GetDateTime(5).ToUniversalTime()),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8));
    }

    private async Task<TombstoneTimestamps> TombstoneCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        int characterId,
        long expectedVersion,
        long nextVersion,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET lifecycle_state = 'deleted',
                lifecycle_version = @nextVersion,
                deleted_at = transaction_timestamp(),
                restore_until =
                    transaction_timestamp() + @restoreWindow,
                purge_after =
                    transaction_timestamp() +
                    @restoreWindow +
                    @purgeDelay
            WHERE account_id = @accountId
              AND server_id = @realmId
              AND id = @characterId
              AND character_slot = 0
              AND lifecycle_state = 'active'
              AND checkpoint_owner_id IS NULL
              AND lifecycle_version = @expectedVersion
            RETURNING restore_until, purge_after;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedVersion",
            expectedVersion);
        command.Parameters.AddWithValue("nextVersion", nextVersion);
        command.Parameters.Add(
            "restoreWindow",
            NpgsqlDbType.Interval).Value =
            CharacterLifecyclePolicy.DefaultRestoreWindow;
        command.Parameters.Add(
            "purgeDelay",
            NpgsqlDbType.Interval).Value =
            CharacterLifecyclePolicy.DefaultPurgeDelay;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The character tombstone transition was not exact.");
        }
        var timestamps = new TombstoneTimestamps(
            new DateTimeOffset(reader.GetDateTime(0).ToUniversalTime()),
            new DateTimeOffset(reader.GetDateTime(1).ToUniversalTime()));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The character tombstone transition affected multiple rows.");
        }
        return timestamps;
    }

    private async Task RestoreCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        int characterId,
        long expectedVersion,
        long nextVersion,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET lifecycle_state = 'active',
                lifecycle_version = @nextVersion,
                deleted_at = NULL,
                restore_until = NULL,
                purge_after = NULL
            WHERE account_id = @accountId
              AND server_id = @realmId
              AND id = @characterId
              AND character_slot = 0
              AND lifecycle_state = 'deleted'
              AND lifecycle_version = @expectedVersion
              AND restore_until > transaction_timestamp();
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedVersion",
            expectedVersion);
        command.Parameters.AddWithValue("nextVersion", nextVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The character restore transition was not exact.");
        }
    }

    private async Task PurgeCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        int characterId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_base
            WHERE account_id = @accountId
              AND server_id = @realmId
              AND id = @characterId
              AND character_slot = 0
              AND lifecycle_state = 'deleted'
              AND lifecycle_version = @expectedVersion
              AND purge_after <= transaction_timestamp();
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedVersion",
            expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The character purge transition was not exact.");
        }
    }

    private async Task AdvanceAccountVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        long expectedVersion,
        long nextVersion,
        CancellationToken cancellationToken)
    {
        if (nextVersion != expectedVersion + 1)
        {
            throw new InvalidDataException(
                "The account lifecycle version must advance once.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.account_realm
            SET character_lifecycle_version = @nextVersion
            WHERE account_id = @accountId
              AND realm_id = @realmId
              AND character_lifecycle_version = @expectedVersion;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue(
            "expectedVersion",
            expectedVersion);
        command.Parameters.AddWithValue("nextVersion", nextVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The account lifecycle version did not advance exactly once.");
        }

        if (realmId == RealmId.Tempest)
        {
            await using var legacyMirror = CreateCommand(
                """
                UPDATE public.accounts
                SET character_lifecycle_version = @nextVersion
                WHERE id = @accountId
                  AND character_lifecycle_version = @expectedVersion;
                """,
                connection,
                transaction);
            legacyMirror.Parameters.AddWithValue("accountId", accountId);
            legacyMirror.Parameters.AddWithValue(
                "expectedVersion",
                expectedVersion);
            legacyMirror.Parameters.AddWithValue("nextVersion", nextVersion);
            if (await legacyMirror.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Tempest lifecycle mirror did not advance exactly once.");
            }
        }
    }
}
