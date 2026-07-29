using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static async Task<EventState> ReadEventAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                attempt_count,
                lease_token IS NOT NULL,
                delivered_at,
                poisoned_at,
                poison_reason
            FROM public.outbox_events
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "The expected outbox event does not exist.");
        }

        return new EventState(
            reader.GetInt16(0),
            reader.GetBoolean(1),
            ReadOptionalTimestamp(reader, 2),
            ReadOptionalTimestamp(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task AssertPositionAsync(
        NpgsqlDataSource dataSource,
        string consumerKey,
        string aggregateKey,
        long expectedRevision,
        bool expectInflight)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                current_version,
                inflight_event_id IS NOT NULL,
                lease_token IS NOT NULL
            FROM public.outbox_consumer_positions
            WHERE consumer_key = @consumer_key
              AND aggregate_type = 'protocol_check'
              AND aggregate_key = @aggregate_key;
            """);
        command.Parameters.AddWithValue("consumer_key", consumerKey);
        command.Parameters.AddWithValue("aggregate_key", aggregateKey);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "The expected outbox consumer position does not exist.");
        }

        Check.Equal(
            expectedRevision,
            reader.GetInt64(0),
            "outbox position stores the expected aggregate revision");
        Check.Equal(
            expectInflight,
            reader.GetBoolean(1),
            "outbox position inflight state matches");
        Check.Equal(
            expectInflight,
            reader.GetBoolean(2),
            "outbox position lease state matches inflight state");
    }

    private static async Task MakeAvailableAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET available_at = clock_timestamp(),
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND delivered_at IS NULL
              AND poisoned_at IS NULL
              AND lease_token IS NULL;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "one pending outbox event is made immediately available");
    }

    private static async Task DelayAvailabilityAsync(
        NpgsqlDataSource dataSource,
        long rowId,
        TimeSpan delay)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET available_at = clock_timestamp() + @delay,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id
              AND delivered_at IS NULL
              AND poisoned_at IS NULL
              AND lease_token IS NULL;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        command.Parameters.AddWithValue("delay", delay);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "one pending outbox event is delayed");
    }

    private static DateTimeOffset? ReadOptionalTimestamp(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(reader.GetDateTime(ordinal));
}
