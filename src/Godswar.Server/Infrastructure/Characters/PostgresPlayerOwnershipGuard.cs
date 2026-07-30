using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

/// <summary>
/// Locks and validates the session-wide ownership fence stored on the
/// authoritative character row. Callers must keep the supplied transaction
/// open for the complete valuable mutation.
/// </summary>
internal sealed class PostgresPlayerOwnershipGuard
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPlayerOwnershipGuard(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<PlayerOwnershipValidationResult> LockCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateSubject(subject);

        var result = ownership.IsValid
            ? await ReadCurrentAsync(
                connection,
                transaction,
                subject,
                ownership,
                lockRow: true,
                cancellationToken)
            : OwnershipLost(storedGeneration: null);
        PostgresPlayerOwnershipMetrics.Record(
            PlayerOwnershipValidationStage.Transaction,
            result.Status);
        return result;
    }

    public async Task<PlayerOwnershipValidationResult> RequireCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default) =>
        (await LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

    public async Task<PlayerOwnershipValidationResult> ValidateCurrentAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        ValidateSubject(subject);
        PlayerOwnershipValidationResult result;
        if (!ownership.IsValid)
        {
            result = OwnershipLost(storedGeneration: null);
        }
        else
        {
            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);
            result = await ReadCurrentAsync(
                connection,
                transaction: null,
                subject,
                ownership,
                lockRow: false,
                cancellationToken);
        }

        PostgresPlayerOwnershipMetrics.Record(
            PlayerOwnershipValidationStage.PostCommit,
            result.Status);
        return result;
    }

    private static async Task<PlayerOwnershipValidationResult>
        ReadCurrentAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            bool lockRow,
            CancellationToken cancellationToken)
    {
        var sql = lockRow
            ? """
              SELECT
                  checkpoint_owner_id,
                  checkpoint_owner_generation,
                  lifecycle_state
              FROM public.character_base
              WHERE id = @characterId
                AND account_id = @accountId
              FOR UPDATE;
              """
            : """
              SELECT
                  checkpoint_owner_id,
                  checkpoint_owner_generation,
                  lifecycle_state
              FROM public.character_base
              WHERE id = @characterId
                AND account_id = @accountId;
              """;
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PlayerOwnershipValidationResult(
                PlayerOwnershipValidationStatus.CharacterNotFound,
                StoredGeneration: null);
        }

        var storedOwner = reader.IsDBNull(0)
            ? (Guid?)null
            : reader.GetGuid(0);
        var storedGeneration = reader.GetInt64(1);
        var lifecycleState = reader.GetString(2);
        return lifecycleState == "active" &&
            storedOwner == ownership.OwnerId &&
            storedGeneration == ownership.Generation
                ? new PlayerOwnershipValidationResult(
                    PlayerOwnershipValidationStatus.Current,
                    storedGeneration)
                : OwnershipLost(storedGeneration);
    }

    private static PlayerOwnershipValidationResult OwnershipLost(
        long? storedGeneration) =>
        new(
            PlayerOwnershipValidationStatus.OwnershipLost,
            storedGeneration);

    private static void ValidateSubject(CommandSubject subject)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            subject.AccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            subject.CharacterId);
    }
}
