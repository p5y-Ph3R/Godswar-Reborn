using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed class ProgressionIntervalSettlementOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        ProgressionIntervalSettlementPersistenceCodec.ConsumerKey;

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
                ProgressionIntervalSettlementPersistenceCodec.AggregateType,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                ProgressionIntervalSettlementPersistenceCodec.EventType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                ProgressionIntervalSettlementPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The progression interval outbox contract is unsupported.");
        }

        var receipt =
            ProgressionIntervalSettlementPersistenceCodec.Decode(
                message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.Projection.AggregateRevision !=
                message.AggregateRevision ||
            !string.Equals(
                ProgressionIntervalSettlementPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The progression interval outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
