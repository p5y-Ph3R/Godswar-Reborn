using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed class ZodiacSkillGridSelectionOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        ZodiacSkillGridSelectionPersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.VersionedState;

    public ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ConsumerKey != ConsumerKey ||
            message.AggregateType !=
                ZodiacSkillGridSelectionPersistenceCodec.AggregateType ||
            message.EventType !=
                ZodiacSkillGridSelectionPersistenceCodec.EventType ||
            message.SchemaVersion !=
                ZodiacSkillGridSelectionPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The Zodiac selection outbox contract is unsupported.");
        }

        var receipt =
            ZodiacSkillGridSelectionPersistenceCodec.Decode(
                message.Payload.Span);
        if (!receipt.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.AggregateRevision != message.AggregateRevision ||
            message.AggregateKey !=
                ZodiacSkillGridSelectionPersistenceCodec
                    .EventAggregateKey(
                        receipt.CharacterId,
                        receipt.GridIndex))
        {
            throw new InvalidDataException(
                "The Zodiac selection outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
