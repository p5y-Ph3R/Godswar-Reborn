using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static async Task CheckEquipmentForgeAsync(
        CharacterInventoryOutboxConsumer consumer)
    {
        var eventId = Guid.NewGuid();
        var before = CompactItemEntry.Parse(
            "[1000,,,,,,1,1,0,1,0,0,,,,,,0,,,,,,,,,,,,]");
        var after = before with { Quality = 2 };
        var receipt = new EquipmentForgeExecutionReceipt(
            CharacterId,
            EquipmentForgeCommandResultStatus.Succeeded,
            materialType: 2,
            roll: 7,
            successProbability: 58,
            silverSpent: 1,
            before.ToCompactString(),
            after.ToCompactString(),
            [
                new EquipmentForgeReceiptMaterial(
                    EquipmentForgeCommandItemRole.PrimaryMaterial,
                    18,
                    4212,
                    1,
                    2,
                    1)
            ],
            walletRevision: 3,
            inventoryRevision: 9,
            auditReference: "forge:9",
            eventId);
        var message = new OutboxEventMessage(
            eventId,
            EquipmentForgePersistenceCodec.ConsumerKey,
            EquipmentForgePersistenceCodec.AggregateType,
            EquipmentForgePersistenceCodec.AggregateKey(CharacterId),
            receipt.InventoryRevision,
            EquipmentForgePersistenceCodec.EventType,
            EquipmentForgePersistenceCodec.ContractVersion,
            DateTimeOffset.UtcNow,
            EquipmentForgePersistenceCodec.Encode(receipt));
        await consumer.ConsumeAsync(message);

        var failedEventId = Guid.NewGuid();
        var failedReceipt = new EquipmentForgeExecutionReceipt(
            CharacterId,
            EquipmentForgeCommandResultStatus.FailedRoll,
            materialType: 2,
            roll: 99,
            successProbability: 58,
            silverSpent: 1,
            before.ToCompactString(),
            before.ToCompactString(),
            receipt.Materials,
            walletRevision: 4,
            inventoryRevision: 10,
            auditReference: "forge:10",
            failedEventId);
        await consumer.ConsumeAsync(
            new OutboxEventMessage(
                failedEventId,
                EquipmentForgePersistenceCodec.ConsumerKey,
                EquipmentForgePersistenceCodec.AggregateType,
                EquipmentForgePersistenceCodec.AggregateKey(CharacterId),
                failedReceipt.InventoryRevision,
                EquipmentForgePersistenceCodec.EventType,
                EquipmentForgePersistenceCodec.ContractVersion,
                DateTimeOffset.UtcNow,
                EquipmentForgePersistenceCodec.Encode(failedReceipt)));

        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyForgeMessage(
                    message,
                    eventId: Guid.NewGuid())).AsTask(),
            "equipment-forge outbox rejects a mismatched event identity");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyForgeMessage(
                    message,
                    revision:
                        message.AggregateRevision + 1)).AsTask(),
            "equipment-forge outbox rejects a mismatched revision");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyForgeMessage(
                    message,
                    aggregateKey:
                        EquipmentForgePersistenceCodec.AggregateKey(
                            CharacterId + 1))).AsTask(),
            "equipment-forge outbox rejects a mismatched aggregate");
    }

    private static OutboxEventMessage CopyForgeMessage(
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
