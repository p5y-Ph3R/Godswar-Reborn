using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static void CheckEquipmentBagTransferStreamIdentity(
        CharacterInventoryOutboxConsumer consumer)
    {
        Check.Equal(
            consumer.ConsumerKey,
            EquipmentBagTransferPersistenceCodec.ConsumerKey,
            "equipment transfer shares the inventory checkpoint");
        Check.Equal(
            DeveloperItemGrantPersistenceCodec.AggregateType,
            EquipmentBagTransferPersistenceCodec.AggregateType,
            "equipment transfer shares the inventory aggregate type");
        Check.Equal(
            DeveloperItemGrantPersistenceCodec.AggregateKey(CharacterId),
            EquipmentBagTransferPersistenceCodec.AggregateKey(CharacterId),
            "equipment transfer shares the inventory aggregate stream");
    }

    private static async Task CheckEquipmentBagTransferAsync(
        CharacterInventoryOutboxConsumer consumer)
    {
        var item = (CompactItemEntry.Empty with
        {
            Id = 4_215,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 1
        }).ToCompactString();

        foreach (var (status, equipment, kitBag, revision) in
                 new[]
                 {
                     (
                         EquipmentBagTransferResultStatus.Equipped,
                         "[]",
                         item,
                         14L),
                     (
                         EquipmentBagTransferResultStatus.Unequipped,
                         item,
                         "[]",
                         15L)
                 })
        {
            var eventId = Guid.NewGuid();
            var receipt = new EquipmentBagTransferExecutionReceipt(
                CharacterId,
                equipmentSlot: 10,
                kitBagSlot: 18,
                status,
                equipment,
                kitBag,
                equipment,
                kitBag,
                revision,
                $"equipment-bag-transfer:{revision}",
                eventId);
            var message = CreateEquipmentBagTransferMessage(
                receipt,
                eventId);

            await consumer.ConsumeAsync(message);

            await CheckThrowsAsync<InvalidDataException>(
                () => consumer.ConsumeAsync(
                    CopyEquipmentBagTransferMessage(
                        message,
                        eventId: Guid.NewGuid())).AsTask(),
                "equipment transfer rejects a mismatched event");
            await CheckThrowsAsync<InvalidDataException>(
                () => consumer.ConsumeAsync(
                    CopyEquipmentBagTransferMessage(
                        message,
                        revision: revision + 1)).AsTask(),
                "equipment transfer rejects a mismatched revision");
            await CheckThrowsAsync<InvalidDataException>(
                () => consumer.ConsumeAsync(
                    CopyEquipmentBagTransferMessage(
                        message,
                        aggregateKey:
                            EquipmentBagTransferPersistenceCodec
                                .AggregateKey(CharacterId + 1))).AsTask(),
                "equipment transfer rejects a mismatched aggregate");
        }

        var rejected = new EquipmentBagTransferExecutionReceipt(
            CharacterId,
            equipmentSlot: 10,
            kitBagSlot: 18,
            EquipmentBagTransferResultStatus.BothEmpty,
            "[]",
            "[]",
            "[]",
            "[]",
            inventoryRevision: 15,
            auditReference: "equipment-bag-transfer:rejected",
            outboxEventId: null);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CreateEquipmentBagTransferMessage(
                    rejected,
                    Guid.NewGuid())).AsTask(),
            "equipment transfer rejects a non-mutating event");
    }

    private static OutboxEventMessage
        CreateEquipmentBagTransferMessage(
            EquipmentBagTransferExecutionReceipt receipt,
            Guid eventId) =>
        new(
            eventId,
            EquipmentBagTransferPersistenceCodec.ConsumerKey,
            EquipmentBagTransferPersistenceCodec.AggregateType,
            EquipmentBagTransferPersistenceCodec.AggregateKey(
                CharacterId),
            receipt.InventoryRevision,
            EquipmentBagTransferPersistenceCodec.EventType,
            EquipmentBagTransferPersistenceCodec.ContractVersion,
            DateTimeOffset.UtcNow,
            EquipmentBagTransferPersistenceCodec.Encode(receipt));

    private static OutboxEventMessage
        CopyEquipmentBagTransferMessage(
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
