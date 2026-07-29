using System.Data;
using System.Text;
using Godswar.Server.Application.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Messaging;

internal sealed partial class PostgresOutboxDispatcher
{
    private sealed record CandidateRow(
        long RowId,
        Guid EventId,
        string ConsumerKey,
        string AggregateType,
        string AggregateKey,
        long AggregateRevision,
        string EventType,
        int ContractVersion,
        string DatabaseOrderingPolicy,
        string Payload,
        int AttemptCount,
        int MaximumAttempts,
        long CurrentRevision,
        DateTimeOffset OccurredAtUtc,
        DateTimeOffset DatabaseNow);

    private readonly record struct ExpiredLeaseRow(
        long RowId,
        string ConsumerKey,
        int AttemptCount,
        int MaximumAttempts,
        string LeaseOwner,
        Guid LeaseToken);

    private async Task<ClaimedBatch> ClaimBatchAsync(
        bool performPassValidation,
        CancellationToken cancellationToken)
    {
        const int maximumClaimedWork = 1;
        var claims = new List<ClaimedEvent>(maximumClaimedWork);
        var outcomes = new List<DeferredOutcome>(maximumClaimedWork);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        if (performPassValidation)
        {
            // These operations scan registered pending streams. Run them
            // once per bounded polling pass, not again for every immediately
            // consumed lease in that pass.
            await EnsureConsumerPositionsAsync(
                connection,
                transaction,
                cancellationToken);
            await ValidateConsumerPoliciesAsync(
                connection,
                transaction,
                cancellationToken);
            await ValidateLeaseConsistencyAsync(
                connection,
                transaction,
                cancellationToken);
        }

        while (outcomes.Count + claims.Count < maximumClaimedWork)
        {
            var expired = await ReadExpiredLeaseAsync(
                connection,
                transaction,
                cancellationToken);
            if (expired is null)
            {
                break;
            }

            outcomes.Add(await RecoverExpiredLeaseAsync(
                connection,
                transaction,
                expired.Value,
                cancellationToken));
        }

        while (outcomes.Count + claims.Count < maximumClaimedWork)
        {
            var candidate = await ReadCandidateAsync(
                connection,
                transaction,
                cancellationToken);
            if (candidate is null)
            {
                break;
            }

            var consumer = _consumers[candidate.ConsumerKey];
            var policy = FromDatabaseOrderingPolicy(
                candidate.DatabaseOrderingPolicy);
            if (policy != consumer.Consumer.OrderingPolicy)
            {
                throw new InvalidDataException(
                    "An outbox row disagrees with its registered consumer policy.");
            }

            var decision = OutboxOrderingRules.Decide(
                policy,
                candidate.CurrentRevision,
                candidate.AggregateRevision);
            if (decision == OutboxOrderingDecision.Stale)
            {
                await DeliverStaleAsync(
                    connection,
                    transaction,
                    candidate.RowId,
                    cancellationToken);
                outcomes.Add(new DeferredOutcome(
                    candidate.ConsumerKey,
                    DeferredOutcomeKind.Stale));
                continue;
            }

            if (decision == OutboxOrderingDecision.Gap)
            {
                await DelayGapAsync(
                    connection,
                    transaction,
                    candidate.RowId,
                    cancellationToken);
                outcomes.Add(new DeferredOutcome(
                    candidate.ConsumerKey,
                    DeferredOutcomeKind.Gap));
                continue;
            }

            var effectiveMaximumAttempts = Math.Min(
                candidate.MaximumAttempts,
                _options.MaximumDeliveryAttempts);
            if (candidate.AttemptCount >= effectiveMaximumAttempts)
            {
                await PoisonUnleasedAsync(
                    connection,
                    transaction,
                    candidate.RowId,
                    "attempts_exhausted",
                    cancellationToken);
                outcomes.Add(new DeferredOutcome(
                    candidate.ConsumerKey,
                    DeferredOutcomeKind.AttemptsExhaustedPoison));
                continue;
            }

            claims.Add(await LeaseCandidateAsync(
                connection,
                transaction,
                candidate,
                effectiveMaximumAttempts,
                consumer.Consumer,
                cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return new ClaimedBatch(claims, outcomes);
    }

    private async Task EnsureConsumerPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH registered(consumer_key, ordering_policy) AS (
                SELECT *
                FROM unnest(
                    @consumer_keys::text[],
                    @ordering_policies::text[])
            ),
            streams AS (
                SELECT DISTINCT
                    e.consumer_key,
                    e.aggregate_type,
                    e.aggregate_key,
                    e.ordering_policy
                FROM public.outbox_events AS e
                INNER JOIN registered AS r
                    ON r.consumer_key = e.consumer_key
                WHERE e.delivered_at IS NULL
                  AND e.poisoned_at IS NULL
            )
            INSERT INTO public.outbox_consumer_positions (
                consumer_key,
                aggregate_type,
                aggregate_key,
                ordering_policy)
            SELECT
                consumer_key,
                aggregate_type,
                aggregate_key,
                ordering_policy
            FROM streams
            ON CONFLICT (
                consumer_key,
                aggregate_type,
                aggregate_key)
            DO NOTHING;
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddRegistryParameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ValidateConsumerPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH registered(consumer_key, ordering_policy) AS (
                SELECT *
                FROM unnest(
                    @consumer_keys::text[],
                    @ordering_policies::text[])
            )
            SELECT EXISTS (
                SELECT 1
                FROM public.outbox_events AS e
                INNER JOIN registered AS r
                    ON r.consumer_key = e.consumer_key
                INNER JOIN public.outbox_consumer_positions AS p
                    ON p.consumer_key = e.consumer_key
                   AND p.aggregate_type = e.aggregate_type
                   AND p.aggregate_key = e.aggregate_key
                WHERE e.delivered_at IS NULL
                  AND e.poisoned_at IS NULL
                  AND (
                      e.ordering_policy <> r.ordering_policy
                      OR p.ordering_policy <> r.ordering_policy)
            );
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        AddRegistryParameters(command);
        if (Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken)))
        {
            throw new InvalidDataException(
                "A pending outbox stream has a conflicting ordering policy.");
        }
    }

    private async Task ValidateLeaseConsistencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH registered(consumer_key) AS (
                SELECT unnest(@consumer_keys::text[])
            )
            SELECT EXISTS (
                SELECT 1
                FROM public.outbox_events AS e
                INNER JOIN registered AS r
                    ON r.consumer_key = e.consumer_key
                INNER JOIN public.outbox_consumer_positions AS p
                    ON p.consumer_key = e.consumer_key
                   AND p.aggregate_type = e.aggregate_type
                   AND p.aggregate_key = e.aggregate_key
                WHERE e.delivered_at IS NULL
                  AND e.poisoned_at IS NULL
                  AND (
                      (
                          e.lease_token IS NOT NULL
                          AND (
                              p.inflight_event_id IS DISTINCT FROM e.id
                              OR p.lease_owner IS DISTINCT FROM e.lease_owner
                              OR p.lease_token IS DISTINCT FROM e.lease_token
                              OR p.lease_expires_at
                                  IS DISTINCT FROM e.lease_expires_at)
                      )
                      OR (
                          p.inflight_event_id = e.id
                          AND (
                              e.lease_token IS NULL
                              OR p.lease_owner IS DISTINCT FROM e.lease_owner
                              OR p.lease_token IS DISTINCT FROM e.lease_token
                              OR p.lease_expires_at
                                  IS DISTINCT FROM e.lease_expires_at)
                      )
                  )
            );
            """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.Add(
            "consumer_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            _consumerKeys;
        if (Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken)))
        {
            throw new InvalidDataException(
                "Outbox event and consumer-position leases disagree.");
        }
    }

    private async Task<CandidateRow?> ReadCandidateAsync(
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
                e.event_id,
                e.consumer_key,
                e.aggregate_type,
                e.aggregate_key,
                e.aggregate_version,
                e.event_type,
                e.contract_version,
                e.ordering_policy,
                e.payload::text,
                e.attempt_count,
                e.max_attempts,
                p.current_version,
                e.created_at,
                clock_timestamp()
            FROM public.outbox_events AS e
            INNER JOIN registered AS r
                ON r.consumer_key = e.consumer_key
            INNER JOIN public.outbox_consumer_positions AS p
                ON p.consumer_key = e.consumer_key
               AND p.aggregate_type = e.aggregate_type
               AND p.aggregate_key = e.aggregate_key
            WHERE e.delivered_at IS NULL
              AND e.poisoned_at IS NULL
              AND e.lease_token IS NULL
              AND e.available_at <= clock_timestamp()
              AND p.inflight_event_id IS NULL
            ORDER BY e.available_at, e.id
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

        return new CandidateRow(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetInt16(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt16(10),
            reader.GetInt16(11),
            reader.GetInt64(12),
            new DateTimeOffset(reader.GetDateTime(13)),
            new DateTimeOffset(reader.GetDateTime(14)));
    }

    private async Task<ClaimedEvent> LeaseCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CandidateRow candidate,
        int effectiveMaximumAttempts,
        IOutboxEventConsumer consumer,
        CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid();
        var leaseExpiresAt =
            candidate.DatabaseNow.Add(_options.Lease);

        const string eventSql =
            """
            UPDATE public.outbox_events
            SET attempt_count = attempt_count + 1,
                lease_owner = @lease_owner,
                lease_token = @lease_token,
                lease_expires_at = @lease_expires_at,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND delivered_at IS NULL
              AND poisoned_at IS NULL
              AND lease_token IS NULL
              AND attempt_count = @attempt_count;
            """;
        await using (var command =
            CreateCommand(eventSql, connection, transaction))
        {
            command.Parameters.AddWithValue(
                "lease_owner",
                _leaseOwner);
            command.Parameters.AddWithValue(
                "lease_token",
                leaseToken);
            command.Parameters.AddWithValue(
                "lease_expires_at",
                leaseExpiresAt);
            command.Parameters.AddWithValue(
                "row_id",
                candidate.RowId);
            command.Parameters.AddWithValue(
                "attempt_count",
                checked((short)candidate.AttemptCount));
            RequireSingleRow(
                await command.ExecuteNonQueryAsync(cancellationToken),
                "The outbox event lease was lost while claiming.");
        }

        const string positionSql =
            """
            UPDATE public.outbox_consumer_positions
            SET inflight_event_id = @row_id,
                inflight_version = @aggregate_version,
                lease_owner = @lease_owner,
                lease_token = @lease_token,
                lease_expires_at = @lease_expires_at,
                updated_at = clock_timestamp()
            WHERE consumer_key = @consumer_key
              AND aggregate_type = @aggregate_type
              AND aggregate_key = @aggregate_key
              AND current_version = @current_version
              AND inflight_event_id IS NULL;
            """;
        await using (var command =
            CreateCommand(positionSql, connection, transaction))
        {
            command.Parameters.AddWithValue(
                "row_id",
                candidate.RowId);
            command.Parameters.AddWithValue(
                "aggregate_version",
                candidate.AggregateRevision);
            command.Parameters.AddWithValue(
                "lease_owner",
                _leaseOwner);
            command.Parameters.AddWithValue(
                "lease_token",
                leaseToken);
            command.Parameters.AddWithValue(
                "lease_expires_at",
                leaseExpiresAt);
            command.Parameters.AddWithValue(
                "consumer_key",
                candidate.ConsumerKey);
            command.Parameters.AddWithValue(
                "aggregate_type",
                candidate.AggregateType);
            command.Parameters.AddWithValue(
                "aggregate_key",
                candidate.AggregateKey);
            command.Parameters.AddWithValue(
                "current_version",
                candidate.CurrentRevision);
            RequireSingleRow(
                await command.ExecuteNonQueryAsync(cancellationToken),
                "The outbox position lease was lost while claiming.");
        }

        var message = new OutboxEventMessage(
            candidate.EventId,
            candidate.ConsumerKey,
            candidate.AggregateType,
            candidate.AggregateKey,
            candidate.AggregateRevision,
            candidate.EventType,
            candidate.ContractVersion,
            candidate.OccurredAtUtc,
            Encoding.UTF8.GetBytes(candidate.Payload));
        return new ClaimedEvent(
            candidate.RowId,
            leaseToken,
            checked(candidate.AttemptCount + 1),
            effectiveMaximumAttempts,
            message,
            consumer);
    }

    private void AddRegistryParameters(NpgsqlCommand command)
    {
        command.Parameters.Add(
            "consumer_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            _consumerKeys;
        command.Parameters.Add(
            "ordering_policies",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            _orderingPolicies;
    }

    private static void RequireSingleRow(
        int affectedRows,
        string message)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(message);
        }
    }
}
