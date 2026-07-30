using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

/// <summary>
/// Validates and checkpoints committed inventory events. PostgreSQL remains
/// authoritative; this consumer deliberately creates no second player-value
/// owner.
/// </summary>
internal sealed partial class CharacterInventoryOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        DeveloperItemGrantPersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.StrictSequence;

    public ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                message.ConsumerKey,
                ConsumerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.AggregateType,
                DeveloperItemGrantPersistenceCodec.AggregateType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The inventory outbox event contract is " +
                "unsupported.");
        }

        if (IsItemGrantEvent(message.EventType) &&
            message.SchemaVersion ==
                DeveloperItemGrantPersistenceCodec.ContractVersion)
        {
            ValidateGrant(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                DeveloperBagClearPersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                DeveloperBagClearPersistenceCodec.ContractVersion)
        {
            ValidateBagClear(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                MakeAttributeStonePersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                MakeAttributeStonePersistenceCodec.ContractVersion)
        {
            ValidateMakeAttributeStone(message);
            return ValueTask.CompletedTask;
        }

        if (IsMaterialConversionEvent(message.EventType) &&
            message.SchemaVersion ==
                GearMentorMaterialConversionPersistenceCodec
                    .ContractVersion)
        {
            ValidateMaterialConversion(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                GearMentorDecomposePersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                GearMentorDecomposePersistenceCodec.ContractVersion)
        {
            ValidateDecompose(message);
            return ValueTask.CompletedTask;
        }

        if (GearEnhancementPersistenceCodec.IsEventType(
                message.EventType) &&
            message.SchemaVersion ==
                GearEnhancementPersistenceCodec.ContractVersion)
        {
            ValidateGearEnhancement(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                EquipmentForgePersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                EquipmentForgePersistenceCodec.ContractVersion)
        {
            ValidateEquipmentForge(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                KitBagItemDeletePersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                KitBagItemDeletePersistenceCodec.ContractVersion)
        {
            ValidateKitBagItemDelete(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                KitBagItemMovePersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                KitBagItemMovePersistenceCodec.ContractVersion)
        {
            ValidateKitBagItemMove(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                EquipmentBagTransferPersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                EquipmentBagTransferPersistenceCodec.ContractVersion)
        {
            ValidateEquipmentBagTransfer(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                HolyStonePersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                HolyStonePersistenceCodec.ContractVersion)
        {
            ValidateHolyStone(message);
            return ValueTask.CompletedTask;
        }

        if (string.Equals(
                message.EventType,
                PetBagActivationInventoryPersistenceCodec.EventType,
                StringComparison.Ordinal) &&
            message.SchemaVersion ==
                PetBagActivationInventoryPersistenceCodec.ContractVersion)
        {
            ValidatePetBagActivation(message);
            return ValueTask.CompletedTask;
        }

        throw new InvalidDataException(
            "The inventory outbox event contract is unsupported.");
    }

    private static void ValidateGrant(OutboxEventMessage message)
    {
        var receipt = DeveloperItemGrantPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                DeveloperItemGrantPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The inventory grant outbox identity is inconsistent.");
        }
    }

    private static bool IsItemGrantEvent(string eventType) =>
        string.Equals(
            eventType,
            DeveloperItemGrantPersistenceCodec.EventType,
            StringComparison.Ordinal) ||
        string.Equals(
            eventType,
            DeveloperItemGrantPersistenceCodec.LegacyMaterialEventType,
            StringComparison.Ordinal);

    private static void ValidateBagClear(OutboxEventMessage message)
    {
        var receipt = DeveloperBagClearPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                DeveloperBagClearPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The bag-clear outbox identity is inconsistent.");
        }
    }

    private static void ValidateMakeAttributeStone(
        OutboxEventMessage message)
    {
        var receipt = MakeAttributeStonePersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status !=
                MakeAttributeStoneResultStatus.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                MakeAttributeStonePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Make Attribute Stone outbox identity is " +
                "inconsistent.");
        }
    }

    private static bool IsMaterialConversionEvent(string eventType) =>
        string.Equals(
            eventType,
            GearMentorMaterialConversionPersistenceCodec
                .TransformEventType,
            StringComparison.Ordinal) ||
        string.Equals(
            eventType,
            GearMentorMaterialConversionPersistenceCodec
                .CombineEventType,
            StringComparison.Ordinal);

    private static void ValidateMaterialConversion(
        OutboxEventMessage message)
    {
        var receipt =
            GearMentorMaterialConversionPersistenceCodec.Decode(
                message.Payload.Span);
        if (receipt.Status !=
                GearMentorMaterialConversionResultStatus.Succeeded ||
            !string.Equals(
                GearMentorMaterialConversionPersistenceCodec.EventType(
                    receipt.Family),
                message.EventType,
                StringComparison.Ordinal) ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                GearMentorMaterialConversionPersistenceCodec
                    .AggregateKey(receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The material-conversion outbox identity is " +
                "inconsistent.");
        }
    }

    private static void ValidateDecompose(OutboxEventMessage message)
    {
        var receipt = GearMentorDecomposePersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status !=
                GearMentorDecomposeGearResultStatus.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                GearMentorDecomposePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Decompose outbox identity is inconsistent.");
        }
    }

    private static void ValidateGearEnhancement(
        OutboxEventMessage message)
    {
        var receipt = GearEnhancementPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status !=
                GearEnhancementCommandResultStatus.Succeeded ||
            !string.Equals(
                GearEnhancementPersistenceCodec.EventType(receipt.Family),
                message.EventType,
                StringComparison.Ordinal) ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                GearEnhancementPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Gear Enhancement outbox identity is inconsistent.");
        }
    }
}
