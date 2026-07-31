using Godswar.Server.Application.Reconciliation;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed partial class PostgresReconciliationSnapshot
{
    public async Task<ReconciliationPage> ReadOutboxPageAsync(
        long afterOutboxKey,
        int limit,
        CancellationToken cancellationToken)
    {
        if (afterOutboxKey < 0 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        const string sql =
            """
            WITH registered(consumer_key, ordering_policy) AS (
                SELECT *
                FROM unnest(
                    @consumer_keys::text[],
                    @ordering_policies::text[])
            )
            SELECT
                event.id,
                event.poisoned_at IS NOT NULL,
                (
                    event.delivered_at IS NULL
                        AND event.poisoned_at IS NULL
                        AND event.lease_token IS NOT NULL
                        AND event.lease_expires_at <=
                            clock_timestamp()
                        AND registered.consumer_key IS NOT NULL
                        AND event.ordering_policy =
                            registered.ordering_policy
                        AND position.inflight_event_id = event.id
                        AND position.lease_owner = event.lease_owner
                        AND position.lease_token = event.lease_token
                        AND position.lease_expires_at =
                            event.lease_expires_at
                ) IS TRUE,
                (
                    event.delivered_at IS NULL
                        AND event.poisoned_at IS NULL
                        AND event.lease_token IS NULL
                        AND registered.ordering_policy = 'strict'
                        AND event.ordering_policy =
                            registered.ordering_policy
                        AND event.available_at <= clock_timestamp()
                        AND event.aggregate_version >
                            COALESCE(position.current_version, 0) + 1
                        AND NOT EXISTS (
                            SELECT 1
                            FROM public.outbox_events expected
                            WHERE expected.consumer_key =
                                    event.consumer_key
                              AND expected.aggregate_type =
                                    event.aggregate_type
                              AND expected.aggregate_key =
                                    event.aggregate_key
                              AND expected.aggregate_version =
                                    COALESCE(
                                        position.current_version,
                                        0
                                    ) + 1
                              AND expected.delivered_at IS NULL
                              AND expected.poisoned_at IS NULL
                        )
                ) IS TRUE,
                (
                    (
                        event.lease_token IS NOT NULL
                        AND (
                            position.inflight_event_id
                                IS DISTINCT FROM event.id
                            OR position.lease_owner
                                IS DISTINCT FROM event.lease_owner
                            OR position.lease_token
                                IS DISTINCT FROM event.lease_token
                            OR position.lease_expires_at
                                IS DISTINCT FROM event.lease_expires_at
                        )
                    )
                    OR (
                        event.lease_token IS NULL
                        AND position.inflight_event_id = event.id
                    )
                ) IS TRUE,
                registered.consumer_key IS NULL,
                (
                    registered.consumer_key IS NOT NULL
                        AND event.ordering_policy <>
                            registered.ordering_policy
                ) IS TRUE,
                (
                    event.delivered_at IS NOT NULL
                        AND event.poisoned_at IS NULL
                        AND (
                            position.consumer_key IS NULL
                            OR position.current_version <
                                event.aggregate_version
                        )
                ) IS TRUE
            FROM public.outbox_events event
            LEFT JOIN registered
                ON registered.consumer_key = event.consumer_key
            LEFT JOIN public.outbox_consumer_positions position
                ON position.consumer_key = event.consumer_key
               AND position.aggregate_type = event.aggregate_type
               AND position.aggregate_key = event.aggregate_key
            WHERE event.id > @after_key
            ORDER BY event.id
            LIMIT @limit;
            """;
        var counts = new Dictionary<ReconciliationCategory, long>();
        var rows = 0;
        var nextKey = afterOutboxKey;
        await using (var command = CreateCommand(sql))
        {
            command.Parameters.AddWithValue("after_key", afterOutboxKey);
            command.Parameters.AddWithValue("limit", limit);
            command.Parameters.Add(
                "consumer_keys",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                _consumerKeys;
            command.Parameters.Add(
                "ordering_policies",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                _orderingPolicies;
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
                nextKey = reader.GetInt64(0);
                Add(
                    counts,
                    ReconciliationCategory.OutboxPoisoned,
                    reader.GetBoolean(1));
                Add(
                    counts,
                    ReconciliationCategory.OutboxExpiredLease,
                    reader.GetBoolean(2));
                Add(
                    counts,
                    ReconciliationCategory.OutboxSequenceGap,
                    reader.GetBoolean(3));
                Add(
                    counts,
                    ReconciliationCategory.OutboxLeaseMismatch,
                    reader.GetBoolean(4));
                Add(
                    counts,
                    ReconciliationCategory.UnknownOutboxConsumer,
                    reader.GetBoolean(5));
                Add(
                    counts,
                    ReconciliationCategory.OutboxPolicyMismatch,
                    reader.GetBoolean(6));
                Add(
                    counts,
                    ReconciliationCategory
                        .OutboxConsumerPositionMismatch,
                    reader.GetBoolean(7));
            }
        }

        var reachedEnd =
            rows < limit ||
            !await HasOutboxAfterAsync(nextKey, cancellationToken);
        return new ReconciliationPage(
            nextKey,
            rows,
            reachedEnd,
            ToCounts(counts));
    }

    private async Task<bool> HasOutboxAfterAsync(
        long key,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.outbox_events
                WHERE id > @key
            );
            """;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("key", key);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken));
    }
}
