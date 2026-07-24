using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static class BoundedByteQueueChecks
{
    public static async Task RunAsync()
    {
        CheckValidation();
        await CheckDualBoundsAndOrderingAsync();
        await CheckCanceledHeadReleasesFollowersAsync();
        await CheckNormalCompletionAsync();
        await CheckErrorCompletionDrainsFirstAsync();
        await CheckDrainAndHighWaterAsync();
    }

    private static void CheckValidation()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new BoundedByteQueue<string>(0, 1),
            "zero item capacity is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new BoundedByteQueue<string>(1, 0),
            "zero byte capacity is rejected");

        var queue = new BoundedByteQueue<string>(1, 4);
        Check.Throws<ArgumentNullException>(
            () => queue.EnqueueAsync(null!, 0),
            "null items are rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => queue.EnqueueAsync("negative", -1),
            "negative byte costs are rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => queue.EnqueueAsync("oversized", 5),
            "items larger than total byte capacity are rejected");
    }

    private static async Task CheckDualBoundsAndOrderingAsync()
    {
        var queue = new BoundedByteQueue<string>(3, 5);
        await queue.EnqueueAsync("first", 4);

        var secondAdmission = queue.EnqueueAsync("second", 2).AsTask();
        var thirdAdmission = queue.EnqueueAsync("third", 1).AsTask();

        var blocked = queue.Snapshot();
        Check.Equal(1, blocked.CurrentItems, "one item is admitted at the byte bound");
        Check.Equal(4L, blocked.CurrentBytes, "admitted bytes are counted");
        Check.Equal(2, blocked.WaitingProducers, "both later producers wait");
        Check.True(!secondAdmission.IsCompleted, "the byte-bound producer waits");
        Check.True(
            !thirdAdmission.IsCompleted,
            "a smaller producer does not bypass the FIFO head");

        CheckDequeue(await queue.DequeueAsync(), "first", 4, "first queue item");
        await Task.WhenAll(secondAdmission, thirdAdmission);

        var admitted = queue.Snapshot();
        Check.Equal(2, admitted.CurrentItems, "waiting producers are admitted after dequeue");
        Check.Equal(3L, admitted.CurrentBytes, "dequeue releases byte accounting");
        Check.Equal(0, admitted.WaitingProducers, "producer waiters are removed on admission");

        CheckDequeue(await queue.DequeueAsync(), "second", 2, "second queue item");
        CheckDequeue(await queue.DequeueAsync(), "third", 1, "third queue item");
    }

    private static async Task CheckCanceledHeadReleasesFollowersAsync()
    {
        var queue = new BoundedByteQueue<string>(3, 5);
        await queue.EnqueueAsync("resident", 4);

        using var cancellation = new CancellationTokenSource();
        var canceledAdmission = queue
            .EnqueueAsync("canceled-head", 2, cancellation.Token)
            .AsTask();
        var followingAdmission = queue.EnqueueAsync("following", 1).AsTask();

        cancellation.Cancel();
        await ExpectCanceledAsync(canceledAdmission, "canceled producer admission");
        await followingAdmission;

        var snapshot = queue.Snapshot();
        Check.Equal(2, snapshot.CurrentItems, "follower is admitted into released capacity");
        Check.Equal(5L, snapshot.CurrentBytes, "follower byte cost is accounted");
        Check.Equal(0, snapshot.WaitingProducers, "canceled producer is unlinked");

        CheckDequeue(await queue.DequeueAsync(), "resident", 4, "resident remains first");
        CheckDequeue(await queue.DequeueAsync(), "following", 1, "follower remains ordered");
    }

    private static async Task CheckNormalCompletionAsync()
    {
        var queue = new BoundedByteQueue<string>(1, 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitingConsumer = queue.DequeueAsync(cancellation.Token).AsTask();

        Check.True(queue.Complete(), "first normal completion succeeds");
        Check.True(!queue.Complete(), "completion is idempotent");

        var completed = await waitingConsumer;
        Check.True(!completed.HasItem, "normal completion wakes a consumer without an item");
        await ExpectThrowsAsync<BoundedByteQueueCompletedException>(
            () => queue.EnqueueAsync("late", 1).AsTask(),
            null,
            "enqueue after normal completion");

        var snapshot = queue.Snapshot();
        Check.True(snapshot.IsCompleted, "snapshot reports completion");
        Check.Equal(0, snapshot.WaitingConsumers, "completed consumer is unlinked");
    }

    private static async Task CheckErrorCompletionDrainsFirstAsync()
    {
        var queue = new BoundedByteQueue<string>(1, 4);
        await queue.EnqueueAsync("admitted", 4);
        var waitingProducer = queue.EnqueueAsync("waiting", 1).AsTask();
        var expectedError = new IOException("test completion");

        Check.True(queue.Complete(expectedError), "error completion succeeds");
        await ExpectThrowsAsync<IOException>(
            () => waitingProducer,
            expectedError,
            "waiting producer receives supplied completion error");

        CheckDequeue(
            await queue.DequeueAsync(),
            "admitted",
            4,
            "admitted item drains before the completion error");
        await ExpectThrowsAsync<IOException>(
            () => queue.DequeueAsync().AsTask(),
            expectedError,
            "consumer receives supplied completion error after drain");
    }

    private static async Task CheckDrainAndHighWaterAsync()
    {
        var queue = new BoundedByteQueue<string>(4, 12);
        await queue.EnqueueAsync("zero", 0);
        await queue.EnqueueAsync("four", 4);
        await queue.EnqueueAsync("six", 6);
        queue.Complete();

        var drained = queue.TryDrain();
        Check.Equal(3, drained.Count, "drain returns every admitted item");
        Check.Equal("zero", drained[0].Item, "drain preserves first item");
        Check.Equal(0, drained[0].ByteCount, "drain preserves zero byte cost");
        Check.Equal("four", drained[1].Item, "drain preserves second item");
        Check.Equal(4, drained[1].ByteCount, "drain preserves second byte cost");
        Check.Equal("six", drained[2].Item, "drain preserves third item");
        Check.Equal(6, drained[2].ByteCount, "drain preserves third byte cost");

        var snapshot = queue.Snapshot();
        Check.Equal(0, snapshot.CurrentItems, "drain releases item accounting");
        Check.Equal(0L, snapshot.CurrentBytes, "drain releases byte accounting");
        Check.Equal(3, snapshot.HighWaterItems, "item high-water survives drain");
        Check.Equal(10L, snapshot.HighWaterBytes, "byte high-water survives drain");

        var completed = await queue.DequeueAsync();
        Check.True(!completed.HasItem, "drained normal queue remains completed");
    }

    private static void CheckDequeue(
        DequeueResult<string> result,
        string expectedItem,
        int expectedBytes,
        string description)
    {
        Check.True(result.HasItem, $"{description} has an item");
        Check.Equal(expectedItem, result.Item, $"{description} value");
        Check.Equal(expectedBytes, result.ByteCount, $"{description} byte cost");
    }

    private static async Task ExpectCanceledAsync(Task task, string description)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected cancellation.");
    }

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        Exception? expectedInstance,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            if (expectedInstance is not null && !ReferenceEquals(expectedInstance, error))
            {
                throw new InvalidOperationException(
                    $"Assertion failed: {description}; completion error instance was replaced.");
            }

            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }
}
