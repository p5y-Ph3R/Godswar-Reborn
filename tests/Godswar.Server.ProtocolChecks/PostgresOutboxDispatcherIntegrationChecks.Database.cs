using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private sealed record CommandFixture(long InboxId);

    private sealed record InsertedEvent(
        long RowId,
        Guid EventId,
        DateTimeOffset CreatedAtUtc);

    private sealed record EventState(
        int AttemptCount,
        bool HasLease,
        DateTimeOffset? DeliveredAtUtc,
        DateTimeOffset? PoisonedAtUtc,
        string? PoisonReason);

    private static async Task RequireDisposableB03DatabaseAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        var databaseName = Convert.ToString(
            await command.ExecuteScalarAsync())
            ?? throw new InvalidOperationException(
                "PostgreSQL did not return its current database name.");
        if (!Regex.IsMatch(
                databaseName,
                "^godswar_b03_[a-f0-9]{10}_smoke_[0-9]{2}$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "The outbox dispatcher check creates immutable fixture rows " +
                "and may run only in a disposable B03 smoke database.");
        }
    }

    private static async Task<CommandFixture> CreateCommandFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid().ToByteArray();
        var requestHash = RandomNumberGenerator.GetBytes(32);
        var resultHash = RandomNumberGenerator.GetBytes(32);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        long auditId;
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO public.command_audit (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                outcome_code,
                detail_payload)
            VALUES (
                'protocol_check',
                @principal_key,
                'dispatcher_fixture',
                @aggregate_key,
                'dispatcher_fixture',
                @operation_id,
                @request_hash,
                'committed',
                '{}'::jsonb)
            RETURNING id;
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue(
                "principal_key",
                $"protocol-check:{suffix}");
            audit.Parameters.AddWithValue(
                "aggregate_key",
                $"dispatcher-fixture:{suffix}");
            audit.Parameters.AddWithValue(
                "operation_id",
                NpgsqlDbType.Bytea,
                operationId);
            audit.Parameters.AddWithValue(
                "request_hash",
                NpgsqlDbType.Bytea,
                requestHash);
            auditId = Convert.ToInt64(
                await audit.ExecuteScalarAsync());
        }

        long inboxId;
        await using (var inbox = new NpgsqlCommand("""
            INSERT INTO public.command_inbox (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload,
                result_hash,
                audit_id)
            VALUES (
                'protocol_check',
                @principal_key,
                'dispatcher_fixture',
                @aggregate_key,
                'dispatcher_fixture',
                @operation_id,
                @request_hash,
                1,
                'committed',
                '{}'::jsonb,
                @result_hash,
                @audit_id)
            RETURNING id;
            """, connection, transaction))
        {
            inbox.Parameters.AddWithValue(
                "principal_key",
                $"protocol-check:{suffix}");
            inbox.Parameters.AddWithValue(
                "aggregate_key",
                $"dispatcher-fixture:{suffix}");
            inbox.Parameters.AddWithValue(
                "operation_id",
                NpgsqlDbType.Bytea,
                operationId);
            inbox.Parameters.AddWithValue(
                "request_hash",
                NpgsqlDbType.Bytea,
                requestHash);
            inbox.Parameters.AddWithValue(
                "result_hash",
                NpgsqlDbType.Bytea,
                resultHash);
            inbox.Parameters.AddWithValue("audit_id", auditId);
            inboxId = Convert.ToInt64(
                await inbox.ExecuteScalarAsync());
        }

        await transaction.CommitAsync();
        return new CommandFixture(inboxId);
    }

    private static async Task<InsertedEvent> InsertEventAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture,
        string consumerKey,
        string aggregateKey,
        long revision,
        string orderingPolicy,
        int maximumAttempts = 8,
        DateTimeOffset? createdAt = null,
        Guid? eventId = null,
        int initialAttemptCount = 0,
        bool startDelivered = false)
    {
        var durableEventId = eventId ?? Guid.NewGuid();
        var created = createdAt ?? DateTimeOffset.UtcNow.AddSeconds(-1);
        var available = DateTimeOffset.UtcNow;
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                attempt_count,
                max_attempts,
                available_at,
                delivered_at,
                created_at,
                state_changed_at)
            VALUES (
                @event_id,
                @command_inbox_id,
                @consumer_key,
                'protocol_check',
                @aggregate_key,
                @aggregate_version,
                'protocol_check.event',
                1,
                @ordering_policy,
                @payload,
                @attempt_count,
                @max_attempts,
                @available_at,
                CASE
                    WHEN @start_delivered THEN @created_at
                    ELSE NULL
                END,
                @created_at,
                @created_at)
            RETURNING id, created_at;
            """);
        command.Parameters.AddWithValue("event_id", durableEventId);
        command.Parameters.AddWithValue(
            "command_inbox_id",
            fixture.InboxId);
        command.Parameters.AddWithValue("consumer_key", consumerKey);
        command.Parameters.AddWithValue("aggregate_key", aggregateKey);
        command.Parameters.AddWithValue("aggregate_version", revision);
        command.Parameters.AddWithValue(
            "ordering_policy",
            orderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(new
            {
                aggregateKey,
                revision
            });
        command.Parameters.AddWithValue(
            "attempt_count",
            checked((short)initialAttemptCount));
        command.Parameters.AddWithValue(
            "max_attempts",
            checked((short)maximumAttempts));
        command.Parameters.AddWithValue(
            "start_delivered",
            startDelivered);
        command.Parameters.AddWithValue(
            "available_at",
            available.UtcDateTime);
        command.Parameters.AddWithValue(
            "created_at",
            created.UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "Outbox fixture insert returned no row.");
        }

        return new InsertedEvent(
            reader.GetInt64(0),
            durableEventId,
            new DateTimeOffset(reader.GetDateTime(1)));
    }

    private static async Task InsertPositionAsync(
        NpgsqlDataSource dataSource,
        string consumerKey,
        string aggregateKey,
        string orderingPolicy,
        long currentRevision)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.outbox_consumer_positions (
                consumer_key,
                aggregate_type,
                aggregate_key,
                ordering_policy,
                current_version)
            VALUES (
                @consumer_key,
                'protocol_check',
                @aggregate_key,
                @ordering_policy,
                @current_version);
            """);
        command.Parameters.AddWithValue("consumer_key", consumerKey);
        command.Parameters.AddWithValue("aggregate_key", aggregateKey);
        command.Parameters.AddWithValue(
            "ordering_policy",
            orderingPolicy);
        command.Parameters.AddWithValue(
            "current_version",
            currentRevision);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "one outbox position fixture is inserted");
    }

    private static string NewAggregateKey(string scenario) =>
        $"{scenario}:{Guid.NewGuid():N}";
}
