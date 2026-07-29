using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL outbox dispatcher integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await RequireDisposableB03DatabaseAsync(dataSource);
        var fixture = await CreateCommandFixtureAsync(dataSource);

        AssertContractBounds();
        await CheckSchemaHardeningAsync(dataSource, fixture);
        await CheckNormalDeliveryAsync(dataSource, fixture);
        await CheckConcurrentPollersAsync(dataSource, fixture);
        await CheckStrictOrderingAsync(dataSource, fixture);
        await CheckVersionedStateOrderingAsync(dataSource, fixture);
        await CheckStaleDeliveryAsync(dataSource, fixture);
        await CheckRetryAndPoisonAsync(dataSource, fixture);
        await CheckClaimCrashRecoveryAsync(dataSource, fixture);
        await CheckConsumerSuccessCrashAsync(dataSource, fixture);
        await CheckImmediateLeaseRegressionAsync(dataSource, fixture);
        await CheckPolicyMismatchAsync(dataSource, fixture);
        await CheckGracefulRunnerCancellationAsync(dataSource);
    }

    private static async Task CheckNormalDeliveryAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.normal";
        var aggregateKey = NewAggregateKey("normal");
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var inserted = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict",
            createdAt: createdAt);
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            leaseOwner: "checks-normal");

        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "one normal outbox event is processed");
        var message = consumer.SingleMessage;
        Check.Equal(
            inserted.EventId,
            message.EventId,
            "normal delivery preserves event identity");
        Check.Equal(
            inserted.CreatedAtUtc.UtcTicks,
            message.OccurredAtUtc.UtcTicks,
            "event occurrence time is the durable created_at value");
        Check.True(
            DateTimeOffset.UtcNow - message.OccurredAtUtc >
                TimeSpan.FromMinutes(9),
            "event occurrence time is not replaced by claim time");

        var state = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            state.DeliveredAtUtc.HasValue &&
            !state.PoisonedAtUtc.HasValue &&
            !state.HasLease,
            "normal delivery clears the lease and records delivery");
        Check.Equal(
            1,
            state.AttemptCount,
            "normal delivery consumes one attempt");
        await AssertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            expectedRevision: 1,
            expectInflight: false);
    }

    private static async Task CheckConcurrentPollersAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.concurrent";
        var aggregateKey = NewAggregateKey("concurrent");
        var inserted = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        var consumer = new SelectiveBlockingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence,
            aggregateKey);
        var dispatcherA = CreateDispatcher(
            dataSource,
            consumer,
            "checks-concurrent-a");
        var dispatcherB = CreateDispatcher(
            dataSource,
            consumer,
            "checks-concurrent-b");

        var firstPoll = dispatcherA.DispatchOnceAsync();
        await consumer.WaitUntilBlockedAsync();
        try
        {
            Check.Equal(
                0,
                await dispatcherB.DispatchOnceAsync(),
                "a concurrent poller cannot claim an inflight stream");
            Check.Equal(
                1,
                consumer.CountFor(aggregateKey),
                "concurrent pollers invoke the consumer once");
        }
        finally
        {
            consumer.Release();
        }

        Check.Equal(
            1,
            await firstPoll,
            "the winning concurrent poller checkpoints its event");
        var state = await ReadEventAsync(dataSource, inserted.RowId);
        Check.Equal(
            1,
            state.AttemptCount,
            "concurrent polling does not spend a duplicate attempt");
        Check.True(
            state.DeliveredAtUtc.HasValue,
            "the concurrent-poller event is delivered");
    }

    private static async Task CheckStrictOrderingAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.strict";
        var aggregateKey = NewAggregateKey("strict");
        var second = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 2,
            orderingPolicy: "strict");
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-strict",
            batchSize: 1);

        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "strict revision two is recognized as a gap");
        Check.Equal(
            0,
            consumer.MessageCount,
            "a strict gap never reaches the consumer");
        Check.Equal(
            0,
            (await ReadEventAsync(dataSource, second.RowId)).AttemptCount,
            "delaying a strict gap does not consume an attempt");
        await DelayAvailabilityAsync(
            dataSource,
            second.RowId,
            TimeSpan.FromSeconds(2));

        var first = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "strict revision one is delivered after the gap");
        await MakeAvailableAsync(dataSource, second.RowId);
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "strict revision two follows revision one");

        Check.True(
            consumer.Revisions.SequenceEqual([1L, 2L]),
            "strict callbacks occur in aggregate revision order");
        Check.True(
            (await ReadEventAsync(dataSource, first.RowId))
                .DeliveredAtUtc.HasValue &&
            (await ReadEventAsync(dataSource, second.RowId))
                .DeliveredAtUtc.HasValue,
            "both strict events are durably checkpointed");
        await AssertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            expectedRevision: 2,
            expectInflight: false);
    }

    private static void AssertContractBounds()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new OutboxEventMessage(
                Guid.NewGuid(),
                "checks.outbox.bounds",
                "character",
                "bounded-aggregate",
                aggregateRevision: 1,
                "checks.event",
                schemaVersion: 1,
                DateTimeOffset.UtcNow,
                new byte[OutboxEventMessage.MaximumPayloadBytes + 1]),
            "outbox messages reject oversized payloads before dispatch");
    }

    private static async Task CheckGracefulRunnerCancellationAsync(
        NpgsqlDataSource dataSource)
    {
        var consumer = new RecordingConsumer(
            "checks.outbox.shutdown",
            OutboxOrderingPolicy.StrictSequence);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-shutdown");
        using var shutdown = new CancellationTokenSource();

        var run = dispatcher.RunAsync(shutdown.Token);
        await Task.Delay(100);
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Check.True(
            run.IsCompletedSuccessfully,
            "host cancellation stops the dispatcher without a faulted task");
    }
}
