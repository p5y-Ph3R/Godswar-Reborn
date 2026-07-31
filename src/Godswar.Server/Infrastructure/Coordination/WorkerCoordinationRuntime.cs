using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Infrastructure.Coordination;

/// <summary>
/// Publishes one exact, statically configured worker incarnation and issues
/// short-lived player route leases backed by PostgreSQL ownership fences.
/// No coordination I/O runs on an ECS simulation tick.
/// </summary>
internal sealed partial class WorkerCoordinationRuntime :
    IPlayerCoordinationLeaseIssuer,
    IWorkerCoordinationReadinessSource,
    IAsyncDisposable
{
    private readonly IWorkerCoordination _coordination;
    private readonly CoordinationRuntimeOptions _options;
    private readonly WorkerRegistrationRequest _registration;
    private readonly Dictionary<byte, CoordinatedWorldRoute> _routes;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _registered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposeStop = new();
    private readonly SemaphoreSlim _workerOperationGate = new(1, 1);
    private readonly object _drainPublicationGate = new();
    private readonly object _stateGate = new();
    private Task _drainPublication = Task.CompletedTask;
    private MonotonicLeaseBudget _workerLeaseBudget;
    private WorkerRegistrationLease? _workerLease;
    private bool _coordinationReady;
    private int _desiredState =
        (int)CoordinatedWorkerState.Draining;
    private int _availabilityAuthorized;
    private int _drainRequested;
    private int _disposed;
    private int _runStarted;

    public WorkerCoordinationRuntime(
        IWorkerCoordination coordination,
        CoordinationRuntimeOptions options,
        WorldInstanceRuntimeOptions worldInstances,
        string contentRevision,
        string buildRevision,
        TimeProvider? timeProvider = null)
    {
        _coordination = coordination ??
            throw new ArgumentNullException(nameof(coordination));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(worldInstances);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildRevision);
        _timeProvider = timeProvider ?? TimeProvider.System;

        var routes = worldInstances.StaticOpenWorldInstances
            .Select(static route => new CoordinatedWorldRoute(
                route.ProcessRealmId,
                route.ProcessMapId,
                route.ProcessWorldInstanceId))
            .ToArray();
        if (routes.Length == 0)
        {
            throw new InvalidDataException(
                "Redis worker coordination requires at least one exact " +
                "static open-world route.");
        }

        _routes = routes.ToDictionary(
            static route => checked((byte)route.MapId.Value));
        _registration = new WorkerRegistrationRequest
        {
            NodeId = worldInstances.ProcessServerNodeId,
            BootId = Guid.NewGuid(),
            BuildRevision = BoundRevision(buildRevision),
            ContentRevision = BoundRevision(contentRevision),
            State = CoordinatedWorkerState.Draining,
            Capabilities = ["open-world-v1", "player-fence-v1"],
            Routes = routes
        };
        _registration.Validate();
    }

    public bool IsEnabled => true;

    public ServerNodeId NodeId => _registration.NodeId;

    public bool IsReady
    {
        get
        {
            lock (_stateGate)
            {
                return _coordinationReady &&
                    _workerLease is { } lease &&
                    lease.State == CoordinatedWorkerState.Available &&
                    _workerLeaseBudget.IsCurrent(_timeProvider);
            }
        }
    }

    public WorkerCoordinationSnapshot GetSnapshot()
    {
        var snapshot = _coordination.GetSnapshot();
        return snapshot with { IsReady = IsReady };
    }

    public Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);

    public Task WaitUntilRegisteredAsync(
        CancellationToken cancellationToken = default) =>
        _registered.Task.WaitAsync(cancellationToken);

    public async Task PublishAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Volatile.Read(ref _runStarted) == 0)
        {
            throw new InvalidOperationException(
                "Worker coordination must be running before availability " +
                "can be published.");
        }

        await WaitUntilRegisteredAsync(cancellationToken);
        ThrowIfDrainRequested();
        Volatile.Write(ref _availabilityAuthorized, 1);
        Interlocked.Exchange(
            ref _desiredState,
            (int)CoordinatedWorkerState.Available);
        if (Volatile.Read(ref _drainRequested) != 0)
        {
            RevokeAvailability();
            throw new InvalidOperationException(
                "A draining worker cannot publish availability.");
        }

        try
        {
            await RenewOrRestoreAsync(
                cancellationToken,
                requireSuccess: true);
            if (!IsReady ||
                Volatile.Read(ref _drainRequested) != 0)
            {
                throw new WorkerCoordinationUnavailableException(
                    "publish available",
                    CoordinationOperationStatus.Unavailable);
            }
            _ready.TrySetResult();
        }
        catch
        {
            RevokeAvailability();
            QueueDrainPublication();
            throw;
        }
    }

    public void BeginDrain()
    {
        Interlocked.Exchange(ref _drainRequested, 1);
        RevokeAvailability();
        QueueDrainPublication();
    }

    private void RevokeAvailability()
    {
        Volatile.Write(ref _availabilityAuthorized, 0);
        Interlocked.Exchange(
            ref _desiredState,
            (int)CoordinatedWorkerState.Draining);
        SetNotReady();
    }

    public bool TryResolveRoute(
        byte legacyMapId,
        out CoordinatedWorldRoute route) =>
        _routes.TryGetValue(legacyMapId, out route);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "The worker coordination runtime can run only once.");
        }

        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeStop.Token);
        try
        {
            await RegisterAsync(lifetime.Token);
            _registered.TrySetResult();
            using var timer = new PeriodicTimer(
                _options.ServerHeartbeat,
                _timeProvider);
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await RenewOrRestoreAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException)
            when (lifetime.IsCancellationRequested)
        {
            _registered.TrySetCanceled(lifetime.Token);
            _ready.TrySetCanceled(lifetime.Token);
        }
        catch (Exception error)
        {
            SetNotReady();
            _registered.TrySetException(error);
            _ready.TrySetException(error);
            throw;
        }
        finally
        {
            await ReleaseWorkerBestEffortAsync();
            SetNotReady();
        }
    }

    public async ValueTask<IPlayerCoordinationLease?> AcquireAsync(
        int accountId,
        int characterId,
        Application.Characters.PlayerOwnershipFence ownership,
        CoordinatedWorldRoute route,
        Action ownershipLost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownershipLost);
        route.Validate();
        var worker = GetCurrentAvailableWorkerLease();
        if (worker is null ||
            !_routes.TryGetValue(
                checked((byte)route.MapId.Value),
                out var expected) ||
            expected != route)
        {
            return null;
        }

        var request = new PlayerLeaseInstallRequest
        {
            AccountId = accountId,
            CharacterId = characterId,
            Ownership = ownership,
            LeaseToken = Guid.NewGuid(),
            NodeId = worker.Value.NodeId,
            WorkerBootId = worker.Value.BootId,
            Route = route,
            Presence = CoordinatedPresenceState.EnteringWorld
        };
        var playerLeaseBudget = MonotonicLeaseBudget.Capture(
            _timeProvider,
            _options.PlayerLeaseTtl);
        var result = await _coordination.InstallPlayerLeaseAsync(
            request,
            _options.PlayerLeaseTtl,
            Deadline(),
            cancellationToken);
        if (!result.Succeeded || result.Lease is null)
        {
            return null;
        }

        return new PlayerCoordinationLease(
            this,
            _coordination,
            _options,
            request,
            result.Lease,
            playerLeaseBudget,
            ownershipLost,
            _timeProvider);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeStop.Cancel();
        Task drainPublication;
        lock (_drainPublicationGate)
        {
            drainPublication = _drainPublication;
        }
        await drainPublication;
        _disposeStop.Dispose();
        await _coordination.DisposeAsync();
        _workerOperationGate.Dispose();
    }

    internal CoordinationDeadline Deadline() =>
        CoordinationDeadline.FromNow(
            _options.OperationTimeout,
            _timeProvider);

    internal bool IsWorkerCurrent(Guid bootId)
    {
        lock (_stateGate)
        {
            return _workerLease is { } lease &&
                lease.BootId == bootId &&
                _workerLeaseBudget.IsCurrent(_timeProvider);
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        await _workerOperationGate.WaitAsync(cancellationToken);
        try
        {
            await RegisterCoreAsync(cancellationToken);
        }
        finally
        {
            _workerOperationGate.Release();
        }
    }

    private async Task RegisterCoreAsync(
        CancellationToken cancellationToken)
    {
        var request = _registration with
        {
            State = DesiredState()
        };
        var budget = MonotonicLeaseBudget.Capture(
            _timeProvider,
            _options.ServerTtl);
        var result = await _coordination.RegisterWorkerAsync(
            request,
            _options.ServerTtl,
            Deadline(),
            cancellationToken);
        if (!result.Succeeded || result.Lease is null)
        {
            throw new WorkerCoordinationUnavailableException(
                "register",
                result.Status);
        }

        SetWorkerLease(result.Lease.Value, budget);
    }

    private async Task RenewOrRestoreAsync(
        CancellationToken cancellationToken,
        bool requireSuccess = false)
    {
        await _workerOperationGate.WaitAsync(cancellationToken);
        try
        {
            await RenewOrRestoreCoreAsync(
                cancellationToken,
                requireSuccess);
        }
        finally
        {
            _workerOperationGate.Release();
        }
    }

    private async Task RenewOrRestoreCoreAsync(
        CancellationToken cancellationToken,
        bool requireSuccess)
    {
        var current = GetCurrentWorkerLease();
        if (current is null)
        {
            await RegisterCoreAsync(cancellationToken);
            return;
        }
        var budget = MonotonicLeaseBudget.Capture(
            _timeProvider,
            _options.ServerTtl);
        var result = await _coordination.RenewWorkerAsync(
            current.Value,
            DesiredState(),
            _options.ServerTtl,
            Deadline(),
            cancellationToken);
        if (result.Succeeded && result.Lease is { } renewed)
        {
            SetWorkerLease(renewed, budget);
            return;
        }

        SetNotReady();
        if (result.Status == CoordinationOperationStatus.NotFound)
        {
            await RegisterCoreAsync(cancellationToken);
            return;
        }
        if (result.Status == CoordinationOperationStatus.Conflict ||
            requireSuccess)
        {
            throw new WorkerCoordinationUnavailableException(
                "renew",
                result.Status);
        }
    }

    private async ValueTask ReleaseWorkerBestEffortAsync()
    {
        await _workerOperationGate.WaitAsync(CancellationToken.None);
        try
        {
            var lease = GetCurrentWorkerLease();
            if (lease is null)
            {
                return;
            }

            try
            {
                var draining = await _coordination.RenewWorkerAsync(
                    lease.Value,
                    CoordinatedWorkerState.Draining,
                    _options.ServerTtl,
                    Deadline(),
                    CancellationToken.None);
                await _coordination.ReleaseWorkerAsync(
                    draining.Lease ?? lease.Value,
                    Deadline(),
                    CancellationToken.None);
            }
            catch
            {
                // Short TTLs are the final cleanup boundary during an outage.
            }
        }
        finally
        {
            _workerOperationGate.Release();
        }
    }

    private void QueueDrainPublication()
    {
        lock (_drainPublicationGate)
        {
            if (!_drainPublication.IsCompleted)
            {
                return;
            }
            _drainPublication = PublishDrainingBestEffortAsync();
        }
    }

    private async Task PublishDrainingBestEffortAsync()
    {
        if (GetCurrentWorkerLease() is null)
        {
            return;
        }
        try
        {
            await RenewOrRestoreAsync(
                CancellationToken.None,
                requireSuccess: false);
        }
        catch
        {
            // The supervised heartbeat retries until shutdown. The worker is
            // already locally not-ready and its short Redis TTL fails closed.
        }
    }

    private WorkerRegistrationLease? GetCurrentWorkerLease()
    {
        lock (_stateGate)
        {
            return _workerLease;
        }
    }

    private WorkerRegistrationLease? GetCurrentAvailableWorkerLease()
    {
        lock (_stateGate)
        {
            return _coordinationReady &&
                _workerLease is { } lease &&
                lease.State == CoordinatedWorkerState.Available &&
                _workerLeaseBudget.IsCurrent(_timeProvider)
                    ? lease
                    : null;
        }
    }

    private void SetWorkerLease(
        WorkerRegistrationLease lease,
        MonotonicLeaseBudget budget)
    {
        lock (_stateGate)
        {
            _workerLease = lease;
            _workerLeaseBudget = budget;
            _coordinationReady =
                lease.State == CoordinatedWorkerState.Available &&
                Volatile.Read(ref _availabilityAuthorized) != 0 &&
                Volatile.Read(ref _drainRequested) == 0;
        }
    }

    private void SetNotReady()
    {
        lock (_stateGate)
        {
            _coordinationReady = false;
        }
    }

    private CoordinatedWorkerState DesiredState() =>
        (CoordinatedWorkerState)Volatile.Read(ref _desiredState);

    private void ThrowIfDrainRequested()
    {
        if (Volatile.Read(ref _drainRequested) != 0)
        {
            throw new InvalidOperationException(
                "A draining worker cannot publish availability.");
        }
    }

    private static string BoundRevision(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <=
            WorkerRegistrationRequest.MaximumBuildRevisionLength
                ? trimmed
                : trimmed[
                    ..WorkerRegistrationRequest.MaximumBuildRevisionLength];
    }
}

internal sealed class WorkerCoordinationUnavailableException :
    Exception
{
    public WorkerCoordinationUnavailableException(
        string operation,
        CoordinationOperationStatus status)
        : base(
            $"Worker coordination {operation} failed with status {status}.")
    {
        Status = status;
    }

    public CoordinationOperationStatus Status { get; }
}
