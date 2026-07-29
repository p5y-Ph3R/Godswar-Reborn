using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Messaging;

internal sealed partial class PostgresOutboxDispatcher
{
    private async Task<ExpiredLeaseRow?> ReadExpiredLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH registered(consumer_key) AS (
                SELECT unnest(@consumer_keys::text[])
            )
            SELECT
                e.id,
                e.consumer_key,
                e.attempt_count,
                e.max_attempts,
                e.lease_owner,
                e.lease_token
            FROM public.outbox_events AS e
            INNER JOIN registered AS r
                ON r.consumer_key = e.consumer_key
            INNER JOIN public.outbox_consumer_positions AS p
                ON p.inflight_event_id = e.id
               AND p.consumer_key = e.consumer_key
               AND p.aggregate_type = e.aggregate_type
               AND p.aggregate_key = e.aggregate_key
               AND p.inflight_version = e.aggregate_version
               AND p.ordering_policy = e.ordering_policy
               AND p.lease_owner = e.lease_owner
               AND p.lease_token = e.lease_token
               AND p.lease_expires_at = e.lease_expires_at
            WHERE e.delivered_at IS NULL
              AND e.poisoned_at IS NULL
              AND e.lease_token IS NOT NULL
              AND e.lease_expires_at <= clock_timestamp()
            ORDER BY e.lease_expires_at, e.id
            LIMIT 1
            FOR UPDATE OF e, p SKIP LOCKED;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.Add(
            "consumer_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            _consumerKeys;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExpiredLeaseRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetString(4),
            reader.GetGuid(5));
    }

    private async Task<DeferredOutcome> RecoverExpiredLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExpiredLeaseRow expired,
        CancellationToken cancellationToken)
    {
        await ClearPositionLeaseAsync(
            connection,
            transaction,
            expired.RowId,
            expired.LeaseOwner,
            expired.LeaseToken,
            cancellationToken);

        var maximumAttempts = Math.Min(
            expired.MaximumAttempts,
            _options.MaximumDeliveryAttempts);
        if (expired.AttemptCount >= maximumAttempts)
        {
            await FinishExpiredEventAsync(
                connection,
                transaction,
                expired,
                retryDelay: null,
                poisonReason: "lease_expired_max_attempts",
                cancellationToken);
            return new DeferredOutcome(
                expired.ConsumerKey,
                DeferredOutcomeKind.LeaseExpiredPoison);
        }

        await FinishExpiredEventAsync(
            connection,
            transaction,
            expired,
            _options.RetryDelay(expired.AttemptCount),
            poisonReason: null,
            cancellationToken);
        return new DeferredOutcome(
            expired.ConsumerKey,
            DeferredOutcomeKind.LeaseExpiredRetry);
    }

    private async Task ClearPositionLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long rowId,
        string leaseOwner,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_consumer_positions
            SET inflight_event_id = NULL,
                inflight_version = NULL,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE inflight_event_id = @row_id
              AND lease_owner = @lease_owner
              AND lease_token = @lease_token
              AND lease_expires_at <= clock_timestamp();
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("row_id", rowId);
        command.Parameters.AddWithValue("lease_owner", leaseOwner);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The expired outbox position lease changed during recovery.");
    }

    private async Task FinishExpiredEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExpiredLeaseRow expired,
        TimeSpan? retryDelay,
        string? poisonReason,
        CancellationToken cancellationToken)
    {
        var sql = retryDelay.HasValue
            ? """
              UPDATE public.outbox_events
              SET available_at =
                      clock_timestamp() + @retry_delay,
                  lease_owner = NULL,
                  lease_token = NULL,
                  lease_expires_at = NULL,
                  state_changed_at = clock_timestamp()
              WHERE id = @row_id
                AND lease_owner = @lease_owner
                AND lease_token = @lease_token
                AND delivered_at IS NULL
                AND poisoned_at IS NULL;
              """
            : """
              UPDATE public.outbox_events
              SET lease_owner = NULL,
                  lease_token = NULL,
                  lease_expires_at = NULL,
                  poisoned_at = clock_timestamp(),
                  poison_reason = @poison_reason,
                  state_changed_at = clock_timestamp()
              WHERE id = @row_id
                AND lease_owner = @lease_owner
                AND lease_token = @lease_token
                AND delivered_at IS NULL
                AND poisoned_at IS NULL;
              """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("row_id", expired.RowId);
        command.Parameters.AddWithValue(
            "lease_owner",
            expired.LeaseOwner);
        command.Parameters.AddWithValue(
            "lease_token",
            expired.LeaseToken);
        if (retryDelay.HasValue)
        {
            command.Parameters.AddWithValue(
                "retry_delay",
                retryDelay.Value);
        }
        else
        {
            command.Parameters.AddWithValue(
                "poison_reason",
                poisonReason!);
        }

        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The expired outbox event lease changed during recovery.");
    }

    private async Task DeliverStaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long rowId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_events
            SET delivered_at = clock_timestamp(),
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND delivered_at IS NULL
              AND poisoned_at IS NULL
              AND lease_token IS NULL;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("row_id", rowId);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The stale outbox event changed while completing.");
    }

    private async Task DelayGapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long rowId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_events
            SET available_at =
                    clock_timestamp() + @gap_retry_delay,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND delivered_at IS NULL
              AND poisoned_at IS NULL
              AND lease_token IS NULL;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "gap_retry_delay",
            _options.GapRetryDelay);
        command.Parameters.AddWithValue("row_id", rowId);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The strict-order gap event changed while delaying.");
    }

    private async Task PoisonUnleasedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long rowId,
        string poisonReason,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_events
            SET poisoned_at = clock_timestamp(),
                poison_reason = @poison_reason,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND delivered_at IS NULL
              AND poisoned_at IS NULL
              AND lease_token IS NULL;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "poison_reason",
            poisonReason);
        command.Parameters.AddWithValue("row_id", rowId);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The exhausted outbox event changed while quarantining.");
    }
}
