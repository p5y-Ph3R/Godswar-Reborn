namespace Godswar.Server.Application.Messaging;

/// <summary>
/// Handles one outbox event after dispatcher ordering checks. Returning
/// successfully means the event may be checkpointed; throwing leaves it
/// eligible for bounded retry. Consumers must tolerate at-least-once delivery.
/// Implementations must observe the supplied cancellation token and must not
/// start unbounded background work that outlives the callback.
/// </summary>
internal interface IOutboxEventConsumer
{
    string ConsumerKey { get; }

    OutboxOrderingPolicy OrderingPolicy { get; }

    ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken = default);
}

internal static class OutboxConsumerContract
{
    public const int MaximumConsumerKeyBytes = 64;

    public static string RequireKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumConsumerKeyBytes ||
            value[0] is not (>= 'a' and <= 'z') ||
            value.Skip(1).Any(static character =>
                character is not (
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '_' or '.' or '-')))
        {
            throw new ArgumentException(
                "Consumer keys must be 1-64 lowercase ASCII identifier " +
                "characters.",
                nameof(value));
        }

        return value;
    }
}
