using System.Collections.Concurrent;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static PostgresOutboxDispatcher CreateDispatcher(
        NpgsqlDataSource dataSource,
        IOutboxEventConsumer consumer,
        string leaseOwner,
        int batchSize = 8,
        int maximumAttempts = 8,
        IPostgresOutboxDispatcherProbe? probe = null,
        int leaseMilliseconds = 10_000,
        int commandTimeoutMilliseconds = 5_000) =>
        new(
            dataSource,
            [consumer],
            new PostgresOutboxDispatcherOptions
            {
                Enabled = true,
                BatchSize = batchSize,
                PollIntervalMilliseconds = 50,
                LeaseMilliseconds = leaseMilliseconds,
                MaximumDeliveryAttempts = maximumAttempts,
                BaseRetryDelayMilliseconds = 50,
                MaximumRetryDelayMilliseconds = 50,
                GapRetryDelayMilliseconds = 50,
                CommandTimeoutMilliseconds = commandTimeoutMilliseconds
            },
            leaseOwner,
            probe);

    private static async Task ExpectAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }

    private sealed class RecordingConsumer : IOutboxEventConsumer
    {
        private readonly ConcurrentQueue<OutboxEventMessage> _messages = [];

        public RecordingConsumer(
            string consumerKey,
            OutboxOrderingPolicy orderingPolicy)
        {
            ConsumerKey = consumerKey;
            OrderingPolicy = orderingPolicy;
        }

        public string ConsumerKey { get; }

        public OutboxOrderingPolicy OrderingPolicy { get; }

        public int MessageCount => _messages.Count;

        public OutboxEventMessage SingleMessage =>
            _messages.Single();

        public IReadOnlyList<long> Revisions =>
            _messages.Select(static message =>
                message.AggregateRevision).ToArray();

        public IReadOnlyList<Guid> EventIds =>
            _messages.Select(static message =>
                message.EventId).ToArray();

        public ValueTask ConsumeAsync(
            OutboxEventMessage message,
            CancellationToken cancellationToken = default)
        {
            _messages.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlwaysFailingConsumer : IOutboxEventConsumer
    {
        private int _attemptCount;

        public AlwaysFailingConsumer(
            string consumerKey,
            OutboxOrderingPolicy orderingPolicy)
        {
            ConsumerKey = consumerKey;
            OrderingPolicy = orderingPolicy;
        }

        public string ConsumerKey { get; }

        public OutboxOrderingPolicy OrderingPolicy { get; }

        public int AttemptCount =>
            Volatile.Read(ref _attemptCount);

        public ValueTask ConsumeAsync(
            OutboxEventMessage message,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attemptCount);
            throw new InvalidOperationException(
                "Expected protocol-check consumer failure.");
        }
    }

    private sealed class SelectiveBlockingConsumer :
        IOutboxEventConsumer
    {
        private readonly string _blockedAggregateKey;
        private readonly ConcurrentDictionary<string, int> _counts =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SelectiveBlockingConsumer(
            string consumerKey,
            OutboxOrderingPolicy orderingPolicy,
            string blockedAggregateKey)
        {
            ConsumerKey = consumerKey;
            OrderingPolicy = orderingPolicy;
            _blockedAggregateKey = blockedAggregateKey;
        }

        public string ConsumerKey { get; }

        public OutboxOrderingPolicy OrderingPolicy { get; }

        public int CountFor(string aggregateKey) =>
            _counts.TryGetValue(aggregateKey, out var count)
                ? count
                : 0;

        public async ValueTask ConsumeAsync(
            OutboxEventMessage message,
            CancellationToken cancellationToken = default)
        {
            _counts.AddOrUpdate(
                message.AggregateKey,
                1,
                static (_, count) => count + 1);
            if (message.AggregateKey != _blockedAggregateKey)
            {
                return;
            }

            _blocked.TrySetResult();
            await _released.Task;
        }

        public async Task WaitUntilBlockedAsync() =>
            await _blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _released.TrySetResult();
    }

    private sealed class ThrowOnceProbe :
        IPostgresOutboxDispatcherProbe
    {
        private readonly PostgresOutboxDispatcherProbeStage _stage;
        private int _hasThrown;

        public ThrowOnceProbe(
            PostgresOutboxDispatcherProbeStage stage)
        {
            _stage = stage;
        }

        public ValueTask ReachedAsync(
            PostgresOutboxDispatcherProbeStage stage,
            CancellationToken cancellationToken)
        {
            if (stage == _stage &&
                Interlocked.Exchange(ref _hasThrown, 1) == 0)
            {
                throw new SimulatedDispatcherCrashException();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SimulatedDispatcherCrashException : Exception;
}
