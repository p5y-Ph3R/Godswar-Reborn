using System.Reflection;
using Godswar.Server.Infrastructure.Coordination;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server;

/// <summary>
/// Process composition root for optional Redis coordination. Local mode
/// creates no Redis client and preserves the existing in-process authorities.
/// </summary>
internal sealed class ServerCoordinationComposition :
    IAsyncDisposable
{
    private readonly RedisCoordinationExecutor? _executor;
    private readonly RedisCoordinationKeyBuilder? _keys;
    private IGameTicketStore? _tickets;
    private int _disposed;

    private ServerCoordinationComposition(
        RedisCoordinationExecutor? executor,
        RedisCoordinationKeyBuilder? keys,
        WorkerCoordinationRuntime? worker)
    {
        _executor = executor;
        _keys = keys;
        Worker = worker;
    }

    public WorkerCoordinationRuntime? Worker { get; }

    public static async ValueTask<ServerCoordinationComposition>
        CreateAsync(
            ServerOptions options,
            string contentRevision,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRevision);
        if (options.Coordination.ProviderKind ==
            CoordinationProviderKind.Local)
        {
            return new ServerCoordinationComposition(
                executor: null,
                keys: null,
                worker: null);
        }

        RedisCoordinationExecutor? executor = null;
        try
        {
            var node = options.Game.WorldInstances.ProcessServerNodeId;
            executor = await RedisCoordinationExecutor.ConnectAsync(
                options.Coordination,
                $"worker-{node}",
                cancellationToken);
            var keys = new RedisCoordinationKeyBuilder(
                options.Coordination.Environment);
            var adapter = new RedisWorkerCoordination(
                executor,
                keys,
                options.Coordination.Capacity,
                options.Coordination.MaximumConcurrentOperations);
            var worker = new WorkerCoordinationRuntime(
                adapter,
                options.Coordination,
                options.Game.WorldInstances,
                contentRevision,
                BuildRevision());
            return new ServerCoordinationComposition(
                executor,
                keys,
                worker);
        }
        catch
        {
            if (executor is not null)
            {
                await executor.DisposeAsync();
            }
            throw;
        }
    }

    public static async ValueTask<ISemanticGatewayCoordination?>
        CreateSemanticGatewayCoordinationAsync(
            ServerOptions options,
            SemanticGatewayRuntimeConfiguration configuration,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        if (options.Coordination.ProviderKind ==
            CoordinationProviderKind.Local)
        {
            return null;
        }

        RedisCoordinationExecutor? executor = null;
        try
        {
            executor = await RedisCoordinationExecutor.ConnectAsync(
                options.Coordination,
                "semantic-gateway",
                cancellationToken);
            return new RedisSemanticGatewayCoordination(
                executor,
                new RedisCoordinationKeyBuilder(
                    options.Coordination.Environment),
                configuration.RouteDirectory,
                configuration.AuthorityLimits,
                ownsExecutor: true);
        }
        catch
        {
            if (executor is not null)
            {
                await executor.DisposeAsync();
            }
            throw;
        }
    }

    public IGameTicketStore CreateGameTicketStore(
        SecureGameTicketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_tickets is not null)
        {
            throw new InvalidOperationException(
                "A game-ticket store was already composed.");
        }

        _tickets = _executor is null
            ? new InMemoryGameTicketStore(
                options.Capacity,
                options.Ttl)
            : new RedisGameTicketStore(
                _executor,
                _keys ?? throw new InvalidOperationException(
                    "Redis coordination keys were not composed."),
                options.Capacity,
                options.Ttl);
        return _tickets;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_tickets is not null)
        {
            await _tickets.DisposeAsync();
        }
        if (Worker is not null)
        {
            await Worker.DisposeAsync();
        }
        if (_executor is not null)
        {
            await _executor.DisposeAsync();
        }
    }

    private static string BuildRevision()
    {
        var assembly = typeof(ServerCoordinationComposition).Assembly;
        return assembly
                .GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
    }
}
