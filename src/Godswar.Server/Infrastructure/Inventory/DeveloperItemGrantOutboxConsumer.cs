using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

/// <summary>
/// Validates and checkpoints the committed inventory grant event. PostgreSQL
/// remains authoritative; this first consumer deliberately creates no second
/// player-value owner.
/// </summary>
internal sealed class DeveloperItemGrantOutboxConsumer :
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
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                DeveloperItemGrantPersistenceCodec.EventType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                DeveloperItemGrantPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The inventory grant outbox event contract is " +
                "unsupported.");
        }

        var receipt =
            DeveloperItemGrantPersistenceCodec.Decode(
                message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision !=
                message.AggregateRevision ||
            !string.Equals(
                DeveloperItemGrantPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The inventory grant outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
