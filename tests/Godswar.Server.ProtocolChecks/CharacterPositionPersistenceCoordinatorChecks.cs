using System.Collections.Concurrent;
using Godswar.Server.Game.Maps;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterPositionPersistenceCoordinatorChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        await CheckCurrentSaveAcceptedAsync();
        await CheckStaleSaveRejectedAsync();
        await CheckRelocationWinsAfterInflightSaveAsync();
        await CheckFailedRelocationRestoresEpochAsync();
        await CheckCancelledRelocationDoesNotLeakGateAsync();
        await CheckCancelledSaveWaitDoesNotLeakGateAsync();
    }

    private static async Task CheckCurrentSaveAcceptedAsync()
    {
        var coordinator =
            new CharacterPositionPersistenceCoordinator();
        var epoch = coordinator.CaptureEpoch();
        var callbackCount = 0;

        var accepted = await coordinator.PersistIfCurrentAsync(
            epoch,
            _ =>
            {
                callbackCount++;
                return Task.CompletedTask;
            }).WaitAsync(Timeout);

        Check.True(
            accepted,
            "current position epoch is accepted");
        Check.Equal(
            1,
            callbackCount,
            "current position callback runs once");
        Check.Equal(
            epoch,
            coordinator.CaptureEpoch(),
            "normal save does not advance position epoch");
    }

    private static async Task CheckStaleSaveRejectedAsync()
    {
        var coordinator =
            new CharacterPositionPersistenceCoordinator();
        var staleEpoch = coordinator.CaptureEpoch();
        var currentEpoch = await coordinator.AdvanceAndPersistAsync(
            _ => Task.CompletedTask).WaitAsync(Timeout);
        var callbackCount = 0;

        var accepted = await coordinator.PersistIfCurrentAsync(
            staleEpoch,
            _ =>
            {
                callbackCount++;
                return Task.CompletedTask;
            }).WaitAsync(Timeout);

        Check.True(
            !accepted,
            "stale position epoch is rejected");
        Check.Equal(
            0,
            callbackCount,
            "stale position callback is not invoked");
        Check.Equal(
            currentEpoch,
            coordinator.CaptureEpoch(),
            "stale save does not alter current epoch");
    }

    private static async Task CheckRelocationWinsAfterInflightSaveAsync()
    {
        var coordinator =
            new CharacterPositionPersistenceCoordinator();
        var oldEpoch = coordinator.CaptureEpoch();
        var writes = new ConcurrentQueue<string>();
        var oldSaveStarted = NewSignal();
        var releaseOldSave = NewSignal();
        var relocationStarted = NewSignal();

        var oldSave = coordinator.PersistIfCurrentAsync(
            oldEpoch,
            async cancellationToken =>
            {
                oldSaveStarted.TrySetResult();
                await releaseOldSave.Task.WaitAsync(
                    Timeout,
                    cancellationToken);
                writes.Enqueue("old-world");
            });
        await oldSaveStarted.Task.WaitAsync(Timeout);

        var relocation = coordinator.AdvanceAndPersistAsync(
            _ =>
            {
                writes.Enqueue("relocation");
                relocationStarted.TrySetResult();
                return Task.CompletedTask;
            });

        Check.True(
            !relocationStarted.Task.IsCompleted,
            "relocation waits behind in-flight old-world save");

        var invalidatedCallbackCount = 0;
        var invalidated = await coordinator.PersistIfCurrentAsync(
            oldEpoch,
            _ =>
            {
                invalidatedCallbackCount++;
                return Task.CompletedTask;
            }).WaitAsync(Timeout);
        Check.True(
            !invalidated,
            "pending relocation invalidates another old-world save");
        Check.Equal(
            0,
            invalidatedCallbackCount,
            "invalidated queued save callback is not invoked");

        releaseOldSave.TrySetResult();
        Check.True(
            await oldSave.WaitAsync(Timeout),
            "in-flight old-world save completes");
        var newEpoch = await relocation.WaitAsync(Timeout);

        Check.True(
            writes.SequenceEqual(["old-world", "relocation"]),
            "relocation is persisted after the in-flight old-world save");
        Check.Equal(
            oldEpoch + 1,
            newEpoch,
            "successful relocation advances epoch once");
        Check.Equal(
            newEpoch,
            coordinator.CaptureEpoch(),
            "successful relocation publishes new epoch");
    }

    private static async Task CheckFailedRelocationRestoresEpochAsync()
    {
        var coordinator =
            new CharacterPositionPersistenceCoordinator();
        var originalEpoch = coordinator.CaptureEpoch();

        await ExpectThrowsAsync<IOException>(
            () => coordinator.AdvanceAndPersistAsync(
                _ => Task.FromException(
                    new IOException("simulated persistence failure"))),
            "failed relocation surfaces persistence error");

        Check.Equal(
            originalEpoch,
            coordinator.CaptureEpoch(),
            "failed relocation rolls epoch back");

        var callbackCount = 0;
        var accepted = await coordinator.PersistIfCurrentAsync(
            originalEpoch,
            _ =>
            {
                callbackCount++;
                return Task.CompletedTask;
            }).WaitAsync(Timeout);
        Check.True(
            accepted,
            "old epoch is current after relocation rollback");
        Check.Equal(
            1,
            callbackCount,
            "save runs after relocation rollback");
    }

    private static async Task
        CheckCancelledRelocationDoesNotLeakGateAsync()
    {
        var coordinator =
            new CharacterPositionPersistenceCoordinator();
        var originalEpoch = coordinator.CaptureEpoch();
        var oldSaveStarted = NewSignal();
        var releaseOldSave = NewSignal();

        var oldSave = coordinator.PersistIfCurrentAsync(
            originalEpoch,
            async cancellationToken =>
            {
                oldSaveStarted.TrySetResult();
                await releaseOldSave.Task.WaitAsync(
                    Timeout,
                    cancellationToken);
            });
        await oldSaveStarted.Task.WaitAsync(Timeout);

        using var cancellation = new CancellationTokenSource();
        var cancelledRelocation =
            coordinator.AdvanceAndPersistAsync(
                _ => Task.CompletedTask,
                cancellation.Token);
        cancellation.Cancel();

        await ExpectThrowsAsync<OperationCanceledException>(
            () => cancelledRelocation,
            "cancelled relocation surfaces cancellation");
        Check.Equal(
            originalEpoch,
            coordinator.CaptureEpoch(),
            "cancelled relocation restores original epoch");

        var successfulRelocationStarted = NewSignal();
        var successfulRelocation =
            coordinator.AdvanceAndPersistAsync(
                _ =>
                {
                    successfulRelocationStarted.TrySetResult();
                    return Task.CompletedTask;
                });
        Check.True(
            !successfulRelocationStarted.Task.IsCompleted,
            "replacement relocation still waits for in-flight save");

        releaseOldSave.TrySetResult();
        Check.True(
            await oldSave.WaitAsync(Timeout),
            "old save completes after relocation cancellation");
        var newEpoch =
            await successfulRelocation.WaitAsync(Timeout);

        Check.Equal(
            originalEpoch + 1,
            newEpoch,
            "relocation gate is reusable after cancellation");
        Check.True(
            successfulRelocationStarted.Task.IsCompleted,
            "replacement relocation callback runs");
    }

    private static async Task
        CheckCancelledSaveWaitDoesNotLeakGateAsync()
    {
        var coordinator =
            new CharacterPositionPersistenceCoordinator();
        var epoch = coordinator.CaptureEpoch();
        var blockerStarted = NewSignal();
        var releaseBlocker = NewSignal();

        var blocker = coordinator.PersistIfCurrentAsync(
            epoch,
            async cancellationToken =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(
                    Timeout,
                    cancellationToken);
            });
        await blockerStarted.Task.WaitAsync(Timeout);

        using var cancellation = new CancellationTokenSource();
        var cancelledCallbackCount = 0;
        var cancelledSave = coordinator.PersistIfCurrentAsync(
            epoch,
            _ =>
            {
                cancelledCallbackCount++;
                return Task.CompletedTask;
            },
            cancellation.Token);
        cancellation.Cancel();

        await ExpectThrowsAsync<OperationCanceledException>(
            () => cancelledSave,
            "cancelled queued save surfaces cancellation");
        Check.Equal(
            0,
            cancelledCallbackCount,
            "cancelled queued save callback does not run");

        releaseBlocker.TrySetResult();
        Check.True(
            await blocker.WaitAsync(Timeout),
            "blocking save retains and releases its own gate");

        var accepted = await coordinator.PersistIfCurrentAsync(
            epoch,
            _ => Task.CompletedTask).WaitAsync(Timeout);
        Check.True(
            accepted,
            "persistence gate is reusable after waiter cancellation");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action().WaitAsync(Timeout);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }
}
