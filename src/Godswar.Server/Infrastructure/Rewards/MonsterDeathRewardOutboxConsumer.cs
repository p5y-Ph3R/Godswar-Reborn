using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed class MonsterDeathRewardOutboxConsumer :
    IOutboxEventConsumer
{
    public string ConsumerKey =>
        MonsterDeathRewardPersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.StrictSequence;

    public ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ConsumerKey != ConsumerKey ||
            message.AggregateType !=
                MonsterDeathRewardPersistenceCodec.AggregateType ||
            message.EventType !=
                MonsterDeathRewardPersistenceCodec.EventType ||
            message.SchemaVersion !=
                MonsterDeathRewardPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "Monster reward outbox routing metadata is invalid.");
        }

        var receipt = MonsterDeathRewardPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.OutboxEventId != message.EventId ||
            receipt.ProgressionRevision != message.AggregateRevision ||
            !string.Equals(
                MonsterDeathRewardPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Monster reward outbox identity is invalid.");
        }
        return ValueTask.CompletedTask;
    }
}
