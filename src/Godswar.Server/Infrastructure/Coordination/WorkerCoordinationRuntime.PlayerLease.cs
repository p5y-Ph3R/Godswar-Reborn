using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Infrastructure.Coordination;

internal sealed partial class WorkerCoordinationRuntime
{
    private sealed class PlayerCoordinationLease :
        IPlayerCoordinationLease
    {
        private readonly IWorkerCoordination _coordination;
        private readonly CancellationTokenSource _disposeStop = new();
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly Action _ownershipLost;
        private readonly WorkerCoordinationRuntime _runtime;
        private readonly CoordinationRuntimeOptions _options;
        private readonly PlayerLeaseInstallRequest _request;
        private readonly TimeProvider _timeProvider;
        private readonly object _stateGate = new();
        private CoordinatedPlayerLease _lease;
        private MonotonicLeaseBudget _leaseBudget;
        private readonly Task _renewal;
        private int _disposed;
        private int _invalidated;

        public PlayerCoordinationLease(
            WorkerCoordinationRuntime runtime,
            IWorkerCoordination coordination,
            CoordinationRuntimeOptions options,
            PlayerLeaseInstallRequest request,
            CoordinatedPlayerLease lease,
            MonotonicLeaseBudget leaseBudget,
            Action ownershipLost,
            TimeProvider timeProvider)
        {
            _runtime = runtime;
            _coordination = coordination;
            _options = options;
            _request = request;
            _lease = lease;
            _leaseBudget = leaseBudget;
            _ownershipLost = ownershipLost;
            _timeProvider = timeProvider;
            _renewal = RunRenewalAsync(_disposeStop.Token);
        }

        public PlayerOwnershipFence Ownership =>
            _request.Ownership;

        public bool IsCurrent
        {
            get
            {
                if (Volatile.Read(ref _invalidated) != 0)
                {
                    return false;
                }

                lock (_stateGate)
                {
                    return _leaseBudget.IsCurrent(_timeProvider) &&
                        _runtime.IsWorkerCurrent(
                            _lease.WorkerBootId);
                }
            }
        }

        public async ValueTask<bool> PublishOnlineAsync(
            CoordinatedWorldRoute route,
            CancellationToken cancellationToken = default)
        {
            route.Validate();
            return await RenewAsync(
                route,
                CoordinatedPresenceState.Online,
                restoreMissing: true,
                useLatestState: false,
                cancellationToken);
        }

        public async ValueTask<bool> PublishEnteringAsync(
            CoordinatedWorldRoute route,
            CancellationToken cancellationToken = default)
        {
            route.Validate();
            return await RenewAsync(
                route,
                CoordinatedPresenceState.EnteringWorld,
                restoreMissing: true,
                useLatestState: false,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _disposeStop.Cancel();
            try
            {
                await _renewal;
            }
            catch (OperationCanceledException)
                when (_disposeStop.IsCancellationRequested)
            {
            }

            await _operationGate.WaitAsync();
            try
            {
                CoordinatedPlayerLease lease;
                lock (_stateGate)
                {
                    lease = _lease;
                }
                try
                {
                    await _coordination.ReleasePlayerLeaseAsync(
                        lease,
                        _runtime.Deadline(),
                        CancellationToken.None);
                }
                catch
                {
                    // The durable fence and Redis TTL prevent stale reuse.
                }
            }
            finally
            {
                _operationGate.Release();
                _operationGate.Dispose();
                _disposeStop.Dispose();
            }
        }

        private async Task RunRenewalAsync(
            CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(
                _options.PlayerLeaseRenewal,
                _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var renewed = await RenewAsync(
                    default,
                    default,
                    restoreMissing: true,
                    useLatestState: true,
                    cancellationToken);
                if (renewed)
                {
                    continue;
                }

                if (!IsLeaseBudgetCurrent())
                {
                    Invalidate();
                    return;
                }
            }
        }

        private async ValueTask<bool> RenewAsync(
            CoordinatedWorldRoute route,
            CoordinatedPresenceState presence,
            bool restoreMissing,
            bool useLatestState,
            CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _invalidated) != 0)
                {
                    return false;
                }

                CoordinatedPlayerLease current;
                lock (_stateGate)
                {
                    current = _lease;
                }
                if (useLatestState)
                {
                    route = current.Route;
                    presence = _runtime.DesiredState() ==
                            CoordinatedWorkerState.Draining
                        ? CoordinatedPresenceState.Draining
                        : current.Presence;
                }
                var budget = MonotonicLeaseBudget.Capture(
                    _timeProvider,
                    _options.PlayerLeaseTtl);
                var deadline = _runtime.Deadline();
                var result =
                    await _coordination.RenewPlayerLeaseAsync(
                        current,
                        route,
                        presence,
                        _options.PlayerLeaseTtl,
                        deadline,
                        cancellationToken);
                if (result.Succeeded && result.Lease is { } renewed)
                {
                    lock (_stateGate)
                    {
                        _lease = renewed;
                        _leaseBudget = budget;
                    }
                    return true;
                }

                if (restoreMissing &&
                    result.Status ==
                        CoordinationOperationStatus.NotFound)
                {
                    return await RestoreAsync(
                        route,
                        presence,
                        deadline,
                        cancellationToken);
                }
                if (result.Status ==
                    CoordinationOperationStatus.Conflict)
                {
                    Invalidate();
                }
                return false;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private async ValueTask<bool> RestoreAsync(
            CoordinatedWorldRoute route,
            CoordinatedPresenceState presence,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
        {
            if (!_runtime.IsWorkerCurrent(_request.WorkerBootId))
            {
                Invalidate();
                return false;
            }

            var reinstall = _request with
            {
                Route = route,
                Presence = presence
            };
            var budget = MonotonicLeaseBudget.Capture(
                _timeProvider,
                _options.PlayerLeaseTtl);
            var result =
                await _coordination.InstallPlayerLeaseAsync(
                    reinstall,
                    _options.PlayerLeaseTtl,
                    deadline,
                    cancellationToken);
            if (result.Succeeded && result.Lease is { } restored)
            {
                lock (_stateGate)
                {
                    _lease = restored;
                    _leaseBudget = budget;
                }
                return true;
            }
            if (result.Status ==
                CoordinationOperationStatus.Conflict)
            {
                Invalidate();
                return false;
            }

            // A failed reinstall can race the worker heartbeat after an empty
            // disposable keyspace. Prove the exact worker route before making
            // one bounded retry. A missing/unavailable route remains
            // fail-closed and retries only until the local lease budget ends.
            var routeLookup = await _coordination.FindRouteAsync(
                route,
                deadline,
                cancellationToken);
            if (!routeLookup.IsFound)
            {
                return false;
            }
            var routeProjection = routeLookup.Route!;
            if (routeProjection.NodeId != _request.NodeId ||
                routeProjection.BootId != _request.WorkerBootId ||
                routeProjection.Route != route ||
                routeProjection.WorkerState !=
                    CoordinatedWorkerState.Available)
            {
                Invalidate();
                return false;
            }

            result = await _coordination.InstallPlayerLeaseAsync(
                reinstall,
                _options.PlayerLeaseTtl,
                deadline,
                cancellationToken);
            if (result.Succeeded && result.Lease is { } retried)
            {
                lock (_stateGate)
                {
                    _lease = retried;
                    _leaseBudget = budget;
                }
                return true;
            }

            Invalidate();
            return false;
        }

        private bool IsLeaseBudgetCurrent()
        {
            lock (_stateGate)
            {
                return _leaseBudget.IsCurrent(_timeProvider);
            }
        }

        private void Invalidate()
        {
            if (Interlocked.Exchange(ref _invalidated, 1) != 0)
            {
                return;
            }

            try
            {
                _ownershipLost();
            }
            catch
            {
                // Ownership loss is authoritative even if notification fails.
            }
        }
    }
}
