using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static async Task CheckHolyStoneAsync(
        CharacterInventoryOutboxConsumer consumer)
    {
        Check.Equal(
            consumer.ConsumerKey,
            HolyStonePersistenceCodec.ConsumerKey,
            "Holy Stone shares the inventory checkpoint");
        Check.Equal(
            DeveloperItemGrantPersistenceCodec.AggregateKey(CharacterId),
            HolyStonePersistenceCodec.AggregateKey(CharacterId),
            "Holy Stone shares the inventory aggregate");

        var eventId = Guid.NewGuid();
        var before = CompactItemEntry.Empty with
        {
            Id = 1007,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1,
            SocketCount = 1
        };
        var after = before with
        {
            Socket1EffectId = 1,
            Socket1Level = 7
        };
        var stone = CompactItemEntry.Empty with
        {
            Id = 9060,
            Quality = 1,
            Grade = 7,
            Bound = 1,
            Stack = 2
        };
        // Historical version-2 outbox events may identify the equipped
        // weapon. The consumer must remain able to project those events even
        // though current stock-client Holy Stone commands target the bag.
        var receipt = new HolyStoneExecutionReceipt(
            CharacterId,
            HolyStoneCommandOperation.Mount,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.Mounted,
            HolyStoneNativeResults.MountedSubId,
            HolyStoneTargetLocation.Equipment,
            HolyStoneCommandEnvelope.WeaponEquipmentSlot,
            socketIndex: 0,
            targetItemInstanceId: 101,
            before.ToCompactString(),
            before.ToCompactString(),
            after.ToCompactString(),
            stoneKitBagSlot: 11,
            stoneItemInstanceId: 102,
            stone.ToCompactString(),
            stone.ToCompactString(),
            (stone with { Stack = 1 }).ToCompactString(),
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 777,
            goldAfter: 777,
            walletRevision: 3,
            inventoryRevision: 16,
            auditReference: "holy-stone:16",
            eventId);
        var message = new OutboxEventMessage(
            eventId,
            HolyStonePersistenceCodec.ConsumerKey,
            HolyStonePersistenceCodec.AggregateType,
            HolyStonePersistenceCodec.AggregateKey(CharacterId),
            receipt.InventoryRevision,
            HolyStonePersistenceCodec.EventType,
            HolyStonePersistenceCodec.ContractVersion,
            DateTimeOffset.UtcNow,
            HolyStonePersistenceCodec.Encode(receipt));
        await consumer.ConsumeAsync(message);

        var advancedEventId = Guid.NewGuid();
        var advancedBefore = before with { SocketCount = 2 };
        var advancedAfter = before with { SocketCount = 3 };
        var socketSpell = CompactItemEntry.Empty with
        {
            Id = 4272,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 2
        };
        var advancedReceipt = new HolyStoneExecutionReceipt(
            CharacterId,
            HolyStoneCommandOperation.AdvancedDrill,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.Drilled,
            HolyStoneNativeResults.DrilledSubId,
            HolyStoneTargetLocation.KitBag,
            targetSlot: 16,
            socketIndex: 2,
            targetItemInstanceId: 201,
            advancedBefore.ToCompactString(),
            advancedBefore.ToCompactString(),
            advancedAfter.ToCompactString(),
            stoneKitBagSlot: 11,
            stoneItemInstanceId: 202,
            socketSpell.ToCompactString(),
            socketSpell.ToCompactString(),
            (socketSpell with { Stack = 1 }).ToCompactString(),
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 777,
            goldAfter: 777,
            walletRevision: 3,
            inventoryRevision: 17,
            auditReference: "holy-stone-advanced:17",
            advancedEventId);
        await consumer.ConsumeAsync(new OutboxEventMessage(
            advancedEventId,
            HolyStonePersistenceCodec.ConsumerKey,
            HolyStonePersistenceCodec.AggregateType,
            HolyStonePersistenceCodec.AggregateKey(CharacterId),
            advancedReceipt.InventoryRevision,
            HolyStonePersistenceCodec.EventType,
            HolyStonePersistenceCodec.ContractVersion,
            DateTimeOffset.UtcNow,
            HolyStonePersistenceCodec.Encode(advancedReceipt)));

        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyHolyStoneMessage(
                    message,
                    eventId: Guid.NewGuid())).AsTask(),
            "Holy Stone rejects a mismatched event");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyHolyStoneMessage(
                    message,
                    revision: receipt.InventoryRevision + 1)).AsTask(),
            "Holy Stone rejects a mismatched revision");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyHolyStoneMessage(
                    message,
                    aggregateKey:
                        HolyStonePersistenceCodec.AggregateKey(
                            CharacterId + 1))).AsTask(),
            "Holy Stone rejects a mismatched aggregate");
    }

    private static OutboxEventMessage CopyHolyStoneMessage(
        OutboxEventMessage source,
        Guid? eventId = null,
        long? revision = null,
        string? aggregateKey = null) =>
        new(
            eventId ?? source.EventId,
            source.ConsumerKey,
            source.AggregateType,
            aggregateKey ?? source.AggregateKey,
            revision ?? source.AggregateRevision,
            source.EventType,
            source.SchemaVersion,
            source.OccurredAtUtc,
            source.Payload);
}
