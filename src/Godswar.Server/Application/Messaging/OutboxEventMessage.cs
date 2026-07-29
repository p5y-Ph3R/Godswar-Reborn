using System.Text;

namespace Godswar.Server.Application.Messaging;

/// <summary>
/// Provider-neutral, immutable application event loaded from a durable outbox.
/// The payload is copied at construction so a producer cannot mutate it after
/// publication.
/// </summary>
internal sealed class OutboxEventMessage
{
    public const int MaximumAggregateTypeBytes = 32;
    public const int MaximumAggregateKeyBytes = 128;
    public const int MaximumEventTypeBytes = 64;
    public const int MaximumPayloadBytes = 16 * 1024;
    public const int MaximumSchemaVersion = short.MaxValue;

    private readonly byte[] _payload;

    public OutboxEventMessage(
        Guid eventId,
        string consumerKey,
        string aggregateType,
        string aggregateKey,
        long aggregateRevision,
        string eventType,
        int schemaVersion,
        DateTimeOffset occurredAtUtc,
        ReadOnlyMemory<byte> payload)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException(
                "An outbox event ID is required.",
                nameof(eventId));
        }

        if (aggregateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateRevision));
        }

        if (schemaVersion is <= 0 or > MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (occurredAtUtc == default)
        {
            throw new ArgumentException(
                "An event occurrence time is required.",
                nameof(occurredAtUtc));
        }

        if (payload.Length > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Outbox payloads are limited to {MaximumPayloadBytes} bytes.");
        }

        EventId = eventId;
        ConsumerKey = OutboxConsumerContract.RequireKey(consumerKey);
        AggregateType = RequireBoundedCode(
            aggregateType,
            MaximumAggregateTypeBytes,
            nameof(aggregateType));
        AggregateKey = RequireBoundedText(
            aggregateKey,
            MaximumAggregateKeyBytes,
            nameof(aggregateKey));
        AggregateRevision = aggregateRevision;
        EventType = RequireBoundedCode(
            eventType,
            MaximumEventTypeBytes,
            nameof(eventType));
        SchemaVersion = schemaVersion;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        _payload = payload.ToArray();
    }

    public Guid EventId { get; }

    public string ConsumerKey { get; }

    public string AggregateType { get; }

    public string AggregateKey { get; }

    public long AggregateRevision { get; }

    public string EventType { get; }

    public int SchemaVersion { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public ReadOnlyMemory<byte> Payload => _payload.ToArray();

    private static string RequireBoundedCode(
        string value,
        int maximumBytes,
        string parameterName)
    {
        var bounded = RequireBoundedText(
            value,
            maximumBytes,
            parameterName);
        if (bounded[0] is not (>= 'a' and <= 'z') ||
            bounded.Skip(1).Any(static character =>
                character is not (
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '_' or '.' or '-')))
        {
            throw new ArgumentException(
                $"{parameterName} must be a canonical lowercase ASCII code.",
                parameterName);
        }

        return bounded;
    }

    private static string RequireBoundedText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"{parameterName} is limited to {maximumBytes} UTF-8 bytes.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} cannot contain control characters.",
                parameterName);
        }

        return value;
    }
}
