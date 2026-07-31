using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Reconciliation;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    private static async Task<long> SeedExpiredOutboxLeaseAsync(
        NpgsqlDataSource dataSource)
    {
        var leaseToken = Guid.NewGuid();
        var leaseExpiry = DateTime.UtcNow.AddMinutes(-5);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        ExpiredLeaseTarget target;
        await using (var select = new NpgsqlCommand("""
            SELECT
                event.id,
                event.consumer_key,
                event.aggregate_type,
                event.aggregate_key,
                event.aggregate_version,
                event.ordering_policy
            FROM public.outbox_events event
            JOIN public.command_inbox inbox
              ON inbox.id = event.command_inbox_id
            JOIN public.accounts account_row
              ON inbox.principal_key = account_row.id::text
            JOIN public.outbox_consumer_positions position
              ON position.consumer_key = event.consumer_key
             AND position.aggregate_type = event.aggregate_type
             AND position.aggregate_key = event.aggregate_key
            WHERE account_row.username = 'b19_expected_purge'
              AND event.event_type = 'character.created'
              AND event.aggregate_version = 1
              AND event.delivered_at IS NULL
              AND event.poisoned_at IS NULL
              AND event.lease_token IS NULL
              AND position.current_version = 0
              AND position.inflight_event_id IS NULL
            ORDER BY event.id
            LIMIT 1;
            """, connection, transaction))
        {
            await using var reader = await select.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidDataException(
                    "The real lifecycle event for B19 lease recovery is missing.");
            }

            target = new ExpiredLeaseTarget(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5));
        }

        await using (var leaseEvent = new NpgsqlCommand("""
            UPDATE public.outbox_events
            SET attempt_count = 1,
                lease_owner = 'b19-expired-owner',
                lease_token = @lease_token,
                lease_expires_at = @lease_expiry,
                state_changed_at = clock_timestamp()
            WHERE id = @row_id;
            """, connection, transaction))
        {
            AddLeaseParameters(
                leaseEvent,
                target.RowId,
                leaseToken,
                leaseExpiry);
            Check.Equal(
                1,
                await leaseEvent.ExecuteNonQueryAsync(),
                "one B19 outbox event receives an expired lease");
        }

        await using (var leasePosition = new NpgsqlCommand("""
            UPDATE public.outbox_consumer_positions
            SET inflight_event_id = @row_id,
                inflight_version = @aggregate_version,
                lease_owner = 'b19-expired-owner',
                lease_token = @lease_token,
                lease_expires_at = @lease_expiry,
                updated_at = clock_timestamp()
            WHERE consumer_key = @consumer_key
              AND aggregate_type = @aggregate_type
              AND aggregate_key = @aggregate_key;
            """, connection, transaction))
        {
            AddLeaseParameters(
                leasePosition,
                target.RowId,
                leaseToken,
                leaseExpiry);
            leasePosition.Parameters.AddWithValue(
                "consumer_key",
                target.ConsumerKey);
            leasePosition.Parameters.AddWithValue(
                "aggregate_type",
                target.AggregateType);
            leasePosition.Parameters.AddWithValue(
                "aggregate_key",
                target.AggregateKey);
            leasePosition.Parameters.AddWithValue(
                "aggregate_version",
                target.AggregateVersion);
            Check.Equal(
                1,
                await leasePosition.ExecuteNonQueryAsync(),
                "one B19 position receives the matching expired lease");
        }

        await transaction.CommitAsync();
        return target.RowId;
    }

    private static void AddLeaseParameters(
        NpgsqlCommand command,
        long rowId,
        Guid leaseToken,
        DateTime leaseExpiry)
    {
        command.Parameters.AddWithValue("row_id", rowId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_expiry", leaseExpiry);
    }

    private static async Task AssertRepairBoundsAsync(
        IReconciliationRepairer repairer)
    {
        await ExpectAsync<ArgumentOutOfRangeException>(
            () => repairer.RecoverExpiredOutboxLeasesAsync(0),
            "expired lease repair rejects a zero bound");
        await ExpectAsync<ArgumentOutOfRangeException>(
            () => repairer.RecoverExpiredOutboxLeasesAsync(501),
            "expired lease repair rejects an oversized bound");
    }

    private static async Task AssertRecoveredLeaseAsync(
        NpgsqlDataSource dataSource,
        long rowId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                event.attempt_count,
                event.lease_token IS NULL,
                event.delivered_at IS NULL,
                event.poisoned_at IS NULL,
                position.inflight_event_id IS NULL,
                position.lease_token IS NULL
            FROM public.outbox_events event
            JOIN public.outbox_consumer_positions position
              ON position.consumer_key = event.consumer_key
             AND position.aggregate_type = event.aggregate_type
             AND position.aggregate_key = event.aggregate_key
            WHERE event.id = @row_id;
            """);
        command.Parameters.AddWithValue("row_id", rowId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt16(0) == 1 &&
            reader.GetBoolean(1) &&
            reader.GetBoolean(2) &&
            reader.GetBoolean(3) &&
            reader.GetBoolean(4) &&
            reader.GetBoolean(5),
            "safe repair clears only the expired event/position lease");
    }

    private static async Task AssertRecoveredLeaseMarkerAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*)::integer
            FROM public.outbox_events event
            JOIN public.command_inbox inbox
              ON inbox.id = event.command_inbox_id
            JOIN public.accounts account_row
              ON inbox.principal_key = account_row.id::text
            JOIN public.outbox_consumer_positions position
              ON position.consumer_key = event.consumer_key
             AND position.aggregate_type = event.aggregate_type
             AND position.aggregate_key = event.aggregate_key
            WHERE account_row.username = 'b19_expected_purge'
              AND event.event_type = 'character.created'
              AND event.aggregate_version = 1
              AND event.lease_token IS NULL
              AND event.delivered_at IS NULL
              AND event.poisoned_at IS NULL
              AND position.inflight_event_id IS NULL
              AND position.lease_token IS NULL;
            """);
        Check.Equal(
            1,
            Convert.ToInt32(await command.ExecuteScalarAsync()),
            "the recovered B19 lease marker survives restore");
    }

    private static async Task<string> ReadDurableFingerprintAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'migrations', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data)
                        ORDER BY migration_id)
                    FROM (
                        SELECT migration_id, checksum
                        FROM public.schema_migrations
                    ) row_data
                ), '[]'::jsonb),
                'characters', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
                    FROM public.character_base row_data
                ), '[]'::jsonb),
                'items', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
                    FROM public.character_items row_data
                ), '[]'::jsonb),
                'baselines', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data)
                        ORDER BY character_id)
                    FROM public.character_economy_baseline row_data
                ), '[]'::jsonb),
                'baselineItems', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data)
                        ORDER BY character_id, item_instance_id)
                    FROM public.character_inventory_baseline_items row_data
                ), '[]'::jsonb),
                'currencyLedger', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
                    FROM public.character_currency_ledger row_data
                ), '[]'::jsonb),
                'inventoryLedger', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
                    FROM public.character_inventory_ledger row_data
                ), '[]'::jsonb),
                'inbox', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
                    FROM public.command_inbox row_data
                ), '[]'::jsonb),
                'outbox', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data) ORDER BY id)
                    FROM public.outbox_events row_data
                ), '[]'::jsonb),
                'positions', COALESCE((
                    SELECT jsonb_agg(to_jsonb(row_data)
                        ORDER BY consumer_key, aggregate_type, aggregate_key)
                    FROM public.outbox_consumer_positions row_data
                ), '[]'::jsonb)
            )::text;
            """);
        var canonical = Convert.ToString(
            await command.ExecuteScalarAsync())
            ?? throw new InvalidDataException(
                "Could not read the restored durable fingerprint.");
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task ExpectAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }

    private sealed record ExpiredLeaseTarget(
        long RowId,
        string ConsumerKey,
        string AggregateType,
        string AggregateKey,
        long AggregateVersion,
        string OrderingPolicy);
}
