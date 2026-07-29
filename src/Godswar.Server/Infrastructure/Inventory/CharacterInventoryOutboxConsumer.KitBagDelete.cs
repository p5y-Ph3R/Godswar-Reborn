using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidateKitBagItemDelete(
        OutboxEventMessage message)
    {
        var receipt = KitBagItemDeletePersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status !=
                KitBagItemDeleteResultStatus.Deleted ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                KitBagItemDeletePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The kit-bag delete outbox identity is inconsistent.");
        }
    }
}
