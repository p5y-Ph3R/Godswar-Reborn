using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Warehouse;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWarehouseCommandIntegrationChecks
{
    private static async Task AssertFoundationGuardsAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "guards");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            await using var update = new NpgsqlCommand(
                """
                UPDATE public.character_base
                SET warehouse_capacity = 80,
                    warehouse_revision = 1
                WHERE id = @characterId;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            Check.Equal(1, await update.ExecuteNonQueryAsync(),
                "direct capacity update reaches deferred evidence guard");
            try
            {
                await transaction.CommitAsync();
                throw new InvalidOperationException(
                    "Unaudited capacity update unexpectedly committed.");
            }
            catch (PostgresException error)
            {
                Check.Equal("23514", error.SqlState,
                    "deferred capacity evidence guard rejects direct update");
            }
        }
        var state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(40, state.Capacity, "failed direct capacity change rolls back");
        Check.Equal(0L, state.WarehouseRevision,
            "failed direct capacity revision rolls back");

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack,
                    item_exp, holy_suit_code, holy_socket_count)
                VALUES (@characterId, 3, 40, 4102, 1, 1, 1, 1, 0, 0, 0);
                """,
                connection);
            insert.Parameters.AddWithValue("characterId", fixture.CharacterId);
            try
            {
                await insert.ExecuteNonQueryAsync();
                throw new InvalidOperationException(
                    "Closed warehouse slot unexpectedly accepted an item.");
            }
            catch (PostgresException error)
            {
                Check.Equal("23514", error.SqlState,
                    "warehouse item trigger enforces opened capacity");
            }
        }

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var update = new NpgsqlCommand(
                """
                UPDATE public.warehouse_expansion_policy_levels
                SET key_cost = 2
                WHERE revision = 1 AND capacity = 80;
                """,
                connection);
            try
            {
                await update.ExecuteNonQueryAsync();
                throw new InvalidOperationException(
                    "Sealed warehouse policy unexpectedly mutated.");
            }
            catch (PostgresException error)
            {
                Check.Equal("55000", error.SqlState,
                    "sealed warehouse policy levels are immutable");
            }
        }
    }

    private static async Task AssertExplicitSameItemSwapAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "swap",
            new ItemPlacement(1, 10, 5),
            new ItemPlacement(3, 0, 7));
        var source = fixture.Items[(1, 10)];
        var destination = fixture.Items[(3, 0)];
        var result = await ExecuteTransferAsync(
            TransferExecutor(dataSource, templates),
            fixture,
            Guid.NewGuid(),
            WarehouseTransferOperation.Deposit,
            0,
            10,
            -1,
            source.CompactState,
            destination.CompactState);
        var receipt = result.Receipt ??
            throw new InvalidOperationException("Explicit swap has no receipt.");
        Check.Equal(
            (int)WarehouseTransferExecutionDisposition.Committed,
            (int)result.Disposition,
            "explicit same-item transfer commits");
        Check.Equal(
            (int)WarehouseTransferResultStatus.Swapped,
            (int)receipt.Status,
            "explicit compatible occupied target swaps, never merges");
        Check.Equal(
            destination.Id,
            (await ReadItemAsync(connectionString, fixture, 1, 10))!.Value.Id,
            "explicit destination identity moves to bag source");
        Check.Equal(
            source.Id,
            (await ReadItemAsync(connectionString, fixture, 3, 0))!.Value.Id,
            "explicit source identity moves to warehouse target");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(0L, state.WarehouseRevision, "transfer keeps capacity revision");
        Check.Equal(1L, state.InventoryRevision, "swap advances inventory once");
        Check.Equal(2L, state.LedgerCount, "swap appends two exact ledgers");
        Check.Equal(1L, state.OutboxCount, "swap emits one inventory event");
        Check.Equal(0L, state.TemporaryCount, "swap clears private staging");
    }

    private static async Task AssertAutomaticEmptyPrecedenceAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "empty",
            new ItemPlacement(1, 10, 5),
            new ItemPlacement(3, 1, 98));
        var source = fixture.Items[(1, 10)];
        var laterStack = fixture.Items[(3, 1)];
        var result = await ExecuteTransferAsync(
            TransferExecutor(dataSource, templates),
            fixture,
            Guid.NewGuid(),
            WarehouseTransferOperation.Deposit,
            -1,
            10,
            -1,
            source.CompactState,
            "[]");
        var receipt = result.Receipt!;
        Check.Equal(
            (int)WarehouseTransferResultStatus.Deposited,
            (int)receipt.Status,
            "earlier empty auto slot wins before later compatible stack");
        Check.Equal(0, receipt.ActualWarehouseSlot, "auto empty slot identity");
        var moved = await ReadItemAsync(connectionString, fixture, 3, 0);
        var untouched = await ReadItemAsync(connectionString, fixture, 3, 1);
        Check.True(
            moved is { } first && first.Id == source.Id && first.Stack == 5,
            "whole source moves into first empty warehouse cell");
        Check.True(
            untouched is { } second && second.Id == laterStack.Id &&
            second.Stack == 98,
            "later compatible stack remains untouched");
    }

    private static async Task AssertAutomaticFanOutAndReplayAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fanout",
            new ItemPlacement(1, 10, 2),
            new ItemPlacement(3, 0, 98),
            new ItemPlacement(3, 1, 98));
        var operationId = Guid.NewGuid();
        var source = fixture.Items[(1, 10)];
        var result = await ExecuteTransferAsync(
            TransferExecutor(dataSource, templates),
            fixture,
            operationId,
            WarehouseTransferOperation.Deposit,
            -1,
            10,
            -1,
            source.CompactState,
            "[]");
        var receipt = result.Receipt!;
        Check.Equal(
            (int)WarehouseTransferResultStatus.Stacked,
            (int)receipt.Status,
            "auto move pushes across compatible stacks");
        Check.Equal(2, receipt.MovedQuantity, "fan-out moves the whole source");
        Check.Equal(3, receipt.Mutations.Count, "fan-out evidence is exact");
        Check.True(
            (await ReadItemAsync(connectionString, fixture, 3, 0))!.Value.Stack == 99 &&
            (await ReadItemAsync(connectionString, fixture, 3, 1))!.Value.Stack == 99,
            "ascending compatible stacks fill before the empty cell");
        Check.True(
            await ReadItemAsync(connectionString, fixture, 1, 10) is null &&
            await ReadItemAsync(connectionString, fixture, 3, 2) is null,
            "fan-out deletes the exhausted source before the later empty cell");
        var snapshot = await new PostgresWarehouseSnapshotReader(dataSource)
            .ReadAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(fixture.CharacterId));
        Check.True(
            snapshot is { Capacity: 40, WarehouseRevision: 0,
                InventoryRevision: 1 } &&
            snapshot.Items.Select(static item => item.Slot)
                .SequenceEqual([0, 1]),
            "snapshot reader returns the exact post-fan-out warehouse state");

        var executor = TransferExecutor(dataSource, templates);
        var replay = await executor.TryReplayAsync(
            fixture.Subject,
            PlayerOwnershipTestFences.ForCharacter(fixture.CharacterId),
            new WarehouseTransferReplayIntent(
                1,
                WarehouseTransferOperation.Deposit,
                -1,
                10,
                -1,
                0,
                WarehouseStorageType.Normal),
            WarehouseOperationIdentity.SecureClient(operationId));
        Check.Equal(
            (int)WarehouseTransferExecutionDisposition.Duplicate,
            (int)replay.Disposition,
            "post-move stable wire intent replays the lost result");
        Check.Equal(
            receipt.AuditReference,
            replay.Receipt!.AuditReference,
            "lost-result replay returns the original evidence");
        var conflict = await executor.TryReplayAsync(
            fixture.Subject,
            PlayerOwnershipTestFences.ForCharacter(fixture.CharacterId),
            new WarehouseTransferReplayIntent(
                1,
                WarehouseTransferOperation.Deposit,
                0,
                10,
                -1,
                0,
                WarehouseStorageType.Normal),
            WarehouseOperationIdentity.SecureClient(operationId));
        Check.Equal(
            (int)WarehouseTransferExecutionDisposition.RequestHashConflict,
            (int)conflict.Disposition,
            "same identity with changed stable wire intent conflicts");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(1L, state.InventoryRevision, "replay never advances revision");
        Check.Equal(3L, state.LedgerCount, "replay never duplicates ledgers");
        Check.Equal(1L, state.OutboxCount, "replay never duplicates outbox");
    }

    private static async Task AssertTransferRollbackAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "rollback",
            new ItemPlacement(1, 10, 2));
        var source = fixture.Items[(1, 10)];
        try
        {
            _ = await ExecuteTransferAsync(
                TransferExecutor(
                    dataSource,
                    templates,
                    new ThrowBeforeCommitProbe()),
                fixture,
                Guid.NewGuid(),
                WarehouseTransferOperation.Deposit,
                0,
                10,
                -1,
                source.CompactState,
                "[]");
            throw new InvalidOperationException(
                "Injected warehouse fault did not interrupt commit.");
        }
        catch (InjectedWarehouseFault)
        {
        }
        var item = await ReadItemAsync(connectionString, fixture, 1, 10);
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            item is { } value && value.Id == source.Id && value.Stack == 2,
            "fault rollback preserves source identity and stack");
        Check.Equal(0L, state.InventoryRevision, "fault rolls back revision");
        Check.Equal(0L, state.InboxCount, "fault rolls back inbox");
        Check.Equal(0L, state.AuditCount, "fault rolls back command audit");
        Check.Equal(0L, state.LedgerCount, "fault rolls back ledgers");
        Check.Equal(0L, state.OutboxCount, "fault rolls back outbox");
    }

    private static async Task AssertExpansionAndLostResultReplayAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        WarehouseExpansionPolicySnapshot policy)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "expand",
            new ItemPlacement(1, 10, 1));
        var operationId = Guid.NewGuid();
        var identity = WarehouseOperationIdentity.SecureClient(operationId);
        Check.True(
            WarehouseExpansionCommandEnvelope.TryCreateCommand(
                identity,
                1,
                1001,
                WarehouseExpansionCommandEnvelope.DialogIndex,
                WarehouseExpansionCommandEnvelope.ActionSubId,
                40,
                policy,
                out var command),
            "warehouse expansion fixture creates a valid command");
        var executor = new PostgresWarehouseExpansionCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            policy);
        var committed = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                WarehouseExpansionCommandEnvelope.Create(
                    fixture.Subject,
                    new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.SecureTlsLegacy),
                    DateTimeOffset.UtcNow,
                    command)));
        Check.Equal(
            (int)WarehouseExpansionExecutionDisposition.Committed,
            (int)committed.Disposition,
            "warehouse expansion commits");
        Check.Equal(
            (int)WarehouseExpansionResultStatus.Expanded,
            (int)committed.Receipt!.Status,
            "warehouse expansion consumes the policy key");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(80, state.Capacity, "first expansion opens box two");
        Check.Equal(1L, state.WarehouseRevision, "capacity revision advances");
        Check.Equal(1L, state.InventoryRevision, "key inventory advances");
        Check.Equal(1L, state.LedgerCount, "key deletion has one ledger");
        Check.Equal(2L, state.OutboxCount, "expansion emits two aggregates");
        Check.Equal(1L, state.SettlementCount, "expansion settlement is durable");
        Check.True(
            await ReadItemAsync(connectionString, fixture, 1, 10) is null,
            "consumed Storage Box Key row is deleted");

        var replay = await executor.TryReplayAsync(
            fixture.Subject,
            PlayerOwnershipTestFences.ForCharacter(fixture.CharacterId),
            new WarehouseExpansionReplayIntent(
                1,
                WarehouseExpansionCommandEnvelope.ActionSubId),
            identity);
        Check.Equal(
            (int)WarehouseExpansionExecutionDisposition.Duplicate,
            (int)replay.Disposition,
            "changed capacity still replays lost expansion result");
        Check.Equal(
            committed.Receipt.AuditReference,
            replay.Receipt!.AuditReference,
            "expansion replay returns original evidence");
        state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(80, state.Capacity, "replay never expands twice");
        Check.Equal(1L, state.SettlementCount, "replay never duplicates settlement");
    }

    private static async Task AssertMaximumReceiptBoundAsync(
        string connectionString)
    {
        var mutations = new List<WarehouseItemMutation>(100)
        {
            new(
                long.MaxValue,
                int.MaxValue,
                WarehouseInventoryLocation.KitBag,
                0,
                99,
                null,
                null,
                null)
        };
        for (var slot = 0; slot < 99; slot++)
        {
            mutations.Add(new WarehouseItemMutation(
                long.MaxValue - 1 - slot,
                int.MaxValue,
                WarehouseInventoryLocation.Warehouse,
                slot,
                98,
                WarehouseInventoryLocation.Warehouse,
                slot,
                99));
        }
        var receipt = new WarehouseTransferExecutionReceipt(
            int.MaxValue,
            WarehouseTransferOperation.Deposit,
            -1,
            0,
            -1,
            0,
            0,
            WarehouseTransferResultStatus.Stacked,
            99,
            160,
            long.MaxValue - 1,
            long.MaxValue,
            mutations,
            long.MaxValue.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid());
        var payload = WarehouseTransferPersistenceCodec.Encode(receipt);
        Console.WriteLine(
            $"[warehouse-test] 100-mutation receipt bytes={payload.Length}");
        Check.True(
            payload.Length < OutboxEventMessage.MaximumPayloadBytes,
            "100-mutation canonical receipt remains below 16 KiB");
        var hash = WarehouseTransferPersistenceCodec.Hash(payload);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT @payload::jsonb::text;",
            connection);
        command.Parameters.AddWithValue("payload", Encoding.UTF8.GetString(payload));
        var jsonb = await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException("PostgreSQL returned no JSONB receipt.");
        var decoded = WarehouseTransferPersistenceCodec.DecodeAndVerify(
            jsonb,
            hash);
        Check.Equal(100, decoded.Mutations.Count, "JSONB replay keeps fan-out evidence");
        Check.True(
            WarehouseTransferPersistenceCodec.Encode(decoded)
                .SequenceEqual(payload),
            "JSONB replay reproduces exact canonical receipt bytes");
    }
}
