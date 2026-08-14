using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    public async Task<bool> IsCurrentAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        Guid previewOperationId,
        CancellationToken cancellationToken = default)
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
                FROM public.character_pet_growth_previews preview
                JOIN public.character_pets pet
                  ON pet.id = preview.pet_id
                 AND pet.user_id = preview.user_id
                WHERE preview.user_id = @characterId
                  AND preview.preview_operation_id = @previewOperationId
                  AND preview.connection_id = @connectionId
                  AND preview.owner_id = @ownerId
                  AND preview.owner_generation = @ownerGeneration
                  AND preview.expires_at > clock_timestamp()
                  AND pet.level = preview.expected_pet_level
                  AND pet.revision = preview.expected_pet_revision
                  AND ARRAY(
                      SELECT stat.revision
                      FROM public.character_pet_stat_values stat
                      WHERE stat.pet_id = preview.pet_id
                      ORDER BY stat.stat_code
                  ) = preview.expected_stat_revisions
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

    public async Task DiscardForSessionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        CancellationToken cancellationToken = default)
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
            DELETE FROM public.character_pet_growth_previews
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
