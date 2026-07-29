using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task AssertSuccessfulTransfersAsync(
        string connectionString)
    {
        await AssertEquipAsync(connectionString);
        await AssertUnequipAsync(connectionString);
        await AssertSecondRingSlotAsync(connectionString);
    }

    private static async Task AssertEquipAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "equip",
            kitBagItem: Item(1007));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.Committed,
            EquipmentBagTransferResultStatus.Equipped,
            "equip");
        Check.True(
            receipt.OutboxEventId.HasValue,
            "equip receipt has strict outbox event");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "equip revision");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.EquipmentItemId,
            "equip preserves item instance identity");
        Check.Equal(0L, state.KitBagItemId, "equip clears bag slot");
        AssertCommittedEvidence(state, "equip");
    }

    private static async Task AssertUnequipAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "unequip",
            equipmentItem: Item(1007));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.Committed,
            EquipmentBagTransferResultStatus.Unequipped,
            "unequip");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "unequip revision");
        Check.Equal(
            0L,
            state.EquipmentItemId,
            "unequip clears equipment slot");
        Check.Equal(
            fixture.EquipmentItemId!.Value,
            state.KitBagItemId,
            "unequip uses exact requested bag slot");
        AssertCommittedEvidence(state, "unequip");
    }

    private static async Task AssertSecondRingSlotAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ring2",
            kitBagItem: Item(3200),
            equipmentSlot: 9);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.Committed,
            EquipmentBagTransferResultStatus.Equipped,
            "second ring slot");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(
            fixture.KitBagItemId!.Value,
            state.EquipmentItemId,
            "ring template slot eight may equip in slot nine");
        AssertCommittedEvidence(state, "second ring");
    }

    private static void AssertCommittedEvidence(
        TransferDurableState state,
        string description)
    {
        Check.Equal(
            0L,
            state.TemporaryItemCount,
            $"{description} creates no temporary item");
        Check.Equal(1L, state.AuditCount, $"{description} audit");
        Check.Equal(1L, state.InboxCount, $"{description} inbox");
        Check.Equal(
            1L,
            state.CompatibilityAuditCount,
            $"{description} compatibility audit");
        Check.Equal(1L, state.LedgerCount, $"{description} ledger");
        Check.Equal(1L, state.OutboxCount, $"{description} outbox");
        Check.True(
            state.IsReconciled,
            $"{description} inventory reconciles");
    }
}
