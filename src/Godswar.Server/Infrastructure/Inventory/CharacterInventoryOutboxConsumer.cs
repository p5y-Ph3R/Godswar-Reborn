using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

/// <summary>
/// Validates and checkpoints committed inventory events. PostgreSQL remains
/// authoritative; this consumer deliberately creates no second player-value
/// owner.
/// </summary>
internal sealed class CharacterInventoryOutboxConsumer :
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
}
