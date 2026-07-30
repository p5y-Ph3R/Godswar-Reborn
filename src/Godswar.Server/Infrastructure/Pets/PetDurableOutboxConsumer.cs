using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed class PetDurableOutboxConsumer : IOutboxEventConsumer
{
    public string ConsumerKey =>
        PetDurablePersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.StrictSequence;

    public ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var receipt =
            PetDurablePersistenceCodec.Decode(message.Payload.Span);
        if (!receipt.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.AggregateRevision != message.AggregateRevision ||
            message.SchemaVersion !=
                PetDurablePersistenceCodec.ContractVersion ||
            !string.Equals(
                message.ConsumerKey,
                ConsumerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.AggregateType,
                PetDurablePersistenceCodec.AggregateType,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.AggregateKey,
                PetDurablePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                PetDurablePersistenceCodec.EventType(receipt.Family),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The pet durable outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
