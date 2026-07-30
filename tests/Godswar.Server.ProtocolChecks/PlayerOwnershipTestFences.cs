using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerOwnershipTestFences
{
    public static PlayerOwnershipFence ForCharacter(int characterId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        return new PlayerOwnershipFence(
            new Guid(
                characterId,
                unchecked((short)0xB150),
                unchecked((short)0x5E55),
                0xB1,
                0x50,
                0xF0,
                0x0D,
                0xCA,
                0xFE,
                0x11,
                0xCE),
            Generation: 1);
    }

    public static CommandEnvelope<TCommand> Bind<TCommand>(
        CommandEnvelope<TCommand> envelope) =>
        CommandEnvelopeContract.BindOwnership(
            envelope,
            ForCharacter(envelope.Subject.CharacterId));

    public static async Task<PlayerOwnershipFence> InstallAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownership = await InstallAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ownership;
    }

    public static async Task<PlayerOwnershipFence> InstallAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        var ownership = ForCharacter(characterId);
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_id = @ownerId,
                checkpoint_owner_generation = @generation
            WHERE id = @characterId
              AND account_id = @accountId
              AND lifecycle_state = 'active';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "ownerId",
            ownership.OwnerId);
        command.Parameters.AddWithValue(
            "generation",
            ownership.Generation);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken),
            "install current player ownership fence");
        return ownership;
    }
}
