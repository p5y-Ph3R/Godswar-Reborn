using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static async Task CheckKitBagItemDeleteAsync(
        CharacterInventoryOutboxConsumer consumer)
    {
        var eventId = Guid.NewGuid();
        var item = CompactItemEntry.Empty with
        {
            Id = 4_215,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 9
        };
        var receipt = new KitBagItemDeleteExecutionReceipt(
            CharacterId,
            kitBagSlot: 18,
            KitBagItemDeleteResultStatus.Deleted,
            item.ToCompactString(),
            item.ToCompactString(),
            inventoryRevision: 11,
            auditReference: "kit-bag-delete:11",
            eventId);
        var message = new OutboxEventMessage(
            eventId,
            KitBagItemDeletePersistenceCodec.ConsumerKey,
            KitBagItemDeletePersistenceCodec.AggregateType,
            KitBagItemDeletePersistenceCodec.AggregateKey(CharacterId),
            receipt.InventoryRevision,
            KitBagItemDeletePersistenceCodec.EventType,
            KitBagItemDeletePersistenceCodec.ContractVersion,
            DateTimeOffset.UtcNow,
            KitBagItemDeletePersistenceCodec.Encode(receipt));

        await consumer.ConsumeAsync(message);

        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyKitBagDeleteMessage(
                    message,
                    eventId: Guid.NewGuid())).AsTask(),
            "kit-bag delete outbox rejects a mismatched event identity");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyKitBagDeleteMessage(
                    message,
                    revision:
                        message.AggregateRevision + 1)).AsTask(),
            "kit-bag delete outbox rejects a mismatched revision");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CopyKitBagDeleteMessage(
                    message,
                    aggregateKey:
                        KitBagItemDeletePersistenceCodec.AggregateKey(
                            CharacterId + 1))).AsTask(),
            "kit-bag delete outbox rejects a mismatched aggregate");
    }

    private static OutboxEventMessage CopyKitBagDeleteMessage(
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
