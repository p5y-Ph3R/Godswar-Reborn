using Godswar.Server.Application.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static async Task CheckSchemaHardeningAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        await CheckPreflightFailClosedAsync(
            dataSource,
            fixture);

        await ExpectPostgresFailureAsync(
            () => InsertEventAsync(
                dataSource,
                fixture,
                "checks.outbox.empty_uuid",
                NewAggregateKey("empty-uuid"),
                revision: 1,
                orderingPolicy: "strict",
                eventId: Guid.Empty),
            PostgresErrorCodes.CheckViolation,
            "the database rejects an empty outbox event UUID");

        await ExpectPostgresFailureAsync(
            () => InsertEventAsync(
                dataSource,
                fixture,
                "checks.outbox.control_key",
                "control-key:\r",
                revision: 1,
                orderingPolicy: "strict"),
            PostgresErrorCodes.CheckViolation,
            "outbox event aggregate keys reject control characters");

        await ExpectPostgresFailureAsync(
            () => InsertPositionAsync(
                dataSource,
                "checks.outbox.position_control",
                "control-position:\n",
                "strict",
                currentRevision: 0),
            PostgresErrorCodes.CheckViolation,
            "consumer-position aggregate keys reject control characters");

        await ExpectPostgresFailureAsync(
            () => InsertEventAsync(
                dataSource,
                fixture,
                "checks.outbox.attempted_insert",
                NewAggregateKey("attempted-insert"),
                revision: 1,
                orderingPolicy: "strict",
                initialAttemptCount: 1),
            PostgresErrorCodes.RaiseException,
            "new outbox events cannot start with consumed attempts");

        await ExpectPostgresFailureAsync(
            () => InsertEventAsync(
                dataSource,
                fixture,
                "checks.outbox.terminal_insert",
                NewAggregateKey("terminal-insert"),
                revision: 1,
                orderingPolicy: "strict",
                startDelivered: true),
            PostgresErrorCodes.RaiseException,
            "new outbox events cannot start terminal");

        await ExpectPostgresFailureAsync(
            () => InsertPositionAsync(
                dataSource,
                "checks.outbox.advanced_insert",
                NewAggregateKey("advanced-insert"),
                "strict",
                currentRevision: 1),
            PostgresErrorCodes.RaiseException,
            "new consumer positions must start idle at version zero");

        const string consumerKey = "checks.outbox.position_guard";
        var aggregateKey = NewAggregateKey("position-guard");
        var delivered = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-position-guard");
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "a leased consumer position advances through normal delivery");
        await AssertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            expectedRevision: 1,
            expectInflight: false);

        await ExpectPostgresFailureAsync(
            () => ResurrectDeliveredEventAsync(
                dataSource,
                delivered.RowId),
            PostgresErrorCodes.RaiseException,
            "terminal outbox events cannot be resurrected");

        await ExpectPostgresFailureAsync(
            () => SetPositionVersionAsync(
                dataSource,
                consumerKey,
                aggregateKey,
                currentRevision: 2),
            PostgresErrorCodes.RaiseException,
            "idle consumer positions cannot skip forward");

        await ExpectPostgresFailureAsync(
            () => SetPositionVersionAsync(
                dataSource,
                consumerKey,
                aggregateKey,
                currentRevision: 0),
            PostgresErrorCodes.RaiseException,
            "consumer positions cannot move backwards");

        await ExpectPostgresFailureAsync(
            () => DeletePositionAsync(
                dataSource,
                consumerKey,
                aggregateKey),
            PostgresErrorCodes.RaiseException,
            "consumer positions cannot be deleted");

        var attemptJump = await InsertEventAsync(
            dataSource,
            fixture,
            "checks.outbox.attempt_jump",
            NewAggregateKey("attempt-jump"),
            revision: 1,
            orderingPolicy: "strict");
        await ExpectPostgresFailureAsync(
            () => JumpAttemptCountAsync(
                dataSource,
                attemptJump.RowId),
            PostgresErrorCodes.RaiseException,
            "outbox attempts cannot jump without a lease acquisition");

        await CheckFinalStateCouplingGuardsAsync(
            dataSource,
            fixture);
        await CheckLeaseConsistencyGuardsAsync(
            dataSource,
            fixture);
    }

    private static async Task CheckLeaseConsistencyGuardsAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.lease_guard";
        var aggregateKey = NewAggregateKey("lease-guard");
        var leased = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        await InsertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            "strict",
            currentRevision: 0);
        await AcquireManualLeaseAsync(
            dataSource,
            leased.RowId,
            consumerKey,
            aggregateKey);

        _ = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 2,
            orderingPolicy: "strict");

        await ExpectPostgresFailureAsync(
            () => RetargetEventLeaseAsync(
                dataSource,
                leased.RowId),
            PostgresErrorCodes.RaiseException,
            "an active event lease cannot be retargeted");
        await ExpectPostgresFailureAsync(
            () => RetargetPositionLeaseAsync(
                dataSource,
                consumerKey,
                aggregateKey),
            PostgresErrorCodes.RaiseException,
            "an active position lease cannot be retargeted");
        await ExpectPostgresFailureAsync(
            () => MismatchEventLeaseExpiryAsync(
                dataSource,
                leased.RowId),
            PostgresErrorCodes.RaiseException,
            "event and position lease expirations cannot diverge");
    }

    private static async Task AcquireManualLeaseAsync(
        NpgsqlDataSource dataSource,
        long rowId,
        string consumerKey,
        string aggregateKey)
    {
        const string leaseOwner = "checks-manual-lease";
        var leaseToken = Guid.NewGuid();
        var leaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using (var updateEvent = new NpgsqlCommand("""
            UPDATE public.outbox_events
            SET attempt_count = attempt_count + 1,
                lease_owner = @lease_owner,
                lease_token = @lease_token,
                lease_expires_at = @lease_expires_at,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """, connection, transaction))
        {
            AddLeaseParameters(
                updateEvent,
                rowId,
                leaseOwner,
                leaseToken,
                leaseExpiresAt);
            Check.Equal(
                1,
                await updateEvent.ExecuteNonQueryAsync(),
                "one event receives the manual paired lease");
        }

        await using (var updatePosition = new NpgsqlCommand("""
            UPDATE public.outbox_consumer_positions
            SET inflight_event_id = @row_id,
                inflight_version = 1,
                lease_owner = @lease_owner,
                lease_token = @lease_token,
                lease_expires_at = @lease_expires_at,
                updated_at = clock_timestamp()
            WHERE consumer_key = @consumer_key
              AND aggregate_type = 'protocol_check'
              AND aggregate_key = @aggregate_key;
            """, connection, transaction))
        {
            AddLeaseParameters(
                updatePosition,
                rowId,
                leaseOwner,
                leaseToken,
                leaseExpiresAt);
            updatePosition.Parameters.AddWithValue(
                "consumer_key",
                consumerKey);
            updatePosition.Parameters.AddWithValue(
                "aggregate_key",
                aggregateKey);
            Check.Equal(
                1,
                await updatePosition.ExecuteNonQueryAsync(),
                "one position receives the matching manual lease");
        }

        await transaction.CommitAsync();
    }

    private static void AddLeaseParameters(
        NpgsqlCommand command,
        long rowId,
        string leaseOwner,
        Guid leaseToken,
        DateTime leaseExpiresAt)
    {
        command.Parameters.AddWithValue("row_id", rowId);
        command.Parameters.AddWithValue("lease_owner", leaseOwner);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue(
            "lease_expires_at",
            leaseExpiresAt);
    }

    private static async Task ResurrectDeliveredEventAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET delivered_at = NULL,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task JumpAttemptCountAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET attempt_count = attempt_count + 2,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task RetargetEventLeaseAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET lease_token = @lease_token,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        command.Parameters.AddWithValue(
            "lease_token",
            Guid.NewGuid());
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task RetargetPositionLeaseAsync(
        NpgsqlDataSource dataSource,
        string consumerKey,
        string aggregateKey)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_consumer_positions
            SET lease_token = @lease_token,
                updated_at = clock_timestamp()
            WHERE consumer_key = @consumer_key
              AND aggregate_type = 'protocol_check'
              AND aggregate_key = @aggregate_key;
            """);
        command.Parameters.AddWithValue("consumer_key", consumerKey);
        command.Parameters.AddWithValue("aggregate_key", aggregateKey);
        command.Parameters.AddWithValue(
            "lease_token",
            Guid.NewGuid());
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task MismatchEventLeaseExpiryAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET lease_expires_at = lease_expires_at
                    + interval '1 second',
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task SetPositionVersionAsync(
        NpgsqlDataSource dataSource,
        string consumerKey,
        string aggregateKey,
        long currentRevision)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_consumer_positions
            SET current_version = @current_version,
                updated_at = now()
            WHERE consumer_key = @consumer_key
              AND aggregate_type = 'protocol_check'
              AND aggregate_key = @aggregate_key;
            """);
        command.Parameters.AddWithValue(
            "current_version",
            currentRevision);
        command.Parameters.AddWithValue("consumer_key", consumerKey);
        command.Parameters.AddWithValue("aggregate_key", aggregateKey);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "one consumer position version is updated");
    }

    private static async Task DeletePositionAsync(
        NpgsqlDataSource dataSource,
        string consumerKey,
        string aggregateKey)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.outbox_consumer_positions
            WHERE consumer_key = @consumer_key
              AND aggregate_type = 'protocol_check'
              AND aggregate_key = @aggregate_key;
            """);
        command.Parameters.AddWithValue("consumer_key", consumerKey);
        command.Parameters.AddWithValue("aggregate_key", aggregateKey);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task ExpectPostgresFailureAsync(
        Func<Task> action,
        string expectedSqlState,
        string description)
    {
        try
        {
            await action();
        }
        catch (PostgresException exception)
            when (exception.SqlState == expectedSqlState)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected PostgreSQL " +
            $"SQLSTATE {expectedSqlState}.");
    }
}
