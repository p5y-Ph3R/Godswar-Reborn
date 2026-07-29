using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidateKitBagItemMove(
        OutboxEventMessage message)
    {
        var receipt = KitBagItemMovePersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status is not (
                KitBagItemMoveResultStatus.Moved or
                KitBagItemMoveResultStatus.Swapped) ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                KitBagItemMovePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The kit-bag move outbox identity is inconsistent.");
        }
    }
}
