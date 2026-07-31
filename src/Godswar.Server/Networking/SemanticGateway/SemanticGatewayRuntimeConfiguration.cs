using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Immutable client-listener limits used by the semantic gateway host.
/// Mutable <see cref="NetworkRuntimeOptions"/> instances are created on
/// demand so callers cannot change the validated configuration.
/// </summary>
internal sealed record SemanticGatewayClientRuntimeLimits
{
    public SemanticGatewayClientRuntimeLimits(
        int listenBacklog,
        int maximumConnections,
        int maximumUnauthenticatedConnections,
        int maximumUnauthenticatedConnectionsPerIp,
        int maximumUnauthenticatedConnectionsPerPrefix,
        int bufferSizeBytes,
        TimeSpan firstPacketTimeout,
        TimeSpan idleTimeout,
        TimeSpan gracefulDrainTimeout)
    {
        ListenBacklog = listenBacklog;
        MaximumConnections = maximumConnections;
        MaximumUnauthenticatedConnections =
            maximumUnauthenticatedConnections;
        MaximumUnauthenticatedConnectionsPerIp =
            maximumUnauthenticatedConnectionsPerIp;
        MaximumUnauthenticatedConnectionsPerPrefix =
            maximumUnauthenticatedConnectionsPerPrefix;
        BufferSizeBytes = bufferSizeBytes;
        FirstPacketTimeout = firstPacketTimeout;
        IdleTimeout = idleTimeout;
        GracefulDrainTimeout = gracefulDrainTimeout;

        _ = AdmissionOptions;
        _ = CreateNetworkOptions();
        if (BufferSizeBytes is < 1_024 or > 64 * 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferSizeBytes),
                "Semantic-gateway buffers must be between 1 KiB and 64 KiB.");
        }
    }

    public int ListenBacklog { get; }

    public int MaximumConnections { get; }

    public int MaximumUnauthenticatedConnections { get; }

    public int MaximumUnauthenticatedConnectionsPerIp { get; }

    public int MaximumUnauthenticatedConnectionsPerPrefix { get; }

    public int BufferSizeBytes { get; }

    public TimeSpan FirstPacketTimeout { get; }

    public TimeSpan IdleTimeout { get; }

    public TimeSpan GracefulDrainTimeout { get; }

    public ConnectionAdmissionOptions AdmissionOptions
    {
        get
        {
            var options = new ConnectionAdmissionOptions(
                MaximumConnections,
                MaximumUnauthenticatedConnections,
                MaximumUnauthenticatedConnectionsPerIp,
                MaximumUnauthenticatedConnectionsPerPrefix);
            options.Validate();
            return options;
        }
    }

    public NetworkRuntimeOptions CreateNetworkOptions()
    {
        var options = new NetworkRuntimeOptions
        {
            ListenBacklog = ListenBacklog,
            MaxActiveConnections = MaximumConnections,
            MaxUnauthenticatedConnections =
                MaximumUnauthenticatedConnections,
            MaxUnauthenticatedConnectionsPerIp =
                MaximumUnauthenticatedConnectionsPerIp,
            MaxUnauthenticatedConnectionsPerPrefix =
                MaximumUnauthenticatedConnectionsPerPrefix,
            MaxConcurrentTlsHandshakes = Math.Min(
                64,
                MaximumUnauthenticatedConnections),
            FirstPacketTimeoutMilliseconds =
                ToWholeMilliseconds(
                    FirstPacketTimeout,
                    nameof(FirstPacketTimeout)),
            IdleTimeoutMilliseconds =
                ToWholeMilliseconds(
                    IdleTimeout,
                    nameof(IdleTimeout)),
            GracefulDrainTimeoutMilliseconds =
                ToWholeMilliseconds(
                    GracefulDrainTimeout,
                    nameof(GracefulDrainTimeout))
        };
        options.Validate();
        return options;
    }

    private static int ToWholeMilliseconds(
        TimeSpan value,
        string parameter)
    {
        var milliseconds = value.TotalMilliseconds;
        if (milliseconds != Math.Truncate(milliseconds) ||
            milliseconds is < 1 or > 10 * 60 * 1_000)
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                "Semantic-gateway deadlines must be whole milliseconds " +
                "between 1 and 600,000.");
        }

        return checked((int)milliseconds);
    }
}

/// <summary>
/// Fully validated, resolved configuration for a future B18C2 host.
/// The gateway client certificate is owned by this object.
/// </summary>
internal sealed class SemanticGatewayRuntimeConfiguration : IDisposable
{
    private readonly IReadOnlyDictionary<
        MapId,
        SemanticGatewayRouteTarget> _mapRoutes;
    private readonly IReadOnlyDictionary<
        ServerNodeId,
        SemanticGatewayWorkerTarget> _workerTargets;
    private X509Certificate2? _gatewayCertificate;

    public SemanticGatewayRuntimeConfiguration(
        IPEndPoint loginBind,
        IPEndPoint gameBind,
        string gamePublicHost,
        int gamePublicPort,
        X509Certificate2 gatewayCertificate,
        SemanticGatewayClientRuntimeLimits clientLimits,
        BackhaulRuntimeLimits backhaulLimits,
        int maximumConcurrentBackhaulTlsHandshakes,
        SemanticGatewayAuthorityLimits authorityLimits,
        StaticSemanticGatewayRouteDirectory routeDirectory,
        SemanticGatewayRouteTarget bootstrapTarget,
        IReadOnlyDictionary<MapId, SemanticGatewayRouteTarget> mapRoutes,
        IReadOnlyDictionary<
            ServerNodeId,
            SemanticGatewayWorkerTarget> workerTargets)
    {
        LoginBind = CloneEndpoint(loginBind, nameof(loginBind));
        GameBind = CloneEndpoint(gameBind, nameof(gameBind));
        if (string.IsNullOrWhiteSpace(gamePublicHost) ||
            gamePublicHost.Length > 253)
        {
            throw new ArgumentException(
                "A bounded game public host is required.",
                nameof(gamePublicHost));
        }
        if (gamePublicPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePublicPort));
        }
        ArgumentNullException.ThrowIfNull(gatewayCertificate);
        ArgumentNullException.ThrowIfNull(clientLimits);
        backhaulLimits.Validate();
        if (maximumConcurrentBackhaulTlsHandshakes is < 1 or >
            BackhaulHandshakeGate.MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentBackhaulTlsHandshakes));
        }
        ArgumentNullException.ThrowIfNull(authorityLimits);
        ArgumentNullException.ThrowIfNull(routeDirectory);
        if (!bootstrapTarget.IsValid)
        {
            throw new ArgumentException(
                "A valid bootstrap target is required.",
                nameof(bootstrapTarget));
        }
        ArgumentNullException.ThrowIfNull(mapRoutes);
        ArgumentNullException.ThrowIfNull(workerTargets);
        if (mapRoutes.Count == 0 || workerTargets.Count == 0)
        {
            throw new ArgumentException(
                "Semantic-gateway maps and workers cannot be empty.");
        }

        GamePublicHost = gamePublicHost;
        GamePublicPort = gamePublicPort;
        _gatewayCertificate = gatewayCertificate;
        ClientLimits = clientLimits;
        BackhaulLimits = backhaulLimits;
        MaximumConcurrentBackhaulTlsHandshakes =
            maximumConcurrentBackhaulTlsHandshakes;
        AuthorityLimits = authorityLimits;
        RouteDirectory = routeDirectory;
        BootstrapTarget = bootstrapTarget;
        _mapRoutes = new Dictionary<
            MapId,
            SemanticGatewayRouteTarget>(mapRoutes);
        _workerTargets = new Dictionary<
            ServerNodeId,
            SemanticGatewayWorkerTarget>(workerTargets);
    }

    public IPEndPoint LoginBind { get; }

    public IPEndPoint GameBind { get; }

    public string GamePublicHost { get; }

    public int GamePublicPort { get; }

    public X509Certificate2 GatewayCertificate =>
        _gatewayCertificate ??
        throw new ObjectDisposedException(
            nameof(SemanticGatewayRuntimeConfiguration));

    public SemanticGatewayClientRuntimeLimits ClientLimits { get; }

    public BackhaulRuntimeLimits BackhaulLimits { get; }

    public int MaximumConcurrentBackhaulTlsHandshakes { get; }

    public SemanticGatewayAuthorityLimits AuthorityLimits { get; }

    public StaticSemanticGatewayRouteDirectory RouteDirectory { get; }

    public SemanticGatewayRouteTarget BootstrapTarget { get; }

    public BackhaulHandshakeGate CreateBackhaulHandshakeGate() =>
        new(MaximumConcurrentBackhaulTlsHandshakes);

    public SemanticGatewayRouteTarget? ResolveMap(MapId mapId) =>
        TryResolveMap(mapId, out var target) ? target : null;

    public bool TryResolveMap(
        MapId mapId,
        out SemanticGatewayRouteTarget target)
    {
        if (!mapId.IsValid)
        {
            target = default;
            return false;
        }

        return _mapRoutes.TryGetValue(mapId, out target);
    }

    public SemanticGatewayWorkerTarget? ResolveWorker(
        ServerNodeId nodeId) =>
        TryGetWorker(nodeId, out var target) ? target : null;

    public bool TryGetWorker(
        ServerNodeId nodeId,
        [NotNullWhen(true)] out SemanticGatewayWorkerTarget? target)
    {
        if (!nodeId.IsValid)
        {
            target = null;
            return false;
        }

        return _workerTargets.TryGetValue(nodeId, out target);
    }

    public void Dispose() =>
        Interlocked.Exchange(
            ref _gatewayCertificate,
            null)?.Dispose();

    private static IPEndPoint CloneEndpoint(
        IPEndPoint endpoint,
        string parameter)
    {
        ArgumentNullException.ThrowIfNull(endpoint, parameter);
        if (endpoint.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameter);
        }

        return new IPEndPoint(endpoint.Address, endpoint.Port);
    }
}
