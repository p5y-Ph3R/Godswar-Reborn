using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterCheckpointCoordinatorChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Guid OwnerId =
        Guid.Parse("411bd8f0-b894-4b7f-a844-16088a36769e");

    public static async Task RunAsync()
    {
        await CheckCoalescingAndOrderingAsync();
        await CheckFiniteQueueAdmissionAsync();
        await CheckTransientRetryAsync();
        await CheckRetryExhaustionFaultsRuntimeAsync();
        await CheckFiniteDirectAdmissionAsync();
        await CheckReleaseInvalidatesQueuedWorkAsync();
        await CheckDisposalCancelsDirectOperationAsync();
        await CheckDisposalSurvivesUncooperativeWriteAsync();
    }

    private static async Task CheckCoalescingAndOrderingAsync()
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var calls = 0;
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = async (checkpoint, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(
                        Timeout,
                        cancellationToken);
                }
                return Applied(checkpoint.Revision);
            }
        };
        await using var coordinator =
            new CharacterCheckpointCoordinator(store, TestOptions());
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        var owner = new PlayerOwnershipFence(OwnerId, 1);

        Check.Equal(
            (int)CharacterCheckpointEnqueueStatus.Accepted,
            (int)coordinator.TryEnqueue(
                Position(owner, revision: 1, x: 1)).Status,
            "first checkpoint is accepted");
        await firstStarted.Task.WaitAsync(Timeout);
        Check.Equal(
            (int)CharacterCheckpointEnqueueStatus.Coalesced,
            (int)coordinator.TryEnqueue(
                Position(owner, revision: 2, x: 2)).Status,
            "newer checkpoint coalesces behind an active write");
        Check.Equal(
            (int)CharacterCheckpointEnqueueStatus.Coalesced,
            (int)coordinator.TryEnqueue(
                Position(owner, revision: 3, x: 3)).Status,
            "latest checkpoint replaces an intermediate value");
        Check.Equal(
            (int)CharacterCheckpointEnqueueStatus.IgnoredStale,
            (int)coordinator.TryEnqueue(
                Position(owner, revision: 2, x: 2)).Status,
            "older checkpoint cannot replace pending state");
        Check.Equal(
            (int)CharacterCheckpointEnqueueStatus.RevisionConflict,
            (int)coordinator.TryEnqueue(
                Position(owner, revision: 3, x: 99)).Status,
            "same revision with different state is rejected");

        releaseFirst.TrySetResult();
        await WaitUntilAsync(
            () => store.Positions.Count == 2 &&
                coordinator.GetSnapshot().PendingKeys == 0);
        Check.True(
            store.Positions.Select(item => item.Revision)
                .SequenceEqual([1L, 3L]),
            "worker persists the active revision and latest coalesced revision");
        Check.True(
            coordinator.GetSnapshot().IsReady,
            "healthy worker remains ready");

        coordinator.Complete();
        await run.WaitAsync(Timeout);
        Check.Equal(
            (int)CharacterCheckpointRuntimeState.Stopped,
            (int)coordinator.GetSnapshot().State,
            "drained worker stops cleanly");
    }

    private static async Task CheckFiniteQueueAdmissionAsync()
    {
        var started = NewSignal();
        var release = NewSignal();
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = async (checkpoint, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(Timeout, cancellationToken);
                return Applied(checkpoint.Revision);
            }
        };
        await using var coordinator =
            new CharacterCheckpointCoordinator(
                store,
                TestOptions(queueCapacity: 1));
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        var owner = new PlayerOwnershipFence(OwnerId, 1);

        Check.True(
            coordinator.TryEnqueue(Position(owner, 1, 1)).Accepted,
            "first distinct key consumes finite queue capacity");
        await started.Task.WaitAsync(Timeout);
        var saturated = coordinator.TryEnqueue(
            new CharacterVitalsCheckpoint(
                2,
                2,
                owner,
                10,
                10,
                1));
        Check.Equal(
            (int)CharacterCheckpointEnqueueStatus.Saturated,
            (int)saturated.Status,
            "second distinct key is rejected at configured capacity");

        release.TrySetResult();
        await WaitUntilAsync(
            () => coordinator.GetSnapshot().PendingKeys == 0);
        coordinator.Complete();
        await run.WaitAsync(Timeout);
    }

    private static async Task CheckTransientRetryAsync()
    {
        var calls = 0;
        var store = new FakeCharacterCheckpointStore
        {
            VitalsWrite = (checkpoint, _) =>
            {
                if (Interlocked.Increment(ref calls) <= 2)
                {
                    throw new IOException("transient test failure");
                }
                return Task.FromResult(Applied(checkpoint.Revision));
            }
        };
        await using var coordinator =
            new CharacterCheckpointCoordinator(
                store,
                TestOptions(
                    baseRetryMilliseconds: 1,
                    maximumRetryAgeMilliseconds: 1_000));
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        var owner = new PlayerOwnershipFence(OwnerId, 1);

        coordinator.TryEnqueue(
            new CharacterVitalsCheckpoint(
                1,
                1,
                owner,
                50,
                25,
                1));
        await WaitUntilAsync(
            () => Volatile.Read(ref calls) == 3 &&
                coordinator.GetSnapshot().PendingKeys == 0);
        Check.Equal(
            3,
            calls,
            "transient checkpoint failure retries to success");

        coordinator.Complete();
        await run.WaitAsync(Timeout);
    }

    private static async Task CheckRetryExhaustionFaultsRuntimeAsync()
    {
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = (_, _) =>
                throw new IOException("persistent test failure")
        };
        await using var coordinator =
            new CharacterCheckpointCoordinator(
                store,
                TestOptions(
                    workerCount: 4,
                    baseRetryMilliseconds: 2,
                    maximumRetryAgeMilliseconds: 12));
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        coordinator.TryEnqueue(
            Position(
                new PlayerOwnershipFence(OwnerId, 1),
                1,
                1));

        var faulted = false;
        try
        {
            await run.WaitAsync(Timeout);
        }
        catch (CharacterCheckpointRetryExhaustedException)
        {
            faulted = true;
        }
        Check.True(
            faulted,
            "maximum retry age faults the supervised runtime");
        Check.Equal(
            (int)CharacterCheckpointRuntimeState.Faulted,
            (int)coordinator.GetSnapshot().State,
            "retry exhaustion removes readiness");
    }

    private static async Task CheckFiniteDirectAdmissionAsync()
    {
        var started = NewSignal();
        var release = NewSignal();
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = async (checkpoint, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(Timeout, cancellationToken);
                return Applied(checkpoint.Revision);
            }
        };
        await using var coordinator =
            new CharacterCheckpointCoordinator(
                store,
                TestOptions(
                    directConcurrency: 1,
                    directAdmissionMilliseconds: 15));
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        var owner = new PlayerOwnershipFence(OwnerId, 1);
        var first = coordinator.FlushThroughAsync(
            Position(owner, 1, 1));
        await started.Task.WaitAsync(Timeout);

        var rejected = false;
        try
        {
            _ = await coordinator.FlushThroughAsync(
                new CharacterVitalsCheckpoint(
                    1,
                    1,
                    owner,
                    10,
                    10,
                    1));
        }
        catch (CharacterCheckpointAdmissionException)
        {
            rejected = true;
        }
        Check.True(
            rejected,
            "direct checkpoint operations have finite admission");

        release.TrySetResult();
        Check.True(
            (await first.WaitAsync(Timeout)).Satisfies(1),
            "admitted flush completes through requested revision");
        coordinator.Complete();
        await run.WaitAsync(Timeout);
    }

    private static async Task CheckReleaseInvalidatesQueuedWorkAsync()
    {
        var started = NewSignal();
        var releaseWrite = NewSignal();
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = async (checkpoint, cancellationToken) =>
            {
                started.TrySetResult();
                await releaseWrite.Task.WaitAsync(
                    Timeout,
                    cancellationToken);
                return Applied(checkpoint.Revision);
            }
        };
        await using var coordinator =
            new CharacterCheckpointCoordinator(store, TestOptions());
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        var firstOwner = new PlayerOwnershipFence(OwnerId, 1);
        var secondOwner = new PlayerOwnershipFence(
            Guid.Parse("8476eaf9-8ee2-45b8-bcf3-45f66268299f"),
            1);

        coordinator.TryEnqueue(Position(firstOwner, 1, 1));
        await started.Task.WaitAsync(Timeout);
        coordinator.TryEnqueue(
            new CharacterVitalsCheckpoint(
                2,
                2,
                secondOwner,
                100,
                100,
                1));
        var released = await coordinator.ReleaseAsync(
            2,
            2,
            secondOwner);
        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.Released,
            (int)released,
            "release reports authoritative store result");

        releaseWrite.TrySetResult();
        await WaitUntilAsync(
            () => coordinator.GetSnapshot().PendingKeys == 0);
        Check.Equal(
            0,
            store.Vitals.Count,
            "released ownership invalidates queued writes");
        coordinator.Complete();
        await run.WaitAsync(Timeout);
    }

    private static async Task CheckDisposalCancelsDirectOperationAsync()
    {
        var started = NewSignal();
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(
                    System.Threading.Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException(
                    "Infinite checkpoint delay unexpectedly completed.");
            }
        };
        var coordinator = new CharacterCheckpointCoordinator(
            store,
            TestOptions(
                commandTimeoutMilliseconds: 100,
                shutdownDrainMilliseconds: 20));
        _ = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        var flush = coordinator.FlushThroughAsync(
            Position(
                new PlayerOwnershipFence(OwnerId, 1),
                1,
                1));
        await started.Task.WaitAsync(Timeout);

        await coordinator.DisposeAsync().AsTask().WaitAsync(Timeout);
        var cancelled = false;
        try
        {
            _ = await flush;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check.True(
            cancelled,
            "bounded disposal cancels an admitted direct operation");
        Check.Equal(
            (int)CharacterCheckpointRuntimeState.Disposed,
            (int)coordinator.GetSnapshot().State,
            "bounded disposal reaches terminal state");
    }

    private static async Task
        CheckDisposalSurvivesUncooperativeWriteAsync()
    {
        var started = NewSignal();
        var release = NewSignal();
        var store = new FakeCharacterCheckpointStore
        {
            PositionWrite = async (checkpoint, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return Applied(checkpoint.Revision);
            }
        };
        var coordinator = new CharacterCheckpointCoordinator(
            store,
            TestOptions(
                commandTimeoutMilliseconds: 25,
                shutdownDrainMilliseconds: 10));
        var run = coordinator.RunAsync();
        await coordinator.WaitUntilReadyAsync().WaitAsync(Timeout);
        coordinator.TryEnqueue(
            Position(
                new PlayerOwnershipFence(OwnerId, 1),
                1,
                1));
        await started.Task.WaitAsync(Timeout);

        await coordinator.DisposeAsync().AsTask().WaitAsync(Timeout);
        var duringLateWrite = coordinator.GetSnapshot();
        Check.Equal(
            (int)CharacterCheckpointRuntimeState.Disposed,
            (int)duringLateWrite.State,
            "uncooperative write cannot prevent terminal disposal");
        Check.Equal(
            1,
            duringLateWrite.ActiveWrites,
            "disposal preserves live write accounting past its deadline");

        release.TrySetResult();
        await run.WaitAsync(Timeout);
        var afterLateWrite = coordinator.GetSnapshot();
        Check.Equal(
            (int)CharacterCheckpointRuntimeState.Disposed,
            (int)afterLateWrite.State,
            "late worker completion cannot overwrite terminal disposal");
        Check.Equal(
            0,
            afterLateWrite.ActiveWrites,
            "late worker completion drains accounting without underflow");
    }

    private static CharacterPositionCheckpoint Position(
        PlayerOwnershipFence owner,
        long revision,
        float x) =>
        new(1, 1, owner, 1, x, x, revision);

    private static CharacterCheckpointWriteResult Applied(long revision) =>
        new(CharacterCheckpointWriteStatus.Applied, revision);

    private static CharacterCheckpointWorkerOptions TestOptions(
        int queueCapacity = 8,
        int workerCount = 1,
        int directConcurrency = 2,
        int directAdmissionMilliseconds = 100,
        int baseRetryMilliseconds = 5,
        int maximumRetryAgeMilliseconds = 500,
        int commandTimeoutMilliseconds = 2_000,
        int shutdownDrainMilliseconds = 2_000) =>
        new()
        {
            QueueCapacity = queueCapacity,
            WorkerCount = workerCount,
            DirectOperationConcurrency = directConcurrency,
            DirectAdmissionTimeoutMilliseconds =
                directAdmissionMilliseconds,
            CommandTimeoutMilliseconds = commandTimeoutMilliseconds,
            BaseRetryDelayMilliseconds = baseRetryMilliseconds,
            MaximumRetryDelayMilliseconds =
                Math.Max(baseRetryMilliseconds, 20),
            MaximumRetryAgeMilliseconds = maximumRetryAgeMilliseconds,
            ShutdownDrainTimeoutMilliseconds = shutdownDrainMilliseconds
        };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Checkpoint coordinator condition timed out.");
            }
            await Task.Delay(5);
        }
    }
}
