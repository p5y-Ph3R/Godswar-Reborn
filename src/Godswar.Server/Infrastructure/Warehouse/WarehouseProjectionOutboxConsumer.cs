using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Warehouse;

namespace Godswar.Server.Infrastructure.Warehouse;

/// <summary>
/// Validates the capacity side of a committed expansion. PostgreSQL is the
/// sole state owner; delivery checkpoints the ordered evidence only.
/// </summary>
internal sealed class WarehouseProjectionOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        WarehouseExpansionPersistenceCodec.WarehouseConsumerKey;

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
                WarehouseExpansionPersistenceCodec.WarehouseAggregateType,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                WarehouseExpansionPersistenceCodec.WarehouseEventType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                WarehouseExpansionPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The warehouse capacity outbox contract is unsupported.");
        }

        var receipt = WarehouseExpansionPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.Status != WarehouseExpansionResultStatus.Expanded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.WarehouseRevision != message.AggregateRevision ||
            !string.Equals(
                WarehouseExpansionPersistenceCodec.WarehouseAggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The warehouse capacity outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
