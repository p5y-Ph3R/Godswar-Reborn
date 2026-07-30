using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Zodiac;

/// <summary>
/// Validates the one-shot activation event. PostgreSQL remains authoritative;
/// future projections must use their own consumer rather than becoming a
/// second owner of Zodiac state.
/// </summary>
internal sealed class ZodiacSkillGridActivationOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        ZodiacSkillGridActivationPersistenceCodec.ConsumerKey;

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
                ZodiacSkillGridActivationPersistenceCodec.AggregateType,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                ZodiacSkillGridActivationPersistenceCodec.EventType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                ZodiacSkillGridActivationPersistenceCodec.ContractVersion ||
            message.AggregateRevision !=
                ZodiacSkillGridActivationPersistenceCodec.AggregateRevision)
        {
            throw new InvalidDataException(
                "The Zodiac activation outbox contract is unsupported.");
        }

        var receipt =
            ZodiacSkillGridActivationPersistenceCodec.Decode(
                message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            !string.Equals(
                ZodiacSkillGridActivationPersistenceCodec.AggregateKey(
                    receipt.CharacterId,
                    receipt.GridIndex),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Zodiac activation outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
