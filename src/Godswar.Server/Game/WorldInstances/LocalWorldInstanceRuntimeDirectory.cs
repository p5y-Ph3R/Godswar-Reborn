using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Process-local owner of live map runtimes. WorldInstanceId is authoritative;
/// the byte map index is only the Tempest open-world compatibility projection.
/// </summary>
internal sealed partial class LocalWorldInstanceRuntimeDirectory :
    IAsyncDisposable
{
    private static readonly TimeSpan DefaultOwnerInvocationTimeout =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultOwnerShutdownTimeout =
        TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _indexGate = new();
    private readonly Dictionary<WorldInstanceId, WorldInstanceRuntime>
        _runtimes = [];
    private readonly Dictionary<byte, WorldInstanceId>
        _tempestOpenWorldByMapId = [];
    private readonly IWorldInstanceRuntimeFactory _factory;
    private readonly TimeSpan _ownerInvocationTimeout;
    private readonly TimeSpan _ownerShutdownTimeout;
    private readonly LocalWorldInstancePlacementRegistry _placement;
    private volatile bool _disposed;

    public LocalWorldInstanceRuntimeDirectory(
        LocalWorldInstancePlacementRegistry placement,
        IWorldInstanceRuntimeFactory factory,
        TimeSpan? ownerInvocationTimeout = null,
        TimeSpan? ownerShutdownTimeout = null)
    {
        _placement = placement ??
            throw new ArgumentNullException(nameof(placement));
        _factory = factory ??
            throw new ArgumentNullException(nameof(factory));
        _ownerInvocationTimeout = ValidateOwnerTimeout(
            ownerInvocationTimeout ??
            DefaultOwnerInvocationTimeout,
            nameof(ownerInvocationTimeout));
        _ownerShutdownTimeout = ValidateOwnerTimeout(
            ownerShutdownTimeout ??
            DefaultOwnerShutdownTimeout,
            nameof(ownerShutdownTimeout));
    }

    public int MaximumRuntimes => _placement.MaximumInstances;

    public int MaximumCharacterAssignments =>
        _placement.MaximumPlayerAssignments;

    public async ValueTask<WorldInstanceRuntimeDirectoryResult>
        GetOrCreateTempestOpenWorldAsync(
            byte legacyMapId,
            int playerCapacity,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (TryFindTempestOpenWorldCore(
                    legacyMapId,
                    out var existing))
            {
                return existing.Descriptor.LifecycleState ==
                       WorldInstanceLifecycleState.Active
                    ? Result(
                        WorldInstanceRuntimeDirectoryStatus.ExistingDefault,
                        existing)
                    : Result(
                        WorldInstanceRuntimeDirectoryStatus.DefaultUnavailable,
                        existing);
            }

            return await CreateCoreAsync(
                RealmId.Tempest,
                WorldMapId.FromLegacy(legacyMapId),
                InstanceKind.OpenWorld,
                playerCapacity,
                createdAt,
                registerTempestDefault: true,
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<WorldInstanceRuntimeDirectoryResult>
        CreateInstancedAsync(
            RealmId realmId,
            WorldMapId contentMapId,
            InstanceKind kind,
            int playerCapacity,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
    {
        if (kind == InstanceKind.OpenWorld || !Enum.IsDefined(kind))
        {
            return Result(
                WorldInstanceRuntimeDirectoryStatus.InvalidInstanceKind);
        }

        if (!contentMapId.TryGetLegacyValue(out _))
        {
            return Result(
                WorldInstanceRuntimeDirectoryStatus.LegacyMapUnsupported);
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await CreateCoreAsync(
                realmId,
                contentMapId,
                kind,
                playerCapacity,
                createdAt,
                registerTempestDefault: false,
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public bool TryFind(
        WorldInstanceId instanceId,
        out WorldInstanceRuntime runtime)
    {
        lock (_indexGate)
        {
            return _runtimes.TryGetValue(instanceId, out runtime!);
        }
    }

    public bool TryFindTempestOpenWorld(
        byte legacyMapId,
        out WorldInstanceRuntime runtime)
    {
        lock (_indexGate)
        {
            return TryFindTempestOpenWorldCore(
                legacyMapId,
                out runtime!);
        }
    }

    public IReadOnlyList<WorldInstanceRuntime> Snapshot()
    {
        lock (_indexGate)
        {
            return _runtimes.Values
                .OrderBy(static runtime => runtime.RealmId.Value)
                .ThenBy(static runtime => runtime.ContentMapId.Value)
                .ThenBy(static runtime => runtime.InstanceId.Value)
                .ToArray();
        }
    }

    public WorldInstanceRuntimeDirectorySnapshot GetSnapshot()
    {
        lock (_indexGate)
        {
            return new WorldInstanceRuntimeDirectorySnapshot(
                _runtimes.Count,
                _tempestOpenWorldByMapId.Count,
                MaximumRuntimes);
        }
    }

    public ValueTask<WorldInstancePlacementResult> AssignCharacterAsync(
        int characterId,
        WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _placement.AssignCharacterAsync(
            characterId,
            instanceId,
            cancellationToken);
    }

    public ValueTask<WorldInstancePlacementResult> TransferCharacterAsync(
        int characterId,
        WorldInstanceId expectedSourceInstanceId,
        WorldInstanceId targetInstanceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _placement.TransferCharacterAsync(
            characterId,
            expectedSourceInstanceId,
            targetInstanceId,
            cancellationToken);
    }

    public ValueTask<WorldInstancePlacementResult> ReleaseCharacterAsync(
        int characterId,
        WorldInstanceId expectedInstanceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _placement.ReleaseCharacterAsync(
            characterId,
            expectedInstanceId,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _mutationGate.WaitAsync();
        WorldInstanceRuntime[] runtimes;
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_indexGate)
            {
                runtimes = _runtimes.Values.ToArray();
                _runtimes.Clear();
                _tempestOpenWorldByMapId.Clear();
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        foreach (var runtime in runtimes)
        {
            await runtime.DisposeAsync();
        }
    }

    private async ValueTask<WorldInstanceRuntimeDirectoryResult>
        CreateCoreAsync(
            RealmId realmId,
            WorldMapId contentMapId,
            InstanceKind kind,
            int playerCapacity,
            DateTimeOffset createdAt,
            bool registerTempestDefault,
            CancellationToken cancellationToken)
    {
        var descriptor = WorldInstanceDescriptor.Create(
            realmId,
            WorldInstanceId.New(),
            contentMapId,
            kind,
            playerCapacity,
            createdAt);
        var runtime = _factory.Create(descriptor);
        try
        {
            ValidateFactoryResult(descriptor, runtime);
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }

        var registered = await _placement.RegisterAsync(
            descriptor,
            cancellationToken);
        if (!registered.Succeeded ||
            registered.Placement is null)
        {
            await runtime.DisposeAsync();
            return PlacementRejected(
                runtime: null,
                registered.Status);
        }

        var activation = await _placement.TransitionAsync(
            descriptor.InstanceId,
            registered.Placement.Descriptor.Revision,
            WorldInstanceLifecycleState.Active,
            createdAt,
            // Registration is already visible. Complete or compensate the
            // short local transaction even if the caller cancels now.
            CancellationToken.None);
        if (!activation.Succeeded ||
            activation.Placement is null)
        {
            await RetireFailedCreationAsync(
                descriptor.InstanceId,
                registered.Placement.Descriptor,
                createdAt);
            await runtime.DisposeAsync();
            return PlacementRejected(
                runtime: null,
                activation.Status);
        }

        runtime.BindDescriptor(activation.Placement.Descriptor);
        lock (_indexGate)
        {
            _runtimes.Add(runtime.InstanceId, runtime);
            if (registerTempestDefault)
            {
                var legacyMapId = checked((byte)contentMapId.Value);
                _tempestOpenWorldByMapId.Add(
                    legacyMapId,
                    runtime.InstanceId);
            }
        }

        return Result(
            WorldInstanceRuntimeDirectoryStatus.Created,
            runtime);
    }

    private async ValueTask RetireFailedCreationAsync(
        WorldInstanceId instanceId,
        WorldInstanceDescriptor descriptor,
        DateTimeOffset transitionedAt)
    {
        var closed = await _placement.TransitionAsync(
            instanceId,
            descriptor.Revision,
            WorldInstanceLifecycleState.Closed,
            transitionedAt,
            CancellationToken.None);
        if (closed.Succeeded)
        {
            await _placement.RemoveClosedAsync(
                instanceId,
                CancellationToken.None);
        }
    }

    private bool TryFindTempestOpenWorldCore(
        byte legacyMapId,
        out WorldInstanceRuntime runtime)
    {
        if (_tempestOpenWorldByMapId.TryGetValue(
                legacyMapId,
                out var instanceId) &&
            _runtimes.TryGetValue(instanceId, out runtime!))
        {
            return true;
        }

        runtime = default!;
        return false;
    }

    private static void ValidateFactoryResult(
        WorldInstanceDescriptor expected,
        WorldInstanceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Descriptor != expected)
        {
            throw new InvalidOperationException(
                "The runtime factory returned a mismatched descriptor.");
        }
    }

    private static WorldInstanceRuntimeDirectoryResult PlacementRejected(
        WorldInstanceRuntime? runtime,
        WorldInstancePlacementStatus status) =>
        new(
            WorldInstanceRuntimeDirectoryStatus.PlacementRejected,
            runtime,
            status);

    private static WorldInstanceRuntimeDirectoryResult Result(
        WorldInstanceRuntimeDirectoryStatus status,
        WorldInstanceRuntime? runtime = null) =>
        new(status, runtime);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static TimeSpan ValidateOwnerTimeout(
        TimeSpan timeout,
        string parameterName)
    {
        if (timeout < TimeSpan.FromMilliseconds(10) ||
            timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                timeout,
                "Owner timeout must be between 10 ms and 2 minutes.");
        }

        return timeout;
    }
}
