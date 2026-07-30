using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task AssertRideRuntimeBlockedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "riding",
            kitBagItem: Item(6000),
            equipmentSlot: 20);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var blocked = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                operationId,
                mountRuntimeBlocked: true),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.RideRuntimeBlocked,
            "Ride runtime blocks mount transfer");

        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.Subject.CharacterId),
                operationId,
                fixture.EquipmentSlot,
                fixture.KitBagSlot),
            EquipmentBagTransferDisposition.Duplicate,
            EquipmentBagTransferResultStatus.RideRuntimeBlocked,
            "Ride runtime rejection replay");
        Check.True(
            replay == blocked,
            "Ride runtime replay returns the stored receipt");

        var changedObservation = await ExecuteAsync(
            executor,
            fixture,
            operationId,
            mountRuntimeBlocked: false);
        Check.Equal(
            (int)EquipmentBagTransferDisposition.RequestHashConflict,
            (int)changedObservation.Disposition,
            "same UUID cannot change its Ride runtime observation");

        var state = await ReadStateAsync(connectionString, fixture);
        AssertUnchangedTerminalState(
            state,
            0,
            fixture.KitBagItemId!.Value,
            "Ride runtime rejection");
        Check.Equal(
            1,
            state.DuplicateCount,
            "Ride runtime rejection records one replay");
        Check.Equal(
            1,
            state.ConflictCount,
            "Ride runtime flag is request-hash bound");
    }

    private static async Task AssertInvalidRideObservationAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ridebad",
            kitBagItem: Item(6000),
            equipmentSlot: 20);
        var operationId = Guid.NewGuid();
        Check.True(
            EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                operationId,
                fixture.EquipmentSlot,
                fixture.KitBagSlot,
                fixture.EquipmentState,
                fixture.KitBagState,
                mountRuntimeBlocked: true,
                out var command),
            "valid mount observation fixture");
        var envelope = PlayerOwnershipTestFences.Bind(
            EquipmentBagTransferCommandEnvelope.Create(
                fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command));
        var invalid = envelope with
        {
            Command = envelope.Command with
            {
                EquipmentSlot = 10
            }
        };
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(dataSource).ExecuteAsync(
            invalid);
        Check.Equal(
            (int)EquipmentBagTransferDisposition.InvalidIntent,
            (int)result.Disposition,
            "Ride runtime flag on a non-mount slot is invalid");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(0L, state.InventoryRevision, "invalid Ride revision");
        Check.Equal(0L, state.AuditCount, "invalid Ride audit");
        Check.Equal(0L, state.InboxCount, "invalid Ride inbox");
        Check.Equal(0L, state.LedgerCount, "invalid Ride ledger");
        Check.Equal(0L, state.OutboxCount, "invalid Ride outbox");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.KitBagItemId,
            "invalid Ride observation preserves the item");
    }
}
