using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidateEquipmentBagTransfer(
        OutboxEventMessage message)
    {
        var receipt = EquipmentBagTransferPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status is not (
                EquipmentBagTransferResultStatus.Equipped or
                EquipmentBagTransferResultStatus.Unequipped) ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                EquipmentBagTransferPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The equipment/bag transfer outbox identity is " +
                "inconsistent.");
        }
    }
}
