using System.Diagnostics;
using Godswar.Server.Application.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationRunnerChecks
{
    private static async Task CheckInterruptedContinuationRollbackAsync()
    {
        var reader = new InterruptedContinuationReader();
        var options = Options(batchSize: 1);
        options.MaximumCharactersPerRun = 2;
        var runner = new ReconciliationRunner(reader, options);
        using var cancellation = new CancellationTokenSource();
        var interrupted = runner.RunScheduledAsync(cancellation.Token);

        await reader.BlockingReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(
            () => interrupted,
            "an interrupted scheduled scan observes caller cancellation");

        var restarted = await runner.RunScheduledAsync();
        Check.Equal(
            (int)ReconciliationRunStatus.Completed,
            (int)restarted.Status,
            "a replacement scheduled scan completes");
        Check.True(
            reader.RestartRequests.SequenceEqual([0L]),
            "an interrupted run commits no partial continuation cursor");
    }

    private sealed class InterruptedContinuationReader :
        IReconciliationReader
    {
        private int _opens;

        public TaskCompletionSource BlockingReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<long> RestartRequests { get; } = [];

        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReconciliationSnapshot>(
                Interlocked.Increment(ref _opens) == 1
                    ? new PartialThenBlockingSnapshot(
                        BlockingReadStarted)
                    : new RestartSnapshot(RestartRequests));
        }
    }

    private sealed class PartialThenBlockingSnapshot(
        TaskCompletionSource blockingReadStarted) :
        IReconciliationSnapshot
    {
        public async Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken)
        {
            if (afterCharacterKey == 0)
            {
                return new ReconciliationPage(10, 1, false, []);
            }

            blockingReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RestartSnapshot(List<long> requests) :
        IReconciliationSnapshot
    {
        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken)
        {
            requests.Add(afterCharacterKey);
            return Task.FromResult(new ReconciliationPage(
                20,
                1,
                true,
                []));
        }

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(
                afterOutboxKey,
                0,
                true,
                []));

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationOutboxPositionPage(
                after,
                0,
                true,
                []));

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
