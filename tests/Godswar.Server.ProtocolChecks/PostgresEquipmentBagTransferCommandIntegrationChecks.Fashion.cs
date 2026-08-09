using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task AssertPermanentFashionRoundTripAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fashion",
            kitBagItem: Item(8068, quality: 20, grade: 25),
            equipmentSlot: EquipmentSlots.Stylish,
            kitBagSlot: 23);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var equipReceipt = RequireReceipt(
            await ExecuteAsync(executor, fixture, Guid.NewGuid()),
            EquipmentBagTransferDisposition.Committed,
            EquipmentBagTransferResultStatus.Equipped,
            "permanent fashion equip");
        Check.Equal(
            EquipmentSlots.Stylish,
            equipReceipt.EquipmentSlot,
            "fashion equip receipt retains native slot 12");
        var equipped = await ReadStateAsync(connectionString, fixture);
        Check.Equal(1L, equipped.InventoryRevision, "fashion equip revision");
        Check.Equal(
            fixture.KitBagItemId!.Value,
            equipped.EquipmentItemId,
            "fashion equip preserves durable item identity");
        Check.Equal(0L, equipped.KitBagItemId, "fashion equip clears bag slot");
        AssertCommittedEvidence(equipped, "permanent fashion equip");

        var unequipReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                fixture,
                Guid.NewGuid(),
                expectedEquipment: fixture.KitBagState,
                expectedKitBag: "[]"),
            EquipmentBagTransferDisposition.Committed,
            EquipmentBagTransferResultStatus.Unequipped,
            "permanent fashion unequip");
        Check.Equal(
            EquipmentSlots.Stylish,
            unequipReceipt.EquipmentSlot,
            "fashion unequip receipt retains native slot 12");
        var unequipped = await ReadStateAsync(connectionString, fixture);
        Check.Equal(2L, unequipped.InventoryRevision, "fashion round-trip revision");
        Check.Equal(0L, unequipped.EquipmentItemId, "fashion unequip clears slot 12");
        Check.Equal(
            fixture.KitBagItemId.Value,
            unequipped.KitBagItemId,
            "fashion unequip returns the same item to the requested bag slot");
        Check.Equal(2L, unequipped.AuditCount, "fashion round-trip audit");
        Check.Equal(2L, unequipped.InboxCount, "fashion round-trip inbox");
        Check.Equal(
            2L,
            unequipped.CompatibilityAuditCount,
            "fashion round-trip item audit");
        Check.Equal(2L, unequipped.LedgerCount, "fashion round-trip ledger");
        Check.Equal(2L, unequipped.OutboxCount, "fashion round-trip outbox");
        Check.True(
            unequipped.IsReconciled,
            "fashion round-trip inventory remains reconciled");
    }
}
