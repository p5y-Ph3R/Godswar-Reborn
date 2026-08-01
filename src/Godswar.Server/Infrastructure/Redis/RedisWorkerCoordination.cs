using System.Globalization;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisWorkerCoordination :
    IWorkerCoordination
{
    private readonly int _capacity;
    private readonly RedisCoordinationExecutor _executor;
    private readonly RedisCoordinationKeyBuilder _keys;
    private readonly int _maximumConcurrency;
    private readonly bool _ownsExecutor;
    private readonly Dictionary<Guid, CoordinatedWorldRoute[]>
        _routesByBoot = [];
    private readonly Dictionary<Guid, string> _contentByBoot = [];
    private readonly object _stateGate = new();
    private int _activePlayerLeases;
    private int _disposed;
    private int _registeredRoutes;

    public RedisWorkerCoordination(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        int capacity,
        int maximumConcurrency,
        bool ownsExecutor = false)
    {
        _executor = executor ??
            throw new ArgumentNullException(nameof(executor));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (maximumConcurrency is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency));
        }

        _capacity = capacity;
        _maximumConcurrency = maximumConcurrency;
        _ownsExecutor = ownsExecutor;
    }

    public async ValueTask<WorkerRegistrationResult> RegisterWorkerAsync(
        WorkerRegistrationRequest request,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateTtl(ttl);
        ThrowIfDisposed();
        var ttlMilliseconds = TtlMilliseconds(ttl);
        try
        {
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Worker,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.RegisterWorker,
                    [
                        _keys.Worker(request.NodeId),
                        _keys.RealmContent(request.RealmId)
                    ],
                    [
                        request.BootId.ToString("N"),
                        request.NodeId.ToString(),
                        request.BuildRevision,
                        request.ContentRevision,
                        ((byte)request.State).ToString(
                            CultureInfo.InvariantCulture),
                        string.Join(',', request.Capabilities),
                        request.RealmId.Value,
                        ttlMilliseconds
                    ]),
                cancellationToken);
            var response = RedisResultReader.Triple(result);
            if (response.Status is -1 or -2)
            {
                return WorkerFailure(
                    CoordinationOperationStatus.Conflict);
            }
            if (response.Status is not (1 or 2) ||
                response.Value <= 0)
            {
                return WorkerFailure(
                    CoordinationOperationStatus.Unavailable);
            }
            var until = RedisTimestamp(response.Timestamp);

            var lease = new WorkerRegistrationLease(
                request.NodeId,
                request.BootId,
                response.Value,
                request.State,
                until);
            var installed = new List<CoordinatedWorldRoute>(
                request.Routes.Count);
            foreach (var route in request.Routes)
            {
                var routeStatus = await RegisterRouteAsync(
                    route,
                    lease,
                    ttl,
                    deadline,
                    cancellationToken);
                if (routeStatus != CoordinationOperationStatus.Applied)
                {
                    await RollbackRegistrationAsync(
                        lease,
                        installed,
                        deadline);
                    return WorkerFailure(routeStatus);
                }
                installed.Add(route);
            }

            lock (_stateGate)
            {
                _routesByBoot[request.BootId] = request.Routes.ToArray();
                _contentByBoot[request.BootId] =
                    request.ContentRevision;
                _registeredRoutes = _routesByBoot.Values.Sum(
                    static routes => routes.Length);
            }
            return WorkerResult(
                response.Status == 1
                    ? CoordinationOperationStatus.Applied
                    : CoordinationOperationStatus.Current,
                lease);
        }
        catch (RedisCoordinationException error)
        {
            return WorkerFailure(error.Status);
        }
    }

    public async ValueTask<WorkerRegistrationResult> RenewWorkerAsync(
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
        ThrowIfDisposed();

        CoordinatedWorldRoute[] routes;
        string? contentRevision;
        lock (_stateGate)
        {
            routes = _routesByBoot.TryGetValue(
                    lease.BootId,
                    out var found)
                ? found
                : [];
            contentRevision = _contentByBoot.GetValueOrDefault(
                lease.BootId);
        }
        if (routes.Length == 0 || contentRevision is null)
        {
            return WorkerFailure(
                CoordinationOperationStatus.Conflict);
        }

        try
        {
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Worker,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.RenewWorker,
                    [
                        _keys.Worker(lease.NodeId),
                        _keys.RealmContent(routes[0].RealmId)
                    ],
                    [
                        lease.BootId.ToString("N"),
                        lease.Revision,
                        (int)state,
                        contentRevision,
                        TtlMilliseconds(ttl),
                        routes[0].RealmId.Value
                    ]),
                cancellationToken);
            var response = RedisResultReader.Pair(result);
            var status = response.Status;
            if (status <= 0)
            {
                return WorkerFailure(
                    status == 0
                        ? CoordinationOperationStatus.NotFound
                        : CoordinationOperationStatus.Conflict);
            }
            var until = RedisTimestamp(response.Value);

            foreach (var route in routes)
            {
                var routeStatus = await RenewRouteAsync(
                    route,
                    lease,
                    ttl,
                    deadline,
                    cancellationToken);
                if (routeStatus != CoordinationOperationStatus.Current)
                {
                    return WorkerFailure(routeStatus);
                }
            }

            return WorkerResult(
                CoordinationOperationStatus.Current,
                lease with
                {
                    State = state,
                    ProvenUntilUtc = until
                });
        }
        catch (RedisCoordinationException error)
        {
            return WorkerFailure(error.Status);
        }
    }

    public async ValueTask<CoordinationOperationStatus> ReleaseWorkerAsync(
        WorkerRegistrationLease lease,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (!lease.IsValid)
        {
            throw new ArgumentException(
                "A valid worker lease is required.",
                nameof(lease));
        }
        ThrowIfDisposed();

        CoordinatedWorldRoute[] routes;
        lock (_stateGate)
        {
            routes = _routesByBoot.TryGetValue(
                    lease.BootId,
                    out var found)
                ? found
                : [];
        }
        try
        {
            foreach (var route in routes)
            {
                await ReleaseRouteAsync(
                    route,
                    lease,
                    deadline,
                    cancellationToken);
            }
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Worker,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.ReleaseExact,
                    [_keys.Worker(lease.NodeId)],
                    [
                        "boot",
                        "revision",
                        lease.BootId.ToString("N"),
                        lease.Revision
                    ]),
                cancellationToken);
            var status = RedisStatus(
                RedisResultReader.Integer(result));
            if (status is
                    CoordinationOperationStatus.Applied or
                    CoordinationOperationStatus.NotFound)
            {
                lock (_stateGate)
                {
                    _routesByBoot.Remove(lease.BootId);
                    _contentByBoot.Remove(lease.BootId);
                    _registeredRoutes = _routesByBoot.Values.Sum(
                        static values => values.Length);
                }
            }
            _executor.RecordLogicalOutcome(
                RedisCoordinationOperationFamily.Worker,
                status);
            return status;
        }
        catch (RedisCoordinationException error)
        {
            _executor.RecordLogicalOutcome(
                RedisCoordinationOperationFamily.Worker,
                error.Status);
            return error.Status;
        }
    }

    public async ValueTask<CoordinatedRouteLookup> FindRouteAsync(
        CoordinatedWorldRoute route,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        route.Validate();
        ThrowIfDisposed();
        try
        {
            var routeEntries = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Route,
                deadline,
                database => database.HashGetAllAsync(
                    _keys.Route(route.WorldInstanceId)),
                cancellationToken);
            if (routeEntries.Length == 0)
            {
                return RouteLookup(
                    CoordinationOperationStatus.NotFound,
                    null);
            }

            var routeHash = new RedisHashReader(routeEntries);
            var foundRoute = ReadRoute(routeHash);
            var nodeId = new ServerNodeId(
                routeHash.RequiredString("node", 64));
            var bootId = routeHash.RequiredGuid("boot");
            var revision = routeHash.RequiredInt64("revision");
            if (foundRoute != route)
            {
                return RouteLookup(
                    CoordinationOperationStatus.Conflict,
                    null);
            }

            var workerEntries = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Route,
                deadline,
                database => database.HashGetAllAsync(
                    _keys.Worker(nodeId)),
                cancellationToken);
            if (workerEntries.Length == 0)
            {
                return RouteLookup(
                    CoordinationOperationStatus.NotFound,
                    null);
            }
            var worker = new RedisHashReader(workerEntries);
            if (worker.RequiredGuid("boot") != bootId ||
                worker.RequiredInt64("revision") != revision)
            {
                return RouteLookup(
                    CoordinationOperationStatus.Conflict,
                    null);
            }

            var snapshot = new CoordinatedRouteSnapshot(
                foundRoute,
                nodeId,
                bootId,
                revision,
                (CoordinatedWorkerState)worker.RequiredByte("state"),
                worker.RequiredString("build", 64),
                worker.RequiredString("content", 64),
                MinimumUntil(routeHash, worker));
            return RouteLookup(
                CoordinationOperationStatus.Current,
                snapshot);
        }
        catch (RedisCoordinationException error)
        {
            return RouteLookup(error.Status, null);
        }
        catch (Exception error)
            when (error is InvalidDataException or
                ArgumentException or
                OverflowException)
        {
            return RouteLookup(
                CoordinationOperationStatus.Unavailable,
                null);
        }
    }

    public async ValueTask<bool> CheckHealthAsync(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        try
        {
            var latency = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Health,
                deadline,
                static database => database.PingAsync(),
                cancellationToken);
            return latency >= TimeSpan.Zero;
        }
        catch (RedisCoordinationException)
        {
            return false;
        }
    }

    public WorkerCoordinationSnapshot GetSnapshot()
    {
        var redis = _executor.GetSnapshot();
        return new WorkerCoordinationSnapshot(
            redis.IsReady && Volatile.Read(ref _disposed) == 0,
            _capacity,
            _maximumConcurrency,
            redis.InFlight,
            Volatile.Read(ref _registeredRoutes),
            Volatile.Read(ref _activePlayerLeases),
            redis.Accepted,
            redis.Conflicts,
            redis.Timeouts,
            redis.Unavailable,
            redis.OverloadRejections,
            redis.CircuitOpenRejections,
            redis.LastSuccessAtUtc);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_stateGate)
        {
            _routesByBoot.Clear();
            _contentByBoot.Clear();
            _registeredRoutes = 0;
            _activePlayerLeases = 0;
        }
        if (_ownsExecutor)
        {
            await _executor.DisposeAsync();
        }
    }

}
