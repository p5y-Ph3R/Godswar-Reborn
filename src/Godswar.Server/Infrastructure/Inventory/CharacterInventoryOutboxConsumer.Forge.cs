using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidateEquipmentForge(
        OutboxEventMessage message)
    {
        var receipt = EquipmentForgePersistenceCodec.Decode(
            message.Payload.Span);
        if (!receipt.Committed ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                EquipmentForgePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The equipment-forge outbox identity is inconsistent.");
        }
    }
}
