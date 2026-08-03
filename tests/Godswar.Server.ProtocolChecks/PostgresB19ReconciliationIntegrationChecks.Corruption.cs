using Godswar.Server.Application.Reconciliation;
using Npgsql;
using System.Security.Cryptography;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    private static async Task AssertLedgerChainTamperingAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture,
        ReconciliationRunner runner)
    {
        await ConvertInventoryBaselineToLegacyItemShapeAsync(
            dataSource,
            fixture);
        var legacyBaseline = await runner.RunAsync();
        AssertCompleted(
            legacyBaseline,
            "legacy item-schema baseline reconciliation");
        AssertNoFindings(
            legacyBaseline,
            "an old-shape baseline remains semantically equal to current item state");

        var inboxIds = await CreateEconomyInboxIdsAsync(
            dataSource,
            fixture);
        await using (var connection =
                     await dataSource.OpenConnectionAsync())
        await using (var transaction =
                     await connection.BeginTransactionAsync())
        {
            await InsertWalletLedgerAsync(
                connection,
                transaction,
                inboxIds[0],
                fixture,
                revision: 2,
                balanceBefore: fixture.Money + 1,
                balanceAfter: fixture.Money + 2);
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                inboxIds[0],
                fixture,
                revision: 2,
                beforeIncrement: 1,
                afterIncrement: 2);
            await transaction.CommitAsync();
        }

        var tampered = await runner.RunAsync();
        AssertCompleted(tampered, "ledger-chain tamper reconciliation");
        Check.True(
            Find(
                tampered,
                ReconciliationCategory.WalletLedgerChainMismatch) >= 1,
            "a wallet ledger whose first visible link skips the baseline " +
            "is detected");
        Check.True(
            Find(
                tampered,
                ReconciliationCategory.InventoryLedgerChainMismatch) >= 1,
            "an inventory ledger whose first visible link skips the " +
            "baseline is detected");

        await using (var connection =
                     await dataSource.OpenConnectionAsync())
        await using (var transaction =
                     await connection.BeginTransactionAsync())
        {
            await InsertWalletLedgerAsync(
                connection,
                transaction,
                inboxIds[1],
                fixture,
                revision: 1,
                balanceBefore: fixture.Money,
                balanceAfter: fixture.Money + 1);
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                inboxIds[1],
                fixture,
                revision: 1,
                beforeIncrement: 0,
                afterIncrement: 1);
            await using var update = new NpgsqlCommand(
                """
                UPDATE public.character_base
                SET "Money" = @money,
                    wallet_revision = 2,
                    inventory_revision = 2
                WHERE id = @character_id;

                UPDATE public.character_items
                SET item_exp = @item_exp
                WHERE id = @item_id
                  AND user_id = @character_id;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue(
                "money",
                fixture.Money + 2);
            update.Parameters.AddWithValue(
                "item_exp",
                fixture.ItemExperience + 2);
            update.Parameters.AddWithValue(
                "item_id",
                fixture.ItemId);
            update.Parameters.AddWithValue(
                "character_id",
                fixture.CharacterId);
            Check.Equal(
                2,
                await update.ExecuteNonQueryAsync(),
                "missing ledger links and matching current state are " +
                "completed atomically");
            await transaction.CommitAsync();
        }

        await AssertCrossVersionInventoryChainFixtureAsync(
            dataSource,
            fixture);

        var completed = await runner.RunAsync();
        AssertCompleted(completed, "completed ledger-chain reconciliation");
        Check.Equal(
            0L,
            Find(
                completed,
                ReconciliationCategory.WalletLedgerChainMismatch),
            "a contiguous wallet ledger chain is clean");
        Check.Equal(
            0L,
            Find(
                completed,
                ReconciliationCategory.InventoryLedgerChainMismatch),
            "a contiguous old-baseline to new-ledger inventory chain is clean");
        AssertNoFindings(
            completed,
            "completed ledger chains restore a zero-mismatch source");
    }

    private static async Task<long[]> CreateEconomyInboxIdsAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        var ids = new long[2];
        for (var index = 0; index < ids.Length; index++)
        {
            var operationId = Guid.NewGuid().ToByteArray();
            var requestHash = SHA256.HashData(operationId);
            var resultHash = SHA256.HashData(
                requestHash.Concat([(byte)index]).ToArray());
            await using var command = dataSource.CreateCommand(
            """
            WITH audit AS (
                INSERT INTO public.command_audit (
                    principal_type,
                    principal_key,
                    aggregate_type,
                    aggregate_key,
                    command_family,
                    operation_id,
                    request_hash,
                    outcome_code,
                    detail_payload
                )
                VALUES (
                    'account',
                    @principal_key,
                    'character_economy',
                    @aggregate_key,
                    'b19_ledger_chain_fixture',
                    @operation_id,
                    @request_hash,
                    'committed',
                    '{"fixture":"b19_ledger_chain"}'::jsonb
                )
                RETURNING id
            )
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
                audit_id
            )
            SELECT
                'account',
                @principal_key,
                'character_economy',
                @aggregate_key,
                'b19_ledger_chain_fixture',
                @operation_id,
                @request_hash,
                1,
                'committed',
                '{"status":"committed"}'::jsonb,
                @result_hash,
                audit.id
            FROM audit
            RETURNING id;
            """);
            command.Parameters.AddWithValue(
                "principal_key",
                fixture.AccountId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "aggregate_key",
                $"character:{fixture.CharacterId}:economy");
            command.Parameters.AddWithValue(
                "operation_id",
                operationId);
            command.Parameters.AddWithValue(
                "request_hash",
                requestHash);
            command.Parameters.AddWithValue(
                "result_hash",
                resultHash);
            ids[index] = Convert.ToInt64(
                await command.ExecuteScalarAsync());
        }

        Check.True(
            ids.All(static id => id > 0) &&
            ids.Distinct().Count() == ids.Length,
            "two economy-fixture inbox receipts are durable and distinct");
        return ids;
    }

    private static async Task InsertWalletLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        EconomyFixture fixture,
        long revision,
        int balanceBefore,
        int balanceAfter)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id,
                account_id,
                character_id,
                wallet_revision,
                currency_code,
                delta,
                balance_before,
                balance_after,
                reason_code
            )
            VALUES (
                @inbox_id,
                @account_id,
                @character_id,
                @revision,
                'silver',
                @delta,
                @balance_before,
                @balance_after,
                'b19.chain.test'
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inbox_id", inboxId);
        command.Parameters.AddWithValue("account_id", fixture.AccountId);
        command.Parameters.AddWithValue(
            "character_id",
            fixture.CharacterId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue(
            "delta",
            balanceAfter - balanceBefore);
        command.Parameters.AddWithValue(
            "balance_before",
            balanceBefore);
        command.Parameters.AddWithValue(
            "balance_after",
            balanceAfter);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"wallet ledger revision {revision} is seeded");
    }

    private static async Task AssertOutboxPositionMismatchAsync(
        NpgsqlDataSource dataSource,
        ReconciliationRunner runner,
        long eventRowId)
    {
        var leaseToken = Guid.NewGuid();
        await ExecuteDisposableCorruptionAsync(
            dataSource,
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    UPDATE public.outbox_consumer_positions position
                    SET inflight_event_id = event.id,
                        inflight_version = event.aggregate_version,
                        lease_owner = 'b19-position-mismatch',
                        lease_token = @lease_token,
                        lease_expires_at =
                            clock_timestamp() + interval '5 minutes',
                        updated_at = clock_timestamp()
                    FROM public.outbox_events event
                    WHERE event.id = @event_id
                      AND position.consumer_key = event.consumer_key
                      AND position.aggregate_type = event.aggregate_type
                      AND position.aggregate_key = event.aggregate_key;
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue(
                    "event_id",
                    eventRowId);
                command.Parameters.AddWithValue(
                    "lease_token",
                    leaseToken);
                Check.Equal(
                    1,
                    await command.ExecuteNonQueryAsync(),
                    "one consumer position is deliberately mismatched");
            });

        var pairedMismatch = await runner.RunAsync();
        AssertCompleted(
            pairedMismatch,
            "paired outbox-position mismatch reconciliation");
        Check.True(
            Find(
                pairedMismatch,
                ReconciliationCategory.OutboxLeaseMismatch) >= 1,
            "a position lease without the matching event lease is detected");

        await using (var command = dataSource.CreateCommand(
            """
            UPDATE public.outbox_consumer_positions
            SET inflight_event_id = NULL,
                inflight_version = NULL,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE inflight_event_id = @event_id
              AND lease_token = @lease_token;
            """))
        {
            command.Parameters.AddWithValue("event_id", eventRowId);
            command.Parameters.AddWithValue("lease_token", leaseToken);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                "the paired mismatch fixture is restored");
        }

        var aggregateKey = $"b19-position-only-{Guid.NewGuid():N}";
        await ExecuteDisposableCorruptionAsync(
            dataSource,
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO public.outbox_consumer_positions (
                        consumer_key,
                        aggregate_type,
                        aggregate_key,
                        ordering_policy,
                        current_version
                    )
                    VALUES (
                        'character_lifecycle_v1',
                        'account_character_slot',
                        @aggregate_key,
                        'strict',
                        1
                    );
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue(
                    "aggregate_key",
                    aggregateKey);
                Check.Equal(
                    1,
                    await command.ExecuteNonQueryAsync(),
                    "one position-only stream is deliberately seeded");
            });

        var positionOnly = await runner.RunAsync();
        AssertCompleted(
            positionOnly,
            "position-only mismatch reconciliation");
        Check.True(
            Find(
                positionOnly,
                ReconciliationCategory
                    .OutboxConsumerPositionMismatch) >= 1,
            "a consumer position without matching stream evidence is " +
            "detected");

        await ExecuteDisposableCorruptionAsync(
            dataSource,
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    DELETE FROM public.outbox_consumer_positions
                    WHERE consumer_key = 'character_lifecycle_v1'
                      AND aggregate_type = 'account_character_slot'
                      AND aggregate_key = @aggregate_key;
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue(
                    "aggregate_key",
                    aggregateKey);
                Check.Equal(
                    1,
                    await command.ExecuteNonQueryAsync(),
                    "the position-only mismatch fixture is removed");
            });

        var clean = await runner.RunAsync();
        AssertCompleted(clean, "restored outbox-position reconciliation");
        Check.Equal(
            0L,
            Find(
                clean,
                ReconciliationCategory.OutboxLeaseMismatch),
            "the restored paired lease is clean");
        Check.Equal(
            0L,
            Find(
                clean,
                ReconciliationCategory
                    .OutboxConsumerPositionMismatch),
            "the removed position-only fixture is clean");
    }

    private static async Task ExecuteDisposableCorruptionAsync(
        NpgsqlDataSource dataSource,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> action)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await SetReplicationRoleAsync(
            connection,
            transaction,
            replica: true);
        try
        {
            await action(connection, transaction);
            await SetReplicationRoleAsync(
                connection,
                transaction,
                replica: false);
            await transaction.CommitAsync();
        }
        catch
        {
            try
            {
                await SetReplicationRoleAsync(
                    connection,
                    transaction,
                    replica: false);
            }
            finally
            {
                await transaction.RollbackAsync();
            }

            throw;
        }
    }

    private static async Task SetReplicationRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool replica)
    {
        await using var command = new NpgsqlCommand(
            replica
                ? "SET LOCAL session_replication_role = replica;"
                : "SET LOCAL session_replication_role = origin;",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync();
    }
}
