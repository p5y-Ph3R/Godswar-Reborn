using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed partial class
    PostgresMonsterDeathRewardCommandExecutor
{
    private async Task RecordDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await IncrementInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            conflict: false,
            cancellationToken);

    private async Task RecordRequestConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await IncrementInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            conflict: true,
            cancellationToken);

    private async Task IncrementInboxCounterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        bool conflict,
        CancellationToken cancellationToken)
    {
        var sql = conflict
            ? """
              UPDATE public.command_inbox
              SET request_conflict_count =
                      LEAST(request_conflict_count + 1, 1000000),
                  last_request_conflict_at = now()
              WHERE id = @inboxId;
              """
            : """
              UPDATE public.command_inbox
              SET duplicate_count =
                      LEAST(duplicate_count + 1, 1000000),
                  last_duplicate_at = now()
              WHERE id = @inboxId;
              """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The reward inbox counter update was not exact.");
        }
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            MonsterDeathRewardPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            MonsterDeathRewardPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            MonsterDeathRewardPersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
