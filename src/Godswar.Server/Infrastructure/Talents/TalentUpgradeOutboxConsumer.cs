using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Talents;

/// <summary>
/// B08's first concrete sink validates the committed talent event contract.
/// The authoritative mutation is already in PostgreSQL; later projections
/// receive their own consumer destination rather than becoming a second
/// authority.
/// </summary>
internal sealed class TalentUpgradeOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        TalentUpgradePersistenceCodec.ConsumerKey;

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
                TalentUpgradePersistenceCodec.AggregateType,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.EventType,
                TalentUpgradePersistenceCodec.EventType,
                StringComparison.Ordinal) ||
            message.SchemaVersion !=
                TalentUpgradePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The talent outbox event contract is unsupported.");
        }

        var payload = message.Payload;
        var receipt =
            TalentUpgradePersistenceCodec.Decode(payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.AggregateRevision !=
                message.AggregateRevision ||
            !string.Equals(
                TalentUpgradePersistenceCodec.AggregateKey(
                    receipt.CharacterId,
                    receipt.TalentId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The talent outbox event identity is inconsistent.");
        }

        return ValueTask.CompletedTask;
    }
}
