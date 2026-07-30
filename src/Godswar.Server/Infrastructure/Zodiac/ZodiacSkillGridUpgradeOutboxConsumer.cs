using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Zodiac;

/// <summary>
/// Validates successful Zodiac grid-upgrade projection events. PostgreSQL
/// remains authoritative; versioned-state delivery lets a projection apply
/// the newest complete grid state without requiring every intermediate level.
/// </summary>
internal sealed class ZodiacSkillGridUpgradeOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        ZodiacSkillGridUpgradePersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.VersionedState;

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
                ZodiacSkillGridUpgradePersistenceCodec.EventAggregateType,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                ZodiacSkillGridUpgradePersistenceCodec.EventType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                ZodiacSkillGridUpgradePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The Zodiac grid-upgrade outbox contract is unsupported.");
        }

        var receipt = ZodiacSkillGridUpgradePersistenceCodec.Decode(
            message.Payload.Span);
        if (!receipt.Succeeded ||
            receipt.OutboxEventId != message.EventId ||
            receipt.AggregateRevision != message.AggregateRevision ||
            !string.Equals(
                ZodiacSkillGridUpgradePersistenceCodec.EventAggregateKey(
                    receipt.CharacterId,
                    receipt.GridIndex),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Zodiac grid-upgrade outbox identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
