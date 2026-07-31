using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationWorkerChecks
{
    private static async Task CheckCompletedFindingRemainsVisibleAsync()
    {
        var options = Options(enabled: true);
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new FindingReader(),
                options),
            options);
        using var shutdown = new CancellationTokenSource();
        var run = worker.RunAsync(shutdown.Token);
        var completed = await WaitForSnapshotAsync(
            worker,
            snapshot => snapshot.FirstPassCompleted);

        Check.Equal(
            1L,
            completed.LastFindingCount,
            "a completed sweep remains operationally non-clean when it " +
            "contains an accumulated finding");
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FindingReader : IReconciliationReader
    {
        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReconciliationSnapshot>(
                new FindingSnapshot());
    }

    private sealed class FindingSnapshot : IReconciliationSnapshot
    {
        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(
                1,
                1,
                true,
                [
                    new ReconciliationCategoryCount(
                        ReconciliationCategory.WalletBalanceMismatch,
                        1)
                ]));

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
