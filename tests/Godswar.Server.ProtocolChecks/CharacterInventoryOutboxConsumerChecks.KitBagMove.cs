using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static async Task CheckKitBagItemMoveAsync(
        CharacterInventoryOutboxConsumer consumer)
    {
        var source = (CompactItemEntry.Empty with
        {
            Id = 4_215,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 9
        }).ToCompactString();
        var destination = (CompactItemEntry.Empty with
        {
            Id = 4_230,
            Quality = 2,
            Grade = 4,
            Bound = 1,
            Stack = 2
        }).ToCompactString();

        foreach (var (status, destinationState, revision) in
                 new[]
                 {
                     (
                         KitBagItemMoveResultStatus.Moved,
                         "[]",
                         12L),
                     (
                         KitBagItemMoveResultStatus.Swapped,
                         destination,
                         13L)
                 })
        {
            var eventId = Guid.NewGuid();
            var receipt = new KitBagItemMoveExecutionReceipt(
                CharacterId,
                sourceKitBagSlot: 18,
                destinationKitBagSlot: 19,
                status,
                source,
                destinationState,
                source,
                destinationState,
                revision,
                $"kit-bag-move:{revision}",
                eventId);
            var message = CreateKitBagMoveMessage(
                receipt,
                eventId);

            await consumer.ConsumeAsync(message);

            await CheckThrowsAsync<InvalidDataException>(
                () => consumer.ConsumeAsync(
                    CopyKitBagMoveMessage(
                        message,
                        eventId: Guid.NewGuid())).AsTask(),
                "kit-bag move outbox rejects mismatched event");
            await CheckThrowsAsync<InvalidDataException>(
                () => consumer.ConsumeAsync(
                    CopyKitBagMoveMessage(
                        message,
                        revision: revision + 1)).AsTask(),
                "kit-bag move outbox rejects mismatched revision");
            await CheckThrowsAsync<InvalidDataException>(
                () => consumer.ConsumeAsync(
                    CopyKitBagMoveMessage(
                        message,
                        aggregateKey:
                            KitBagItemMovePersistenceCodec.AggregateKey(
                                CharacterId + 1))).AsTask(),
                "kit-bag move outbox rejects mismatched aggregate");
        }

        var rejected = new KitBagItemMoveExecutionReceipt(
            CharacterId,
            sourceKitBagSlot: 18,
            destinationKitBagSlot: 19,
            KitBagItemMoveResultStatus.EmptySource,
            "[]",
            destination,
            "[]",
            destination,
            inventoryRevision: 13,
            auditReference: "kit-bag-move:rejected",
            outboxEventId: null);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(
                CreateKitBagMoveMessage(
                    rejected,
                    Guid.NewGuid())).AsTask(),
            "kit-bag move outbox rejects terminal non-mutation");
    }

    private static OutboxEventMessage CreateKitBagMoveMessage(
        KitBagItemMoveExecutionReceipt receipt,
        Guid eventId) =>
        new(
            eventId,
            KitBagItemMovePersistenceCodec.ConsumerKey,
            KitBagItemMovePersistenceCodec.AggregateType,
            KitBagItemMovePersistenceCodec.AggregateKey(CharacterId),
            receipt.InventoryRevision,
            KitBagItemMovePersistenceCodec.EventType,
            KitBagItemMovePersistenceCodec.ContractVersion,
            DateTimeOffset.UtcNow,
            KitBagItemMovePersistenceCodec.Encode(receipt));

    private static OutboxEventMessage CopyKitBagMoveMessage(
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
