using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static async Task CheckVersionedStateOrderingAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.latest";
        var aggregateKey = NewAggregateKey("latest");
        var revisionTwo = await InsertEventAsync(
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
            "checks-latest",
            batchSize: 1);

        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "versioned state may advance directly from zero to two");
        Check.True(
            consumer.Revisions.SequenceEqual([2L]),
            "versioned-state revision two reaches the consumer");
        Check.Equal(
            1,
            (await ReadEventAsync(dataSource, revisionTwo.RowId))
                .AttemptCount,
            "versioned-state delivery consumes one attempt");
        await AssertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            expectedRevision: 2,
            expectInflight: false);

        var lateRevisionOne = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "latest_wins");
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "an older versioned-state event is terminally processed");
        Check.Equal(
            1,
            consumer.MessageCount,
            "an older versioned-state event never reaches the consumer");
        var stale = await ReadEventAsync(
            dataSource,
            lateRevisionOne.RowId);
        Check.True(
            stale.DeliveredAtUtc.HasValue &&
            stale.AttemptCount == 0,
            "an older versioned-state event is stale without an attempt");
        await AssertPositionAsync(
            dataSource,
            consumerKey,
            aggregateKey,
            expectedRevision: 2,
            expectInflight: false);
    }

    private static async Task CheckImmediateLeaseRegressionAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.immediate";
        var blockedKey = NewAggregateKey("immediate-first");
        var secondKey = NewAggregateKey("immediate-second");
        var first = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            blockedKey,
            revision: 1,
            orderingPolicy: "strict");
        var second = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            secondKey,
            revision: 1,
            orderingPolicy: "strict");
        var consumer = new SelectiveBlockingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence,
            blockedKey);
        var dispatcherA = CreateDispatcher(
            dataSource,
            consumer,
            "checks-immediate-a",
            batchSize: 4);
        var dispatcherB = CreateDispatcher(
            dataSource,
            consumer,
            "checks-immediate-b",
            batchSize: 4);

        var firstPoll = dispatcherA.DispatchOnceAsync();
        await consumer.WaitUntilBlockedAsync();
        try
        {
            var notStarted =
                await ReadEventAsync(dataSource, second.RowId);
            Check.True(
                notStarted.AttemptCount == 0 && !notStarted.HasLease,
                "a dispatcher does not pre-lease later batch work");

            Check.Equal(
                1,
                await dispatcherB.DispatchOnceAsync(),
                "another poller may claim the independent second stream");
            var deliveredSecond =
                await ReadEventAsync(dataSource, second.RowId);
            Check.True(
                deliveredSecond.AttemptCount == 1 &&
                deliveredSecond.DeliveredAtUtc.HasValue &&
                !deliveredSecond.HasLease,
                "the second stream is delivered once without lease recovery");
            Check.Equal(
                1,
                consumer.CountFor(secondKey),
                "the independent second stream has one callback");

            var stillBlocked =
                await ReadEventAsync(dataSource, first.RowId);
            Check.True(
                stillBlocked.AttemptCount == 1 &&
                stillBlocked.HasLease &&
                !stillBlocked.DeliveredAtUtc.HasValue,
                "the first stream remains owned while its callback is blocked");
        }
        finally
        {
            consumer.Release();
        }

        Check.Equal(
            1,
            await firstPoll,
            "the first dispatcher completes only its started work");
        Check.Equal(
            1,
            consumer.CountFor(blockedKey),
            "the blocked stream has one callback");
    }

    private static async Task CheckPolicyMismatchAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.mismatch";
        var aggregateKey = NewAggregateKey("mismatch");
        var inserted = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.VersionedState);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-policy-mismatch");

        await ExpectAsync<InvalidDataException>(
            () => dispatcher.DispatchOnceAsync(),
            "registered and durable ordering policies must agree");
        Check.Equal(
            0,
            consumer.MessageCount,
            "policy mismatch fails before consumer invocation");
        var state = await ReadEventAsync(dataSource, inserted.RowId);
        Check.True(
            state.AttemptCount == 0 &&
            !state.HasLease &&
            !state.DeliveredAtUtc.HasValue,
            "policy mismatch leaves pending work untouched");
    }
}
