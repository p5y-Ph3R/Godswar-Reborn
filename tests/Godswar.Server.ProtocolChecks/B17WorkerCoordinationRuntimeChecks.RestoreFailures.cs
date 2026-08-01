using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Coordination;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B17WorkerCoordinationRuntimeChecks
{
    private static async Task CheckInCallWorkerRouteRecoveryAsync()
    {
        var clock = new ManualTimeProvider();
        var adapter = new RestoreFailureCoordination(
            new InMemoryWorkerCoordination(
                capacity: 16,
                maximumConcurrentOperations: 4,
                clock),
            CoordinationOperationStatus.NotFound,
            recoverDuringRouteLookup: true);
        await using var runtime = new WorkerCoordinationRuntime(
            adapter,
            CreateOptions(),
            CreateWorldOptions("worker-in-call-recovery"),
            contentRevision: "content-in-call-recovery",
            buildRevision: "build-in-call-recovery",
            clock);
        using var stop = new CancellationTokenSource();
        var run = runtime.RunAsync(stop.Token);
        await runtime.WaitUntilRegisteredAsync().WaitAsync(
            TimeSpan.FromSeconds(1));
        await runtime.PublishAvailableAsync();
        Check.True(
            runtime.TryResolveRoute(1, out var route),
            "in-call recovery fixture resolves its route");

        var ownershipLost = 0;
        await using var player = await runtime.AcquireAsync(
            accountId: 74,
            characterId: 174,
            new PlayerOwnershipFence(Guid.NewGuid(), 1),
            route,
            () => Interlocked.Increment(ref ownershipLost));
        Check.True(
            player is not null && player.IsCurrent,
            "in-call recovery fixture installs a current player lease");

        adapter.Arm();
        Check.True(
            await player!.PublishOnlineAsync(route) &&
            player.IsCurrent &&
            Volatile.Read(ref ownershipLost) == 0,
            "exact worker-route proof permits one bounded in-call reinstall");
        Check.True(
            adapter.UsedOneRecoveryDeadline,
            "renew, route proof, and reinstall share one absolute deadline");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task CheckTransientMissingWorkerRouteAsync()
    {
        var clock = new ManualTimeProvider();
        var adapter = new RestoreFailureCoordination(
            new InMemoryWorkerCoordination(
                capacity: 16,
                maximumConcurrentOperations: 4,
                clock),
            CoordinationOperationStatus.NotFound,
            hideRouteWhenArmed: true);
        await using var runtime = new WorkerCoordinationRuntime(
            adapter,
            CreateOptions(),
            CreateWorldOptions("worker-route-recovery"),
            contentRevision: "content-route-recovery",
            buildRevision: "build-route-recovery",
            clock);
        using var stop = new CancellationTokenSource();
        var run = runtime.RunAsync(stop.Token);
        await runtime.WaitUntilRegisteredAsync().WaitAsync(
            TimeSpan.FromSeconds(1));
        await runtime.PublishAvailableAsync();
        Check.True(
            runtime.TryResolveRoute(1, out var route),
            "route-recovery fixture resolves its route");

        var ownershipLost = 0;
        await using var player = await runtime.AcquireAsync(
            accountId: 73,
            characterId: 173,
            new PlayerOwnershipFence(Guid.NewGuid(), 1),
            route,
            () => Interlocked.Increment(ref ownershipLost));
        Check.True(
            player is not null && player.IsCurrent,
            "route-recovery fixture installs a current player lease");

        adapter.Arm();
        Check.True(
            !await player!.PublishOnlineAsync(route) &&
            player.IsCurrent &&
            Volatile.Read(ref ownershipLost) == 0,
            "missing disposable worker route fails closed without " +
            "inventing durable ownership loss");
        adapter.Disarm();
        Check.True(
            await player.PublishOnlineAsync(route) &&
            player.IsCurrent &&
            Volatile.Read(ref ownershipLost) == 0,
            "player lease resumes after worker-route reconstruction");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task CheckDefinitivePlayerLeaseLossAsync(
        CoordinationOperationStatus restoreStatus)
    {
        var clock = new ManualTimeProvider();
        var adapter = new RestoreFailureCoordination(
            new InMemoryWorkerCoordination(
                capacity: 16,
                maximumConcurrentOperations: 4,
                clock),
            restoreStatus);
        await using var runtime = new WorkerCoordinationRuntime(
            adapter,
            CreateOptions(),
            CreateWorldOptions(
                "worker-restore-" +
                restoreStatus.ToString().ToLowerInvariant()),
            contentRevision: "content-restore",
            buildRevision: "build-restore",
            clock);
        using var stop = new CancellationTokenSource();
        var run = runtime.RunAsync(stop.Token);
        await runtime.WaitUntilRegisteredAsync().WaitAsync(
            TimeSpan.FromSeconds(1));
        await runtime.PublishAvailableAsync();
        Check.True(
            runtime.TryResolveRoute(1, out var route),
            "restore-loss fixture resolves its route");

        var ownershipLost = 0;
        await using var player = await runtime.AcquireAsync(
            accountId: 72,
            characterId: 172,
            new PlayerOwnershipFence(Guid.NewGuid(), 1),
            route,
            () => Interlocked.Increment(ref ownershipLost));
        Check.True(
            player is not null && player.IsCurrent,
            "restore-loss fixture installs a current player lease");

        adapter.Arm();
        Check.True(
            !await player!.PublishOnlineAsync(route) &&
            !player.IsCurrent &&
            Volatile.Read(ref ownershipLost) == 1,
            "definitive missing Redis lease invalidates when restore " +
            $"returns {restoreStatus}");
        Check.True(
            !await player.PublishOnlineAsync(route) &&
            Volatile.Read(ref ownershipLost) == 1,
            "definitive ownership loss notification is exactly once");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class RestoreFailureCoordination(
        IWorkerCoordination inner,
        CoordinationOperationStatus restoreStatus,
        bool hideRouteWhenArmed = false,
        bool recoverDuringRouteLookup = false) :
        IWorkerCoordination
    {
        private readonly List<CoordinationDeadline> _recoveryDeadlines = [];
        private readonly object _deadlineGate = new();
        private int _armed;
        private int _recordRecoveryDeadlines;

        public bool UsedOneRecoveryDeadline
        {
            get
            {
                lock (_deadlineGate)
                {
                    return _recoveryDeadlines.Count == 4 &&
                        _recoveryDeadlines.Distinct().Count() == 1;
                }
            }
        }

        public void Arm()
        {
            if (recoverDuringRouteLookup)
            {
                Interlocked.Exchange(ref _recordRecoveryDeadlines, 1);
            }
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Disarm() =>
            Interlocked.Exchange(ref _armed, 0);

        public ValueTask<WorkerRegistrationResult> RegisterWorkerAsync(
            WorkerRegistrationRequest request,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.RegisterWorkerAsync(
                request,
                ttl,
                deadline,
                cancellationToken);

        public ValueTask<WorkerRegistrationResult> RenewWorkerAsync(
            WorkerRegistrationLease lease,
            CoordinatedWorkerState state,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.RenewWorkerAsync(
                lease,
                state,
                ttl,
                deadline,
                cancellationToken);

        public ValueTask<CoordinationOperationStatus> ReleaseWorkerAsync(
            WorkerRegistrationLease lease,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseWorkerAsync(
                lease,
                deadline,
                cancellationToken);

        public ValueTask<CoordinatedRouteLookup> FindRouteAsync(
            CoordinatedWorldRoute route,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            RecordRecoveryDeadline(deadline);
            if (Volatile.Read(ref _armed) != 0 && hideRouteWhenArmed)
            {
                return ValueTask.FromResult(new CoordinatedRouteLookup(
                    CoordinationOperationStatus.NotFound,
                    null));
            }
            if (Volatile.Read(ref _armed) != 0 &&
                recoverDuringRouteLookup)
            {
                Disarm();
            }
            return inner.FindRouteAsync(route, deadline, cancellationToken);
        }

        public ValueTask<PlayerLeaseResult> InstallPlayerLeaseAsync(
            PlayerLeaseInstallRequest request,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            RecordRecoveryDeadline(deadline);
            return Volatile.Read(ref _armed) == 0
                ? inner.InstallPlayerLeaseAsync(
                    request,
                    ttl,
                    deadline,
                    cancellationToken)
                : ValueTask.FromResult(
                    new PlayerLeaseResult(restoreStatus, null));
        }

        public ValueTask<PlayerLeaseResult> RenewPlayerLeaseAsync(
            CoordinatedPlayerLease lease,
            CoordinatedWorldRoute route,
            CoordinatedPresenceState presence,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            RecordRecoveryDeadline(deadline);
            return Volatile.Read(ref _armed) == 0
                ? inner.RenewPlayerLeaseAsync(
                    lease,
                    route,
                    presence,
                    ttl,
                    deadline,
                    cancellationToken)
                : ValueTask.FromResult(
                    new PlayerLeaseResult(
                        CoordinationOperationStatus.NotFound,
                        null));
        }

        public ValueTask<CoordinationOperationStatus>
            ReleasePlayerLeaseAsync(
                CoordinatedPlayerLease lease,
                CoordinationDeadline deadline,
                CancellationToken cancellationToken = default) =>
            inner.ReleasePlayerLeaseAsync(
                lease,
                deadline,
                cancellationToken);

        public ValueTask<PlayerLeaseLookup> FindPlayerLeaseAsync(
            int characterId,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.FindPlayerLeaseAsync(
                characterId,
                deadline,
                cancellationToken);

        public ValueTask<bool> CheckHealthAsync(
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.CheckHealthAsync(deadline, cancellationToken);

        public WorkerCoordinationSnapshot GetSnapshot() =>
            inner.GetSnapshot();

        private void RecordRecoveryDeadline(
            CoordinationDeadline deadline)
        {
            if (Volatile.Read(ref _recordRecoveryDeadlines) == 0)
            {
                return;
            }
            lock (_deadlineGate)
            {
                _recoveryDeadlines.Add(deadline);
            }
        }

        public ValueTask DisposeAsync() =>
            inner.DisposeAsync();
    }
}
