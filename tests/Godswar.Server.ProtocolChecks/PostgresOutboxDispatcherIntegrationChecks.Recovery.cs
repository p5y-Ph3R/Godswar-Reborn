using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static async Task CheckStaleDeliveryAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.stale";
        var aggregateKey = NewAggregateKey("stale");
        _ = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 2,
            orderingPolicy: "latest_wins");
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.VersionedState);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-stale");
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "latest-wins revision two establishes a real checkpoint");

        var stale = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "latest_wins");

        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "stale work is terminally processed");
        Check.Equal(
            1,
            consumer.MessageCount,
            "stale work does not invoke its consumer after the checkpoint");
        var state = await ReadEventAsync(dataSource, stale.RowId);
        Check.True(
            state.DeliveredAtUtc.HasValue &&
            state.AttemptCount == 0 &&
            !state.HasLease,
            "stale work is checkpointed without an attempt or lease");
        await AssertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            expectedRevision: 2,
            expectInflight: false);
    }

    private static async Task CheckRetryAndPoisonAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.poison";
        var aggregateKey = NewAggregateKey("poison");
        var inserted = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict",
            maximumAttempts: 2);
        var consumer = new AlwaysFailingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-poison",
            batchSize: 1,
            maximumAttempts: 2);

        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "the first consumer failure is processed");
        var retry = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            retry.AttemptCount == 1 &&
            !retry.PoisonedAtUtc.HasValue &&
            !retry.HasLease,
            "the first failure schedules an unleased retry");

        await MakeAvailableAsync(dataSource, inserted.RowId);
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "the final consumer failure is processed");
        var poisoned = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            poisoned.AttemptCount == 2 &&
            poisoned.PoisonedAtUtc.HasValue &&
            !poisoned.DeliveredAtUtc.HasValue &&
            !poisoned.HasLease,
            "bounded retries end in a poison state");
        Check.Equal(
            "consumer_failure_max_attempts",
            poisoned.PoisonReason!,
            "consumer failure records a bounded poison reason");
        Check.Equal(
            2,
            consumer.AttemptCount,
            "the failing consumer runs exactly to its attempt budget");
    }

    private static async Task CheckClaimCrashRecoveryAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.claim-crash";
        var aggregateKey = NewAggregateKey("claim-crash");
        var inserted = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var crashingDispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-claim-crash",
            probe: new ThrowOnceProbe(
                PostgresOutboxDispatcherProbeStage.AfterClaim),
            leaseMilliseconds: 1_000,
            commandTimeoutMilliseconds: 100);

        await ExpectAsync<SimulatedDispatcherCrashException>(
            () => crashingDispatcher.DispatchOnceAsync(),
            "a crash immediately after claim escapes the dispatcher");
        Check.Equal(
            0,
            consumer.MessageCount,
            "an after-claim crash occurs before the callback");
        var leased = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            leased.AttemptCount == 1 && leased.HasLease,
            "an after-claim crash leaves a durable lease");

        await Task.Delay(1_100);
        var recovery = CreateDispatcher(
            dataSource,
            consumer,
            "checks-claim-recovery",
            batchSize: 1);
        Check.Equal(
            1,
            await recovery.DispatchOnceAsync(),
            "an expired claim is reclaimed before new delivery");
        Check.Equal(
            0,
            consumer.MessageCount,
            "lease recovery observes the retry delay");
        await MakeAvailableAsync(dataSource, inserted.RowId);
        Check.Equal(
            1,
            await recovery.DispatchOnceAsync(),
            "the recovered claim is delivered");
        Check.Equal(
            1,
            consumer.MessageCount,
            "claim-crash recovery invokes the callback once");
        var delivered = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            delivered.AttemptCount == 2 &&
            delivered.DeliveredAtUtc.HasValue &&
            !delivered.HasLease,
            "claim-crash recovery spends a second bounded attempt");
    }

    private static async Task CheckConsumerSuccessCrashAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.success-crash";
        var aggregateKey = NewAggregateKey("success-crash");
        var inserted = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var crashingDispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-success-crash",
            probe: new ThrowOnceProbe(
                PostgresOutboxDispatcherProbeStage
                    .AfterConsumerSuccess),
            leaseMilliseconds: 1_000,
            commandTimeoutMilliseconds: 100);

        await ExpectAsync<SimulatedDispatcherCrashException>(
            () => crashingDispatcher.DispatchOnceAsync(),
            "a crash after consumer success escapes before checkpointing");
        Check.Equal(
            1,
            consumer.MessageCount,
            "the first at-least-once side effect occurred");
        Check.True(
            (await ReadEventAsync(dataSource, inserted.RowId)).HasLease,
            "a post-consumer crash leaves the event uncheckpointed");

        await Task.Delay(1_100);
        var recovery = CreateDispatcher(
            dataSource,
            consumer,
            "checks-success-recovery",
            batchSize: 1);
        Check.Equal(
            1,
            await recovery.DispatchOnceAsync(),
            "post-consumer lease expiry is recovered");
        await MakeAvailableAsync(dataSource, inserted.RowId);
        Check.Equal(
            1,
            await recovery.DispatchOnceAsync(),
            "post-consumer recovery redelivers the event");
        Check.Equal(
            2,
            consumer.MessageCount,
            "consumer success before checkpoint has at-least-once delivery");
        Check.Equal(
            1,
            consumer.EventIds.Distinct().Count(),
            "at-least-once redelivery preserves the stable event ID");
        var delivered = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            delivered.AttemptCount == 2 &&
            delivered.DeliveredAtUtc.HasValue,
            "redelivery is checkpointed on its second attempt");
    }
}
