using Npgsql;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static async Task CheckPreflightFailClosedAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        await ExpectPostgresFailureAsync(
            () => Emulate025WithInvalidCheckpointAndApply026Async(
                dataSource),
            PostgresErrorCodes.RaiseException,
            "migration 026 fails closed on an unjustified 025 checkpoint");
        await CheckActiveLaterRevisionPreflightAsync(
            dataSource,
            fixture);
    }

    private static async Task
        Emulate025WithInvalidCheckpointAndApply026Async(
            NpgsqlDataSource dataSource)
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static entry =>
                entry.Id ==
                "20260729_026_command_inbox_outbox_hardening");
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await Rewind026To025ShapeAsync(
            connection,
            transaction);
        await using (var invalidCheckpoint = new NpgsqlCommand("""
            INSERT INTO public.outbox_consumer_positions (
                consumer_key,
                aggregate_type,
                aggregate_key,
                ordering_policy,
                current_version)
            VALUES (
                'checks.outbox.preflight',
                'protocol_check',
                'unjustified-checkpoint',
                'strict',
                100);
            """, connection, transaction))
        {
            await invalidCheckpoint.ExecuteNonQueryAsync();
        }

        await using var apply = new NpgsqlCommand(
            migration.Sql,
            connection,
            transaction);
        _ = await apply.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task CheckActiveLaterRevisionPreflightAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static entry =>
                entry.Id ==
                "20260729_026_command_inbox_outbox_hardening");
        var aggregateKey =
            NewAggregateKey("preflight-later-active");
        var leaseToken = Guid.NewGuid();
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await Rewind026To025ShapeAsync(
            connection,
            transaction);

        await using (var seed = new NpgsqlCommand("""
            WITH delivered AS (
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
                    delivered_at)
                VALUES (
                    @delivered_event_id,
                    @inbox_id,
                    'checks.outbox.preflight_later',
                    'protocol_check',
                    @aggregate_key,
                    1,
                    'protocol_check.event',
                    1,
                    'strict',
                    '{}'::jsonb,
                    now())
            ),
            leased AS (
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
                    lease_owner,
                    lease_token,
                    lease_expires_at)
                VALUES (
                    @leased_event_id,
                    @inbox_id,
                    'checks.outbox.preflight_later',
                    'protocol_check',
                    @aggregate_key,
                    2,
                    'protocol_check.event',
                    1,
                    'strict',
                    '{}'::jsonb,
                    1,
                    'checks-preflight',
                    @lease_token,
                    now() + interval '5 minutes')
                RETURNING id
            )
            INSERT INTO public.outbox_consumer_positions (
                consumer_key,
                aggregate_type,
                aggregate_key,
                ordering_policy,
                current_version,
                inflight_event_id,
                inflight_version,
                lease_owner,
                lease_token,
                lease_expires_at)
            SELECT
                'checks.outbox.preflight_later',
                'protocol_check',
                @aggregate_key,
                'strict',
                1,
                id,
                2,
                'checks-preflight',
                @lease_token,
                now() + interval '5 minutes'
            FROM leased;
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue(
                "delivered_event_id",
                Guid.NewGuid());
            seed.Parameters.AddWithValue(
                "leased_event_id",
                Guid.NewGuid());
            seed.Parameters.AddWithValue(
                "inbox_id",
                fixture.InboxId);
            seed.Parameters.AddWithValue(
                "aggregate_key",
                aggregateKey);
            seed.Parameters.AddWithValue(
                "lease_token",
                leaseToken);
            await seed.ExecuteNonQueryAsync();
        }

        await using (var apply = new NpgsqlCommand(
            migration.Sql,
            connection,
            transaction))
        {
            _ = await apply.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    private static async Task Rewind026To025ShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var rewind = new NpgsqlCommand("""
            ALTER TABLE public.command_audit
                DROP CONSTRAINT
                    ck_command_audit_aggregate_key_no_control;
            ALTER TABLE public.command_inbox
                DROP CONSTRAINT
                    ck_command_inbox_aggregate_key_no_control;
            ALTER TABLE public.outbox_events
                DROP CONSTRAINT ck_outbox_events_event_id_not_empty,
                DROP CONSTRAINT
                    ck_outbox_events_aggregate_key_no_control;
            ALTER TABLE public.outbox_consumer_positions
                DROP CONSTRAINT
                    ck_outbox_positions_aggregate_key_no_control;

            DROP TRIGGER trg_outbox_events_lease_consistency
                ON public.outbox_events;
            DROP TRIGGER trg_outbox_positions_lease_consistency
                ON public.outbox_consumer_positions;
            DROP TRIGGER trg_outbox_events_guard
                ON public.outbox_events;
            DROP TRIGGER trg_outbox_consumer_positions_guard
                ON public.outbox_consumer_positions;

            CREATE TRIGGER trg_outbox_events_guard
            BEFORE UPDATE OR DELETE ON public.outbox_events
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_outbox_event_mutation();

            CREATE TRIGGER trg_outbox_consumer_positions_guard
            BEFORE UPDATE ON public.outbox_consumer_positions
            FOR EACH ROW
            EXECUTE FUNCTION public.guard_outbox_consumer_position();
            """, connection, transaction);
        await rewind.ExecuteNonQueryAsync();
    }

    private static async Task CheckFinalStateCouplingGuardsAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string directConsumerKey =
            "checks.outbox.direct_delivery";
        var directAggregateKey =
            NewAggregateKey("direct-delivery");
        var directEvent = await InsertEventAsync(
            dataSource,
            fixture,
            directConsumerKey,
            directAggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        await InsertPositionAsync(
            dataSource,
            directConsumerKey,
            directAggregateKey,
            "strict",
            currentRevision: 0);
        await ExpectPostgresFailureAsync(
            () => DeliverWithoutCheckpointAsync(
                dataSource,
                directEvent.RowId),
            PostgresErrorCodes.RaiseException,
            "an event cannot be delivered without a matching checkpoint");

        var unpairedLease = await InsertEventAsync(
            dataSource,
            fixture,
            "checks.outbox.unpaired_lease",
            NewAggregateKey("unpaired-lease"),
            revision: 1,
            orderingPolicy: "strict");
        await ExpectPostgresFailureAsync(
            () => LeaseEventWithoutPositionAsync(
                dataSource,
                unpairedLease.RowId),
            PostgresErrorCodes.RaiseException,
            "an event lease requires a matching position lease");

        const string poisonConsumerKey =
            "checks.outbox.poison_checkpoint";
        var poisonAggregateKey =
            NewAggregateKey("poison-checkpoint");
        var poisonEvent = await InsertEventAsync(
            dataSource,
            fixture,
            poisonConsumerKey,
            poisonAggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        await InsertPositionAsync(
            dataSource,
            poisonConsumerKey,
            poisonAggregateKey,
            "strict",
            currentRevision: 0);
        await AcquireManualLeaseAsync(
            dataSource,
            poisonEvent.RowId,
            poisonConsumerKey,
            poisonAggregateKey);
        await ExpectPostgresFailureAsync(
            () => CheckpointPoisonedEventAsync(
                dataSource,
                poisonEvent.RowId,
                poisonConsumerKey,
                poisonAggregateKey),
            PostgresErrorCodes.RaiseException,
            "a poisoned event cannot justify checkpoint advancement");
    }

    private static async Task DeliverWithoutCheckpointAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET delivered_at = clock_timestamp(),
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task LeaseEventWithoutPositionAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox_events
            SET attempt_count = attempt_count + 1,
                lease_owner = 'checks-unpaired',
                lease_token = @lease_token,
                lease_expires_at =
                    clock_timestamp() + interval '5 minutes',
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        command.Parameters.AddWithValue(
            "lease_token",
            Guid.NewGuid());
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task CheckpointPoisonedEventAsync(
        NpgsqlDataSource dataSource,
        long rowId,
        string consumerKey,
        string aggregateKey)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using (var checkpoint = new NpgsqlCommand("""
            UPDATE public.outbox_consumer_positions
            SET current_version = inflight_version,
                inflight_event_id = NULL,
                inflight_version = NULL,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE consumer_key = @consumer_key
              AND aggregate_type = 'protocol_check'
              AND aggregate_key = @aggregate_key;
            """, connection, transaction))
        {
            checkpoint.Parameters.AddWithValue(
                "consumer_key",
                consumerKey);
            checkpoint.Parameters.AddWithValue(
                "aggregate_key",
                aggregateKey);
            Check.Equal(
                1,
                await checkpoint.ExecuteNonQueryAsync(),
                "one malicious poison checkpoint is staged");
        }

        await using (var poison = new NpgsqlCommand("""
            UPDATE public.outbox_events
            SET lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                poisoned_at = clock_timestamp(),
                poison_reason = 'checks_poison',
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """, connection, transaction))
        {
            poison.Parameters.AddWithValue("row_id", rowId);
            Check.Equal(
                1,
                await poison.ExecuteNonQueryAsync(),
                "one malicious poison event transition is staged");
        }

        await transaction.CommitAsync();
    }
}
