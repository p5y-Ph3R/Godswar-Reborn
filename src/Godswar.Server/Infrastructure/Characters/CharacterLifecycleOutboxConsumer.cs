using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Characters;

/// <summary>
/// Validates strict lifecycle events before the dispatcher advances its
/// durable account-slot position. PostgreSQL remains the authoritative
/// projection; downstream integrations can replace this validation-only
/// consumer without changing command atomicity.
/// </summary>
internal sealed class CharacterLifecycleOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        CharacterLifecyclePersistenceCodec.ConsumerKey;

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
                CharacterLifecyclePersistenceCodec.AggregateType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                CharacterLifecyclePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The character lifecycle outbox contract is unsupported.");
        }

        var receipt =
            CharacterLifecyclePersistenceCodec.Decode(message.Payload.Span);
        if (!receipt.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.LifecycleVersion != message.AggregateRevision ||
            !string.Equals(
                CharacterLifecyclePersistenceCodec.AggregateKey(
                    receipt.AccountId,
                    receipt.CharacterSlot),
                message.AggregateKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                CharacterLifecyclePersistenceCodec.EventType(receipt.Family),
                message.EventType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The character lifecycle outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
