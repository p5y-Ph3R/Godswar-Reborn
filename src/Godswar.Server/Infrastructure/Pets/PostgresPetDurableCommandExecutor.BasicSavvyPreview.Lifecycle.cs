using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    async Task<bool> IPetBasicSavvyPreviewLifecycleStore.IsCurrentAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        Guid previewOperationId,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty || previewOperationId == Guid.Empty)
        {
            return false;
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        (await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

        await using var command = CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.character_pet_basic_savvy_previews
                WHERE user_id = @characterId
                  AND preview_operation_id = @previewOperationId
                  AND connection_id = @connectionId
                  AND owner_id = @ownerId
                  AND owner_generation = @ownerGeneration
                  AND expires_at > clock_timestamp()
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue(
            "previewOperationId",
            previewOperationId);
        command.Parameters.AddWithValue("connectionId", connectionId);
        command.Parameters.AddWithValue("ownerId", ownership.OwnerId);
        command.Parameters.AddWithValue(
            "ownerGeneration",
            ownership.Generation);
        var current = await command.ExecuteScalarAsync(cancellationToken)
            is true;
        await transaction.CommitAsync(cancellationToken);
        return current;
    }

    async Task IPetBasicSavvyPreviewLifecycleStore.DiscardForSessionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty)
        {
            return;
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        (await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

        await using var command = CreateCommand(
            """
            DELETE FROM public.character_pet_basic_savvy_previews
            WHERE user_id = @characterId
              AND connection_id = @connectionId
              AND owner_id = @ownerId
              AND owner_generation = @ownerGeneration;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue("connectionId", connectionId);
        command.Parameters.AddWithValue("ownerId", ownership.OwnerId);
        command.Parameters.AddWithValue(
            "ownerGeneration",
            ownership.Generation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
