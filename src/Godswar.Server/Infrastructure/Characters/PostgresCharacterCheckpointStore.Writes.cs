using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterCheckpointStore
{
    public async Task<CharacterCheckpointWriteResult>
        WritePositionAsync(
            CharacterPositionCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        PositionRow? stored = null;
        await using (var select = new NpgsqlCommand(
            """
            SELECT
                checkpoint_owner_id,
                checkpoint_owner_generation,
                position_revision,
                "Map",
                "Pos_X",
                "Pos_Z"
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue(
                "accountId",
                checkpoint.AccountId);
            select.Parameters.AddWithValue(
                "characterId",
                checkpoint.CharacterId);
            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                stored = new PositionRow(
                    reader.IsDBNull(0)
                        ? null
                        : reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt16(3),
                    reader.GetFloat(4),
                    reader.GetFloat(5));
            }
        }

        if (stored is null)
        {
            return await CompleteWithoutWriteAsync(
                transaction,
                CharacterCheckpointWriteStatus.CharacterNotFound,
                storedRevision: null,
                cancellationToken);
        }

        if (!Owns(stored.OwnerId, stored.OwnerGeneration, checkpoint.Owner))
        {
            return await CompleteWithoutWriteAsync(
                transaction,
                CharacterCheckpointWriteStatus.OwnershipLost,
                stored.Revision,
                cancellationToken);
        }

        var precondition = ClassifyRevision(
            stored.Revision,
            checkpoint.Revision,
            stored.MapId == checkpoint.CurrentMap &&
            stored.PositionX == checkpoint.PositionX &&
            stored.PositionZ == checkpoint.PositionZ);
        if (precondition.HasValue)
        {
            return await CompleteWithoutWriteAsync(
                transaction,
                precondition.Value,
                stored.Revision,
                cancellationToken);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET "Map" = @currentMap,
                "Pos_X" = @positionX,
                "Pos_Z" = @positionZ,
                position_revision = @revision
            WHERE id = @characterId
              AND account_id = @accountId
              AND checkpoint_owner_id = @ownerId
              AND checkpoint_owner_generation = @ownerGeneration
              AND position_revision = @storedRevision;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(
                "accountId",
                checkpoint.AccountId);
            update.Parameters.AddWithValue(
                "characterId",
                checkpoint.CharacterId);
            update.Parameters.AddWithValue(
                "ownerId",
                checkpoint.Owner.OwnerId);
            update.Parameters.AddWithValue(
                "ownerGeneration",
                checkpoint.Owner.Generation);
            update.Parameters.AddWithValue(
                "currentMap",
                checked((short)checkpoint.CurrentMap));
            update.Parameters.AddWithValue(
                "positionX",
                checkpoint.PositionX);
            update.Parameters.AddWithValue(
                "positionZ",
                checkpoint.PositionZ);
            update.Parameters.AddWithValue(
                "revision",
                checkpoint.Revision);
            update.Parameters.AddWithValue(
                "storedRevision",
                stored.Revision);
            RequireExactlyOneRow(
                await update.ExecuteNonQueryAsync(cancellationToken),
                "position write");
        }

        await transaction.CommitAsync(cancellationToken);
        return new CharacterCheckpointWriteResult(
            CharacterCheckpointWriteStatus.Applied,
            checkpoint.Revision);
    }

    public async Task<CharacterCheckpointWriteResult> WriteVitalsAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        VitalsRow? stored = null;
        await using (var select = new NpgsqlCommand(
            """
            SELECT
                checkpoint_owner_id,
                checkpoint_owner_generation,
                vitals_revision,
                "curHP",
                "curMP"
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue(
                "accountId",
                checkpoint.AccountId);
            select.Parameters.AddWithValue(
                "characterId",
                checkpoint.CharacterId);
            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                stored = new VitalsRow(
                    reader.IsDBNull(0)
                        ? null
                        : reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4));
            }
        }

        if (stored is null)
        {
            return await CompleteWithoutWriteAsync(
                transaction,
                CharacterCheckpointWriteStatus.CharacterNotFound,
                storedRevision: null,
                cancellationToken);
        }

        if (!Owns(stored.OwnerId, stored.OwnerGeneration, checkpoint.Owner))
        {
            return await CompleteWithoutWriteAsync(
                transaction,
                CharacterCheckpointWriteStatus.OwnershipLost,
                stored.Revision,
                cancellationToken);
        }

        var precondition = ClassifyRevision(
            stored.Revision,
            checkpoint.Revision,
            stored.CurrentHp == checkpoint.CurrentHp &&
            stored.CurrentMp == checkpoint.CurrentMp);
        if (precondition.HasValue)
        {
            return await CompleteWithoutWriteAsync(
                transaction,
                precondition.Value,
                stored.Revision,
                cancellationToken);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET "curHP" = @currentHp,
                "curMP" = @currentMp,
                vitals_revision = @revision
            WHERE id = @characterId
              AND account_id = @accountId
              AND checkpoint_owner_id = @ownerId
              AND checkpoint_owner_generation = @ownerGeneration
              AND vitals_revision = @storedRevision;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(
                "accountId",
                checkpoint.AccountId);
            update.Parameters.AddWithValue(
                "characterId",
                checkpoint.CharacterId);
            update.Parameters.AddWithValue(
                "ownerId",
                checkpoint.Owner.OwnerId);
            update.Parameters.AddWithValue(
                "ownerGeneration",
                checkpoint.Owner.Generation);
            update.Parameters.AddWithValue(
                "currentHp",
                checkpoint.CurrentHp);
            update.Parameters.AddWithValue(
                "currentMp",
                checkpoint.CurrentMp);
            update.Parameters.AddWithValue(
                "revision",
                checkpoint.Revision);
            update.Parameters.AddWithValue(
                "storedRevision",
                stored.Revision);
            RequireExactlyOneRow(
                await update.ExecuteNonQueryAsync(cancellationToken),
                "vitals write");
        }

        await transaction.CommitAsync(cancellationToken);
        return new CharacterCheckpointWriteResult(
            CharacterCheckpointWriteStatus.Applied,
            checkpoint.Revision);
    }

    private static CharacterCheckpointWriteStatus?
        ClassifyRevision(
            long storedRevision,
            long requestedRevision,
            bool payloadMatches)
    {
        if (storedRevision > requestedRevision)
        {
            return CharacterCheckpointWriteStatus.Superseded;
        }

        if (storedRevision == requestedRevision)
        {
            return payloadMatches
                ? CharacterCheckpointWriteStatus.AlreadyApplied
                : CharacterCheckpointWriteStatus.RevisionConflict;
        }

        return null;
    }

    private static bool Owns(
        Guid? storedOwnerId,
        long storedGeneration,
        CharacterCheckpointOwner owner) =>
        storedOwnerId == owner.OwnerId &&
        storedGeneration == owner.Generation;

    private static async Task<CharacterCheckpointWriteResult>
        CompleteWithoutWriteAsync(
            NpgsqlTransaction transaction,
            CharacterCheckpointWriteStatus status,
            long? storedRevision,
            CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return new CharacterCheckpointWriteResult(
            status,
            storedRevision);
    }

    private sealed record PositionRow(
        Guid? OwnerId,
        long OwnerGeneration,
        long Revision,
        short MapId,
        float PositionX,
        float PositionZ);

    private sealed record VitalsRow(
        Guid? OwnerId,
        long OwnerGeneration,
        long Revision,
        int CurrentHp,
        int CurrentMp);
}
