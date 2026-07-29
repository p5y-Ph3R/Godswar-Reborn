using System.Text;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Messaging;

internal enum PostgresOutboxDispatcherProbeStage : byte
{
    AfterClaim = 1,
    AfterConsumerSuccess = 2
}

internal interface IPostgresOutboxDispatcherProbe
{
    ValueTask ReachedAsync(
        PostgresOutboxDispatcherProbeStage stage,
        CancellationToken cancellationToken);
}

internal sealed partial class PostgresOutboxDispatcher
{
    internal const int MaximumConsumers = 64;
    private const int MaximumLeaseOwnerBytes = 128;

    private readonly record struct RegisteredConsumer(
        string Key,
        string DatabaseOrderingPolicy,
        IOutboxEventConsumer Consumer);

    private sealed record ClaimedEvent(
        long RowId,
        Guid LeaseToken,
        int AttemptCount,
        int MaximumAttempts,
        OutboxEventMessage Message,
        IOutboxEventConsumer Consumer);

    private sealed record ClaimedBatch(
        IReadOnlyList<ClaimedEvent> Claims,
        IReadOnlyList<DeferredOutcome> DeferredOutcomes);

    private readonly record struct DeferredOutcome(
        string ConsumerKey,
        DeferredOutcomeKind Kind);

    private enum DeferredOutcomeKind : byte
    {
        Stale = 1,
        Gap = 2,
        LeaseExpiredRetry = 3,
        LeaseExpiredPoison = 4,
        AttemptsExhaustedPoison = 5
    }

    private enum CompletionDisposition : byte
    {
        Delivered = 1,
        RetryScheduled = 2,
        Poisoned = 3,
        LeaseLost = 4
    }

    private static IReadOnlyDictionary<string, RegisteredConsumer>
        BuildConsumerRegistry(
            IEnumerable<IOutboxEventConsumer> consumers)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        var registry = new Dictionary<
            string,
            RegisteredConsumer>(StringComparer.Ordinal);
        foreach (var consumer in consumers)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            if (registry.Count >= MaximumConsumers)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(consumers),
                    $"At most {MaximumConsumers} outbox consumers may be registered.");
            }

            var key = OutboxConsumerContract.RequireKey(
                consumer.ConsumerKey);
            var policy = ToDatabaseOrderingPolicy(
                consumer.OrderingPolicy);
            if (!registry.TryAdd(
                    key,
                    new RegisteredConsumer(
                        key,
                        policy,
                        consumer)))
            {
                throw new ArgumentException(
                    $"Outbox consumer '{key}' is registered more than once.",
                    nameof(consumers));
            }
        }

        if (registry.Count == 0)
        {
            throw new ArgumentException(
                "At least one outbox consumer is required.",
                nameof(consumers));
        }

        return registry;
    }

    private static string RequireLeaseOwner(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) >
                MaximumLeaseOwnerBytes ||
            value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Lease owners are limited to {MaximumLeaseOwnerBytes} " +
                "UTF-8 bytes without control characters.");
        }

        return value;
    }

    private static string ToDatabaseOrderingPolicy(
        OutboxOrderingPolicy policy) =>
        policy switch
        {
            OutboxOrderingPolicy.StrictSequence => "strict",
            OutboxOrderingPolicy.VersionedState => "latest_wins",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };

    private static OutboxOrderingPolicy FromDatabaseOrderingPolicy(
        string value) =>
        value switch
        {
            "strict" => OutboxOrderingPolicy.StrictSequence,
            "latest_wins" => OutboxOrderingPolicy.VersionedState,
            _ => throw new InvalidDataException(
                "The outbox row has an unsupported ordering policy.")
        };
}
