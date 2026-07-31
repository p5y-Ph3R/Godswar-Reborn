using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Backhaul;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly object _worldInstanceDirectoryGate = new();
    private readonly WorldInstanceRuntimeOptions _worldInstanceOptions =
        new();
    private LocalWorldInstanceRuntimeDirectory? _worldInstanceDirectory;
    private bool _worldInstanceDirectoryDisposed;

    internal async ValueTask<WorldInstanceRuntimeDirectoryResult>
        CreateLocalWorldInstanceAsync(
            RealmId realmId,
            WorldMapId contentMapId,
            InstanceKind kind,
            int playerCapacity,
            CancellationToken cancellationToken = default)
    {
        return await WorldInstances.CreateInstancedAsync(
            realmId,
            contentMapId,
            kind,
            playerCapacity,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    internal bool TryGetWorldInstance(
        WorldInstanceId instanceId,
        out WorldInstanceDescriptor descriptor)
    {
        if (WorldInstances.TryFind(instanceId, out var runtime))
        {
            descriptor = runtime.Descriptor;
            return true;
        }

        descriptor = default!;
        return false;
    }

    internal bool AcceptsGatewayAdmission(
        GatewayWorldAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return admission.TargetNodeId ==
                _worldInstanceOptions.ProcessServerNodeId &&
            _worldInstanceOptions.TryFindStaticOpenWorld(
                admission.RealmId,
                admission.MapId,
                out var expectedInstanceId) &&
            expectedInstanceId == admission.WorldInstanceId;
    }

    internal bool IsSessionInWorldInstance(
        ClientSession session,
        WorldInstanceId instanceId) =>
        _sessions.TryGetValue(session, out var context) &&
        context.WorldInstanceId == instanceId &&
        TryGetWorldInstance(context, out _);

    internal bool TryGetSessionWorldInstanceId(
        ClientSession session,
        out WorldInstanceId instanceId)
    {
        if (_sessions.TryGetValue(session, out var context) &&
            TryGetWorldInstance(context, out _))
        {
            instanceId = context.WorldInstanceId;
            return true;
        }

        instanceId = default;
        return false;
    }

    internal WorldInstanceRuntimeDirectorySnapshot
        GetWorldInstanceDirectorySnapshot() =>
        WorldInstances.GetSnapshot();

    internal SingleOwnerMailboxSnapshot GetWorldInstanceOwnerSnapshot(
        WorldInstanceId instanceId)
    {
        return GetRequiredWorldInstance(instanceId)
            .Owner
            .GetSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        LocalWorldInstanceRuntimeDirectory? directory;
        lock (_worldInstanceDirectoryGate)
        {
            if (_worldInstanceDirectoryDisposed)
            {
                return;
            }

            _worldInstanceDirectoryDisposed = true;
            directory = _worldInstanceDirectory;
            _worldInstanceDirectory = null;
        }

        if (directory is not null)
        {
            await directory.DisposeAsync();
        }
    }

    private LocalWorldInstanceRuntimeDirectory WorldInstances
    {
        get
        {
            lock (_worldInstanceDirectoryGate)
            {
                ObjectDisposedException.ThrowIf(
                    _worldInstanceDirectoryDisposed,
                    this);
                return _worldInstanceDirectory ??=
                    new LocalWorldInstanceRuntimeDirectory(
                        new LocalWorldInstancePlacementRegistry(
                            _worldInstanceOptions.ProcessServerNodeId,
                            _worldInstanceOptions.MaximumRuntimes,
                            _worldInstanceOptions
                                .MaximumPlayerAssignments,
                            _worldInstanceOptions
                                .MaximumRetiredInstanceIds),
                        new MapWorldInstanceRuntimeFactory(
                            _monsterRuntimeMode,
                            _playerRuntimeMode,
                            _worldInstanceOptions.MailboxCapacity,
                            _worldInstanceOptions
                                .ShutdownDrainTimeout),
                        _worldInstanceOptions.OwnerInvocationTimeout,
                        _worldInstanceOptions.ShutdownDrainTimeout);
            }
        }
    }

    private WorldInstanceRuntime GetOrCreateDefaultWorldInstance(
        byte legacyMapId)
    {
        var mapId = WorldMapId.FromLegacy(legacyMapId);
        var hasAssignedInstance =
            _worldInstanceOptions.TryFindStaticOpenWorld(
                RealmId.Tempest,
                mapId,
                out var instanceId);
        if (!hasAssignedInstance &&
            _worldInstanceOptions.RequireStaticOpenWorldOwnership)
        {
            throw new InvalidOperationException(
                $"Worker does not own a configured open-world route for " +
                $"Tempest map {legacyMapId}.");
        }

        var result = (
            hasAssignedInstance
                ? WorldInstances.GetOrCreateAssignedOpenWorldAsync(
                    RealmId.Tempest,
                    mapId,
                    instanceId,
                    _worldInstanceOptions
                        .DefaultOpenWorldPlayerCapacity,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None)
                : WorldInstances.GetOrCreateTempestOpenWorldAsync(
                    legacyMapId,
                    _worldInstanceOptions
                        .DefaultOpenWorldPlayerCapacity,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (!result.Succeeded || result.Runtime is null)
        {
            throw new InvalidOperationException(
                $"Cannot resolve Tempest open-world map {legacyMapId}: " +
                $"{result.Status}/{result.PlacementStatus}.");
        }

        return result.Runtime;
    }

    private WorldInstanceRuntime GetOrCreateGatewayWorldInstance(
        GatewayWorldAdmission admission)
    {
        if (!AcceptsGatewayAdmission(admission))
        {
            throw new InvalidOperationException(
                "Worker does not own the admitted exact world route.");
        }

        var result = WorldInstances
            .GetOrCreateAssignedOpenWorldAsync(
                admission.RealmId,
                admission.MapId,
                admission.WorldInstanceId,
                _worldInstanceOptions
                    .DefaultOpenWorldPlayerCapacity,
                DateTimeOffset.UtcNow,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var runtime = result.Runtime;
        if (!result.Succeeded ||
            runtime is null ||
            runtime.RealmId != admission.RealmId ||
            runtime.ContentMapId != admission.MapId ||
            runtime.InstanceId != admission.WorldInstanceId)
        {
            throw new InvalidOperationException(
                "Cannot resolve the admitted exact world route: " +
                $"{result.Status}/{result.PlacementStatus}.");
        }

        return runtime;
    }

    private bool TryGetDefaultWorldInstance(
        byte legacyMapId,
        out WorldInstanceRuntime runtime) =>
        WorldInstances.TryFindTempestOpenWorld(
            legacyMapId,
            out runtime!);

    private WorldInstanceRuntime GetRequiredWorldInstance(
        WorldInstanceId instanceId)
    {
        if (WorldInstances.TryFind(instanceId, out var runtime))
        {
            return runtime;
        }

        throw new InvalidOperationException(
            $"World instance {instanceId} is not owned by this process.");
    }

    private bool TryResolveWorldInstance(
        byte legacyMapId,
        ClientSession? routingSession,
        out WorldInstanceRuntime runtime)
    {
        if (routingSession is not null &&
            _sessions.TryGetValue(routingSession, out var context) &&
            context.MapId == legacyMapId &&
            WorldInstances.TryFind(
                context.WorldInstanceId,
                out runtime!) &&
            runtime.MapId == legacyMapId)
        {
            return true;
        }

        return TryGetDefaultWorldInstance(
            legacyMapId,
            out runtime!);
    }

    private bool TryResolveWorldInstance(
        byte legacyMapId,
        int characterId,
        out WorldInstanceRuntime runtime)
    {
        var instanceIds = _sessions.Values
            .Where(context =>
                context.CharacterId == characterId &&
                context.MapId == legacyMapId)
            .Select(static context =>
                context.WorldInstanceId)
            .Distinct()
            .Take(2)
            .ToArray();
        if (instanceIds.Length == 1 &&
            WorldInstances.TryFind(
                instanceIds[0],
                out runtime!) &&
            runtime.MapId == legacyMapId)
        {
            return true;
        }
        if (instanceIds.Length > 1)
        {
            runtime = default!;
            return false;
        }

        return TryGetDefaultWorldInstance(
            legacyMapId,
            out runtime!);
    }

    private WorldInstanceRuntime GetRequiredWorldInstance(
        GameSessionContext context)
    {
        var runtime = GetRequiredWorldInstance(
            context.WorldInstanceId);
        if (runtime.RealmId != context.RealmId ||
            runtime.MapId != context.MapId)
        {
            throw new InvalidOperationException(
                "The world-session route does not match its runtime.");
        }

        return runtime;
    }

    private bool TryGetWorldInstance(
        GameSessionContext context,
        out WorldInstanceRuntime runtime)
    {
        if (WorldInstances.TryFind(
                context.WorldInstanceId,
                out runtime!) &&
            runtime.RealmId == context.RealmId &&
            runtime.MapId == context.MapId)
        {
            return true;
        }

        runtime = default!;
        return false;
    }

    private TResult InvokeWorldOwner<TResult>(
        WorldInstanceRuntime runtime,
        Func<MapInstance, TResult> command,
        CancellationToken cancellationToken = default) =>
        runtime.Owner.Invoke(
            command,
            _worldInstanceOptions.OwnerInvocationTimeout,
            cancellationToken);

    private void InvokeWorldOwner(
        WorldInstanceRuntime runtime,
        Action<MapInstance> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        InvokeWorldOwner(
            runtime,
            map =>
            {
                command(map);
                return SingleOwnerMailboxUnit.Value;
            },
            cancellationToken);
    }

    private static WorldInstanceRuntimeOptions
        SnapshotWorldInstanceOptions(
            WorldInstanceRuntimeOptions? options)
    {
        options ??= new WorldInstanceRuntimeOptions();
        options.Validate();
        return new WorldInstanceRuntimeOptions
        {
            ServerNodeId = options.ServerNodeId,
            MaximumRuntimes = options.MaximumRuntimes,
            MaximumPlayerAssignments =
                options.MaximumPlayerAssignments,
            MaximumRetiredInstanceIds =
                options.MaximumRetiredInstanceIds,
            DefaultOpenWorldPlayerCapacity =
                options.DefaultOpenWorldPlayerCapacity,
            MailboxCapacity = options.MailboxCapacity,
            OwnerInvocationTimeoutMilliseconds =
                options.OwnerInvocationTimeoutMilliseconds,
            ShutdownDrainTimeoutMilliseconds =
                options.ShutdownDrainTimeoutMilliseconds,
            MaximumFanoutConcurrency =
                options.MaximumFanoutConcurrency,
            StaticOpenWorldInstances =
                options.StaticOpenWorldInstances
                    .Select(static route =>
                        new StaticOpenWorldInstanceOptions
                        {
                            RealmId = route.RealmId,
                            MapId = route.MapId,
                            WorldInstanceId =
                                route.WorldInstanceId
                        })
                    .ToArray(),
            RequireStaticOpenWorldOwnership =
                options.RequireStaticOpenWorldOwnership
        };
    }
}
