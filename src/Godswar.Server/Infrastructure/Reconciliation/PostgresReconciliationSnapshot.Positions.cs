using Godswar.Server.Application.Reconciliation;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed partial class PostgresReconciliationSnapshot
{
    public async Task<ReconciliationOutboxPositionPage>
        ReadOutboxPositionPageAsync(
            ReconciliationOutboxPositionCursor after,
            int limit,
            CancellationToken cancellationToken)
    {
        ValidatePositionCursor(after, limit);
        const string sql =
            """
            WITH registered(consumer_key, ordering_policy) AS (
                SELECT *
                FROM unnest(
                    @consumer_keys::text[],
                    @ordering_policies::text[])
            )
            SELECT
                position.consumer_key,
                position.aggregate_type,
                position.aggregate_key,
                registered.consumer_key IS NULL,
                registered.consumer_key IS NOT NULL
                    AND position.ordering_policy <>
                        registered.ordering_policy,
                NOT EXISTS (
                    SELECT 1
                    FROM public.outbox_events stream_event
                    WHERE stream_event.consumer_key =
                            position.consumer_key
                      AND stream_event.aggregate_type =
                            position.aggregate_type
                      AND stream_event.aggregate_key =
                            position.aggregate_key
                )
                OR (
                    position.current_version > 0
                    AND NOT EXISTS (
                        SELECT 1
                        FROM public.outbox_events checkpoint_event
                        WHERE checkpoint_event.consumer_key =
                                position.consumer_key
                          AND checkpoint_event.aggregate_type =
                                position.aggregate_type
                          AND checkpoint_event.aggregate_key =
                                position.aggregate_key
                          AND checkpoint_event.ordering_policy =
                                position.ordering_policy
                          AND checkpoint_event.aggregate_version =
                                position.current_version
                          AND checkpoint_event.delivered_at IS NOT NULL
                          AND checkpoint_event.poisoned_at IS NULL
                    )
                )
                OR EXISTS (
                    SELECT 1
                    FROM public.outbox_events advanced_event
                    WHERE advanced_event.consumer_key =
                            position.consumer_key
                      AND advanced_event.aggregate_type =
                            position.aggregate_type
                      AND advanced_event.aggregate_key =
                            position.aggregate_key
                      AND advanced_event.ordering_policy =
                            position.ordering_policy
                      AND advanced_event.aggregate_version >
                            position.current_version
                      AND advanced_event.delivered_at IS NOT NULL
                      AND advanced_event.poisoned_at IS NULL
                )
                OR (
                    position.inflight_event_id IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM public.outbox_events inflight_event
                        WHERE inflight_event.id =
                                position.inflight_event_id
                          AND inflight_event.consumer_key =
                                position.consumer_key
                          AND inflight_event.aggregate_type =
                                position.aggregate_type
                          AND inflight_event.aggregate_key =
                                position.aggregate_key
                          AND inflight_event.ordering_policy =
                                position.ordering_policy
                          AND inflight_event.aggregate_version =
                                position.inflight_version
                          AND inflight_event.lease_owner =
                                position.lease_owner
                          AND inflight_event.lease_token =
                                position.lease_token
                          AND inflight_event.lease_expires_at =
                                position.lease_expires_at
                          AND inflight_event.delivered_at IS NULL
                          AND inflight_event.poisoned_at IS NULL
                    )
                )
            FROM public.outbox_consumer_positions position
            LEFT JOIN registered
                ON registered.consumer_key = position.consumer_key
            WHERE (
                position.consumer_key,
                position.aggregate_type,
                position.aggregate_key
            ) > (
                @after_consumer_key::varchar(64),
                @after_aggregate_type::varchar(32),
                @after_aggregate_key::varchar(128)
            )
            ORDER BY
                position.consumer_key,
                position.aggregate_type,
                position.aggregate_key
            LIMIT @limit;
            """;
        var counts = new Dictionary<ReconciliationCategory, long>();
        var rows = 0;
        var next = after;
        await using (var command = CreateCommand(sql))
        {
            AddPositionParameters(command, after, limit);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
                next = new ReconciliationOutboxPositionCursor(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2));
                Add(
                    counts,
                    ReconciliationCategory.UnknownOutboxConsumer,
                    reader.GetBoolean(3));
                Add(
                    counts,
                    ReconciliationCategory.OutboxPolicyMismatch,
                    reader.GetBoolean(4));
                Add(
                    counts,
                    ReconciliationCategory
                        .OutboxConsumerPositionMismatch,
                    reader.GetBoolean(5));
            }
        }

        var reachedEnd =
            rows < limit ||
            !await HasPositionAfterAsync(next, cancellationToken);
        return new ReconciliationOutboxPositionPage(
            next,
            rows,
            reachedEnd,
            ToCounts(counts));
    }

    private void AddPositionParameters(
        Npgsql.NpgsqlCommand command,
        ReconciliationOutboxPositionCursor after,
        int limit)
    {
        command.Parameters.AddWithValue(
            "after_consumer_key",
            after.ConsumerKey);
        command.Parameters.AddWithValue(
            "after_aggregate_type",
            after.AggregateType);
        command.Parameters.AddWithValue(
            "after_aggregate_key",
            after.AggregateKey);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.Add(
            "consumer_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            _consumerKeys;
        command.Parameters.Add(
            "ordering_policies",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            _orderingPolicies;
    }

    private async Task<bool> HasPositionAfterAsync(
        ReconciliationOutboxPositionCursor cursor,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.outbox_consumer_positions position
                WHERE (
                    position.consumer_key,
                    position.aggregate_type,
                    position.aggregate_key
                ) > (
                    @consumer_key::varchar(64),
                    @aggregate_type::varchar(32),
                    @aggregate_key::varchar(128)
                )
            );
            """;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue(
            "consumer_key",
            cursor.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregate_type",
            cursor.AggregateType);
        command.Parameters.AddWithValue(
            "aggregate_key",
            cursor.AggregateKey);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void ValidatePositionCursor(
        ReconciliationOutboxPositionCursor after,
        int limit)
    {
        if (limit is < 1 or > 500 ||
            after.ConsumerKey is null ||
            after.AggregateType is null ||
            after.AggregateKey is null ||
            after.ConsumerKey.Length > 64 ||
            after.AggregateType.Length > 32 ||
            after.AggregateKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }
}
