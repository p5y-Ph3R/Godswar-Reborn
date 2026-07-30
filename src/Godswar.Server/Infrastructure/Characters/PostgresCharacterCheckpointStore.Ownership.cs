using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterCheckpointStore
{
    public async Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, characterId);
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownerId),
                "A checkpoint owner ID cannot be empty.");
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_generation =
                    CASE
                        WHEN checkpoint_owner_id = @ownerId
                            THEN checkpoint_owner_generation
                        ELSE checkpoint_owner_generation + 1
                    END,
                checkpoint_owner_id = @ownerId
            WHERE id = @characterId
              AND account_id = @accountId
              AND lifecycle_state = 'active'
            RETURNING
                checkpoint_owner_generation,
                position_revision,
                vitals_revision;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("ownerId", ownerId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ownership = new CharacterCheckpointOwnership(
            new PlayerOwnershipFence(
                ownerId,
                reader.GetInt64(0)),
            reader.GetInt64(1),
            reader.GetInt64(2));
        ownership.Validate();
        return ownership;
    }

    public async Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence owner,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, characterId);
        owner.Validate();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        Guid? storedOwnerId = null;
        long storedGeneration = 0;
        var characterFound = false;
        await using (var select = new NpgsqlCommand(
            """
            SELECT
                checkpoint_owner_id,
                checkpoint_owner_generation
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("accountId", accountId);
            select.Parameters.AddWithValue(
                "characterId",
                characterId);
            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);
            characterFound =
                await reader.ReadAsync(cancellationToken);
            if (characterFound)
            {
                storedOwnerId = reader.IsDBNull(0)
                    ? null
                    : reader.GetGuid(0);
                storedGeneration = reader.GetInt64(1);
            }
        }

        if (!characterFound)
        {
            await transaction.CommitAsync(cancellationToken);
            return CharacterCheckpointReleaseStatus
                .CharacterNotFound;
        }

        if (!storedOwnerId.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return CharacterCheckpointReleaseStatus.AlreadyReleased;
        }

        if (storedOwnerId.Value != owner.OwnerId ||
            storedGeneration != owner.Generation)
        {
            await transaction.CommitAsync(cancellationToken);
            return CharacterCheckpointReleaseStatus.OwnershipLost;
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_id = NULL
            WHERE id = @characterId
              AND account_id = @accountId
              AND checkpoint_owner_id = @ownerId
              AND checkpoint_owner_generation = @ownerGeneration;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("accountId", accountId);
            update.Parameters.AddWithValue(
                "characterId",
                characterId);
            update.Parameters.AddWithValue(
                "ownerId",
                owner.OwnerId);
            update.Parameters.AddWithValue(
                "ownerGeneration",
                owner.Generation);
            RequireExactlyOneRow(
                await update.ExecuteNonQueryAsync(cancellationToken),
                "owner release");
        }

        await transaction.CommitAsync(cancellationToken);
        return CharacterCheckpointReleaseStatus.Released;
    }
}
