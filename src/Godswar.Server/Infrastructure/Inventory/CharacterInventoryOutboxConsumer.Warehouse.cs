using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure.Warehouse;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidateWarehouseTransfer(
        OutboxEventMessage message)
    {
        var receipt = WarehouseTransferPersistenceCodec.Decode(
            message.Payload.Span);
        if (!receipt.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                WarehouseTransferPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The warehouse transfer outbox identity is inconsistent.");
        }
    }

    private static void ValidateWarehouseKeyConsumption(
        OutboxEventMessage message)
    {
        var receipt = WarehouseExpansionPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status != WarehouseExpansionResultStatus.Expanded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                WarehouseExpansionPersistenceCodec.InventoryAggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The warehouse key-consumption outbox identity is " +
                "inconsistent.");
        }
    }
}
