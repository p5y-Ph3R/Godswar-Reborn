using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidatePetBagActivation(
        OutboxEventMessage message)
    {
        var receipt =
            PetBagActivationInventoryPersistenceCodec.Decode(
                message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                PetBagActivationInventoryPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The pet bag inventory outbox identity is inconsistent.");
        }
    }
}
