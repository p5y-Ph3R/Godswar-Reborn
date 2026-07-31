using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Infrastructure.Coordination;

/// <summary>
/// Process-local rollback implementation with the same fencing and TTL
/// semantics as the Redis adapter. It never becomes durable authority.
/// </summary>
internal sealed partial class InMemoryWorkerCoordination :
    IWorkerCoordination
{
    private readonly Dictionary<int, PlayerEntry> _players = [];
    private readonly Dictionary<int, int> _playerByAccount = [];
    private readonly Dictionary<WorldInstanceId, RouteEntry> _routes = [];
    private readonly Dictionary<ServerNodeId, WorkerEntry> _workers = [];
    private readonly int _capacity;
    private readonly int _maximumConcurrentOperations;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private long _accepted;
    private long _conflicts;
    private long _overloads;
    private long _timeouts;
    private long _unavailable;
    private DateTimeOffset _lastSuccess;
    private bool _disposed;

    public InMemoryWorkerCoordination(
        int capacity,
        int maximumConcurrentOperations,
        TimeProvider? timeProvider = null)
    {
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (maximumConcurrentOperations is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentOperations));
        }

        _capacity = capacity;
        _maximumConcurrentOperations = maximumConcurrentOperations;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<WorkerRegistrationResult> RegisterWorkerAsync(
        WorkerRegistrationRequest request,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateTtl(ttl);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(
                    new WorkerRegistrationResult(
                        Timeout(),
                        null));
            }

            var now = _timeProvider.GetUtcNow();
            CleanupExpired(now);
            if (_workers.TryGetValue(request.NodeId, out var current) &&
                current.Lease.BootId != request.BootId)
            {
                return ValueTask.FromResult(
                    new WorkerRegistrationResult(
                        Conflict(),
                        null));
            }
            if (!_workers.ContainsKey(request.NodeId) &&
                _workers.Count >= _capacity)
            {
                return ValueTask.FromResult(
                    new WorkerRegistrationResult(
                        Overloaded(),
                        null));
            }

            foreach (var route in request.Routes)
            {
                if (_routes.TryGetValue(
                        route.WorldInstanceId,
                        out var routeEntry) &&
                    (routeEntry.Route != route ||
                     routeEntry.NodeId != request.NodeId ||
                     routeEntry.BootId != request.BootId))
                {
                    return ValueTask.FromResult(
                        new WorkerRegistrationResult(
                            Conflict(),
                            null));
                }
            }

            var revision = current is null
                ? 1
                : checked(current.Lease.Revision + 1);
            RemoveWorkerRoutes(request.NodeId);
            var lease = new WorkerRegistrationLease(
                request.NodeId,
                request.BootId,
                revision,
                request.State,
                now + ttl);
            _workers[request.NodeId] = new WorkerEntry(
                lease,
                request.BuildRevision,
                request.ContentRevision,
                request.Capabilities.ToArray());
            foreach (var route in request.Routes)
            {
                _routes[route.WorldInstanceId] = new RouteEntry(
                    route,
                    request.NodeId,
                    request.BootId,
                    revision);
            }

            return ValueTask.FromResult(
                new WorkerRegistrationResult(
                    Accepted(CoordinationOperationStatus.Applied),
                    lease));
        }
    }

    public ValueTask<WorkerRegistrationResult> RenewWorkerAsync(
        WorkerRegistrationLease lease,
        CoordinatedWorkerState state,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (!lease.IsValid)
        {
            throw new ArgumentException(
                "A valid worker lease is required.",
                nameof(lease));
        }
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        ValidateTtl(ttl);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(
                    new WorkerRegistrationResult(
                        Timeout(),
                        null));
            }

            var now = _timeProvider.GetUtcNow();
            CleanupExpired(now);
            if (!_workers.TryGetValue(lease.NodeId, out var entry))
            {
                return ValueTask.FromResult(
                    new WorkerRegistrationResult(
                        CoordinationOperationStatus.NotFound,
                        null));
            }
            if (entry.Lease.BootId != lease.BootId ||
                entry.Lease.Revision != lease.Revision)
            {
                return ValueTask.FromResult(
                    new WorkerRegistrationResult(
                        Conflict(),
                        null));
            }

            var updated = entry.Lease with
            {
                State = state,
                ProvenUntilUtc = now + ttl
            };
            entry.Lease = updated;
            return ValueTask.FromResult(
                new WorkerRegistrationResult(
                    Accepted(CoordinationOperationStatus.Current),
                    updated));
        }
    }

    public ValueTask<CoordinationOperationStatus> ReleaseWorkerAsync(
        WorkerRegistrationLease lease,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult(
                    CoordinationOperationStatus.Unavailable);
            }
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(Timeout());
            }
            if (!_workers.TryGetValue(lease.NodeId, out var entry))
            {
                return ValueTask.FromResult(
                    CoordinationOperationStatus.NotFound);
            }
            if (entry.Lease.BootId != lease.BootId ||
                entry.Lease.Revision != lease.Revision)
            {
                return ValueTask.FromResult(Conflict());
            }

            _workers.Remove(lease.NodeId);
            RemoveWorkerRoutes(lease.NodeId);
            return ValueTask.FromResult(
                Accepted(CoordinationOperationStatus.Applied));
        }
    }

    public ValueTask<CoordinatedRouteLookup> FindRouteAsync(
        CoordinatedWorldRoute route,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        route.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(
                    new CoordinatedRouteLookup(Timeout(), null));
            }

            CleanupExpired(_timeProvider.GetUtcNow());
            if (!_routes.TryGetValue(
                    route.WorldInstanceId,
                    out var routeEntry) ||
                routeEntry.Route != route ||
                !_workers.TryGetValue(
                    routeEntry.NodeId,
                    out var worker) ||
                worker.Lease.BootId != routeEntry.BootId ||
                worker.Lease.Revision != routeEntry.WorkerRevision)
            {
                return ValueTask.FromResult(
                    new CoordinatedRouteLookup(
                        CoordinationOperationStatus.NotFound,
                        null));
            }

            var snapshot = new CoordinatedRouteSnapshot(
                route,
                routeEntry.NodeId,
                routeEntry.BootId,
                routeEntry.WorkerRevision,
                worker.Lease.State,
                worker.BuildRevision,
                worker.ContentRevision,
                worker.Lease.ProvenUntilUtc);
            return ValueTask.FromResult(
                new CoordinatedRouteLookup(
                    Accepted(CoordinationOperationStatus.Current),
                    snapshot));
        }
    }

    public ValueTask<bool> CheckHealthAsync(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var healthy = !_disposed && !DeadlineExpired(deadline);
            if (healthy)
            {
                Accepted(CoordinationOperationStatus.Current);
            }
            return ValueTask.FromResult(healthy);
        }
    }

    public WorkerCoordinationSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                CleanupExpired(_timeProvider.GetUtcNow());
            }
            return new WorkerCoordinationSnapshot(
                IsReady: !_disposed,
                _capacity,
                _maximumConcurrentOperations,
                InFlightOperations: 0,
                _routes.Count,
                _players.Count,
                _accepted,
                _conflicts,
                _timeouts,
                _unavailable,
                _overloads,
                CircuitOpenRejections: 0,
                _lastSuccess);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _players.Clear();
                _playerByAccount.Clear();
                _routes.Clear();
                _workers.Clear();
                _disposed = true;
            }
        }
        return ValueTask.CompletedTask;
    }

    private bool WorkerOwnsRoute(
        ServerNodeId nodeId,
        Guid bootId,
        CoordinatedWorldRoute route,
        bool allowDraining = false) =>
        _workers.TryGetValue(nodeId, out var worker) &&
        worker.Lease.BootId == bootId &&
        (worker.Lease.State == CoordinatedWorkerState.Available ||
         allowDraining &&
         worker.Lease.State == CoordinatedWorkerState.Draining) &&
        _routes.TryGetValue(route.WorldInstanceId, out var routeEntry) &&
        routeEntry.Route == route &&
        routeEntry.NodeId == nodeId &&
        routeEntry.BootId == bootId &&
        routeEntry.WorkerRevision == worker.Lease.Revision;

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var entry in _workers
                     .Where(entry =>
                         entry.Value.Lease.ProvenUntilUtc <= now)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _workers.Remove(entry);
            RemoveWorkerRoutes(entry);
        }
        foreach (var entry in _players
                     .Where(entry =>
                         entry.Value.Lease.ProvenUntilUtc <= now)
                     .Select(static entry => entry.Value.Lease)
                     .ToArray())
        {
            RemovePlayer(entry);
        }
    }

    private void RemoveWorkerRoutes(ServerNodeId nodeId)
    {
        foreach (var routeId in _routes
                     .Where(entry => entry.Value.NodeId == nodeId)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _routes.Remove(routeId);
        }
    }

    private CoordinationOperationStatus Accepted(
        CoordinationOperationStatus status)
    {
        _accepted++;
        _lastSuccess = _timeProvider.GetUtcNow();
        return status;
    }

    private CoordinationOperationStatus Conflict()
    {
        _conflicts++;
        return CoordinationOperationStatus.Conflict;
    }

    private CoordinationOperationStatus Timeout()
    {
        _timeouts++;
        return CoordinationOperationStatus.DeadlineExceeded;
    }

    private CoordinationOperationStatus Overloaded()
    {
        _overloads++;
        return CoordinationOperationStatus.Overloaded;
    }

    private bool DeadlineExpired(CoordinationDeadline deadline) =>
        deadline.Remaining(_timeProvider) <= TimeSpan.Zero;

    private static void ValidateTtl(TimeSpan ttl)
    {
        if (ttl < TimeSpan.FromSeconds(1) ||
            ttl > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                "Coordination TTL must be between one second and ten minutes.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            _unavailable++;
            throw new ObjectDisposedException(
                nameof(InMemoryWorkerCoordination));
        }
    }

    private sealed class WorkerEntry(
        WorkerRegistrationLease lease,
        string buildRevision,
        string contentRevision,
        string[] capabilities)
    {
        public WorkerRegistrationLease Lease { get; set; } = lease;

        public string BuildRevision { get; } = buildRevision;

        public string ContentRevision { get; } = contentRevision;

        public string[] Capabilities { get; } = capabilities;
    }

    private sealed record RouteEntry(
        CoordinatedWorldRoute Route,
        ServerNodeId NodeId,
        Guid BootId,
        long WorkerRevision);

}
