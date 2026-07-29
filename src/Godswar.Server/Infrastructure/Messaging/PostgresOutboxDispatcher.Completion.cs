using System.Data;
using Godswar.Server.Application.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.Messaging;

internal sealed partial class PostgresOutboxDispatcher
{
    private readonly record struct CompletionRow(
        long AggregateRevision,
        long CurrentRevision,
        string DatabaseOrderingPolicy,
        int AttemptCount,
        int MaximumAttempts);

    private async Task<CompletionDisposition> CompleteSuccessAsync(
        ClaimedEvent claim,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        var row = await LockCompletionAsync(
            connection,
            transaction,
            claim,
            cancellationToken);
        if (row is null)
        {
            return CompletionDisposition.LeaseLost;
        }

        var policy = FromDatabaseOrderingPolicy(
            row.Value.DatabaseOrderingPolicy);
        if (OutboxOrderingRules.Decide(
                policy,
                row.Value.CurrentRevision,
                row.Value.AggregateRevision) !=
            OutboxOrderingDecision.Deliver)
        {
            throw new InvalidDataException(
                "A leased outbox event is no longer deliverable.");
        }

        await AdvanceAndClearPositionAsync(
            connection,
            transaction,
            claim,
            row.Value.AggregateRevision,
            cancellationToken);
        await DeliverLeasedEventAsync(
            connection,
            transaction,
            claim,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CompletionDisposition.Delivered;
    }

    private async Task<CompletionDisposition> CompleteFailureAsync(
        ClaimedEvent claim,
        string failureReason,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        var row = await LockCompletionAsync(
            connection,
            transaction,
            claim,
            cancellationToken);
        if (row is null)
        {
            return CompletionDisposition.LeaseLost;
        }

        await ClearClaimedPositionAsync(
            connection,
            transaction,
            claim,
            cancellationToken);
        var maximumAttempts = Math.Min(
            row.Value.MaximumAttempts,
            _options.MaximumDeliveryAttempts);
        CompletionDisposition result;
        if (row.Value.AttemptCount >= maximumAttempts)
        {
            await PoisonClaimedEventAsync(
                connection,
                transaction,
                claim,
                $"{failureReason}_max_attempts",
                cancellationToken);
            result = CompletionDisposition.Poisoned;
        }
        else
        {
            await RetryClaimedEventAsync(
                connection,
                transaction,
                claim,
                _options.RetryDelay(row.Value.AttemptCount),
                cancellationToken);
            result = CompletionDisposition.RetryScheduled;
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CompletionRow?> LockCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedEvent claim,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT
                e.aggregate_version,
                p.current_version,
                e.ordering_policy,
                e.attempt_count,
                e.max_attempts
            FROM public.outbox_events AS e
            INNER JOIN public.outbox_consumer_positions AS p
                ON p.inflight_event_id = e.id
               AND p.consumer_key = e.consumer_key
               AND p.aggregate_type = e.aggregate_type
               AND p.aggregate_key = e.aggregate_key
               AND p.inflight_version = e.aggregate_version
               AND p.ordering_policy = e.ordering_policy
            WHERE e.id = @row_id
              AND e.delivered_at IS NULL
              AND e.poisoned_at IS NULL
              AND e.lease_owner = @lease_owner
              AND e.lease_token = @lease_token
              AND p.lease_owner = @lease_owner
              AND p.lease_token = @lease_token
              AND p.lease_expires_at = e.lease_expires_at
            FOR UPDATE OF e, p;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddClaimLeaseParameters(command, claim);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompletionRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt16(3),
            reader.GetInt16(4));
    }

    private async Task AdvanceAndClearPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedEvent claim,
        long aggregateRevision,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_consumer_positions
            SET current_version = @aggregate_version,
                inflight_event_id = NULL,
                inflight_version = NULL,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE inflight_event_id = @row_id
              AND lease_owner = @lease_owner
              AND lease_token = @lease_token;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddClaimLeaseParameters(command, claim);
        command.Parameters.AddWithValue(
            "aggregate_version",
            aggregateRevision);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The outbox position lease was lost before checkpointing.");
    }

    private async Task ClearClaimedPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedEvent claim,
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
              AND lease_token = @lease_token;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddClaimLeaseParameters(command, claim);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The outbox position lease was lost before retrying.");
    }

    private async Task DeliverLeasedEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedEvent claim,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_events
            SET lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                delivered_at = clock_timestamp(),
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND lease_owner = @lease_owner
              AND lease_token = @lease_token
              AND delivered_at IS NULL
              AND poisoned_at IS NULL;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddClaimLeaseParameters(command, claim);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The outbox event lease was lost before delivery.");
    }

    private async Task RetryClaimedEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedEvent claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE public.outbox_events
            SET available_at = clock_timestamp() + @retry_delay,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND lease_owner = @lease_owner
              AND lease_token = @lease_token
              AND delivered_at IS NULL
              AND poisoned_at IS NULL;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddClaimLeaseParameters(command, claim);
        command.Parameters.AddWithValue(
            "retry_delay",
            retryDelay);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The outbox event lease was lost before retrying.");
    }

    private async Task PoisonClaimedEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedEvent claim,
        string poisonReason,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
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
        AddClaimLeaseParameters(command, claim);
        command.Parameters.AddWithValue(
            "poison_reason",
            poisonReason);
        RequireSingleRow(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The outbox event lease was lost before quarantining.");
    }

    private void AddClaimLeaseParameters(
        NpgsqlCommand command,
        ClaimedEvent claim)
    {
        command.Parameters.AddWithValue("row_id", claim.RowId);
        command.Parameters.AddWithValue(
            "lease_owner",
            _leaseOwner);
        command.Parameters.AddWithValue(
            "lease_token",
            claim.LeaseToken);
    }

    private async Task RefreshBacklogAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT
                count(*),
                COALESCE(
                    EXTRACT(
                        EPOCH FROM (
                            clock_timestamp() - min(created_at))),
                    0)::double precision
            FROM public.outbox_events
            WHERE delivered_at IS NULL
              AND poisoned_at IS NULL;
            """;
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(sql, connection);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The outbox backlog query returned no result.");
        }

        PostgresCommandMetrics.UpdateBacklog(
            reader.GetInt64(0),
            TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(1))));
    }
}
