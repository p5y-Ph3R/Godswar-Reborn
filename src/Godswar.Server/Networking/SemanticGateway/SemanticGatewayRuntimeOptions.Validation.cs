using System.Net;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

internal sealed partial class SemanticGatewayRuntimeOptions
{
    private SemanticGatewayRuntimeConfiguration Validate(
        string configurationPath,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var login = ValidateLoginEndpoint(Login);
        var game = ValidateGameEndpoint(Game);
        if (login.Port == game.Bind.Port)
        {
            throw new InvalidDataException(
                "Semantic-gateway login and game bind ports must differ.");
        }

        var limitOptions = Limits ??
            throw new InvalidDataException(
                "SemanticGateway.Limits is required.");
        var clientLimits = ValidateClientLimits(limitOptions);
        var backhaulLimits = ValidateBackhaulLimits(limitOptions);
        var authorityLimits = ValidateAuthorityLimits(
            Authority ??
            throw new InvalidDataException(
                "SemanticGateway.Authority is required."));

        var workerOptions = Workers ??
            throw new InvalidDataException(
                "SemanticGateway.Workers is required.");
        var routeOptions = Routes ??
            throw new InvalidDataException(
                "SemanticGateway.Routes is required.");
        if (workerOptions.Length is < 1 or > MaximumWorkers)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Workers must contain between 1 and " +
                $"{MaximumWorkers} entries.");
        }
        if (routeOptions.Length is < 1 or > MaximumRoutes)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Routes must contain between 1 and " +
                $"{MaximumRoutes} entries.");
        }

        var workers = ValidateWorkers(workerOptions);
        var routes = ValidateRoutes(routeOptions, workers);
        StaticSemanticGatewayRouteDirectory directory;
        try
        {
            directory = new StaticSemanticGatewayRouteDirectory(
                workers.Definitions,
                routes.Definitions,
                MaximumWorkers,
                MaximumRoutes,
                authorityLimits.MaximumAdmissions);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Semantic-gateway workers or exact routes are invalid.",
                exception);
        }

        var certificate = LoadGatewayCertificate(
            configurationPath,
            GatewayCertificate ??
            throw new InvalidDataException(
                "SemanticGateway.GatewayCertificate is required."),
            timeProvider);
        try
        {
            return new SemanticGatewayRuntimeConfiguration(
                login,
                game.Bind,
                game.PublicHost,
                game.PublicPort,
                certificate,
                clientLimits,
                backhaulLimits,
                limitOptions.MaximumConcurrentBackhaulTlsHandshakes,
                authorityLimits,
                directory,
                routes.BootstrapTarget,
                routes.MapTargets,
                workers.Targets);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static ValidatedWorkers ValidateWorkers(
        IReadOnlyList<SemanticGatewayWorkerOptions> options)
    {
        var definitions =
            new List<SemanticGatewayWorkerDefinition>(options.Count);
        var targets = new Dictionary<
            ServerNodeId,
            SemanticGatewayWorkerTarget>();
        var capacities = new Dictionary<ServerNodeId, int>();
        var endpoints = new HashSet<IPEndPoint>();

        for (var index = 0; index < options.Count; index++)
        {
            var configured = options[index] ??
                throw new InvalidDataException(
                    $"SemanticGateway.Workers[{index}] is null.");
            var nodeId = ParseNodeId(
                configured.ServerNodeId,
                $"SemanticGateway.Workers[{index}].ServerNodeId");
            if (!configured.InitialState.HasValue ||
                !Enum.IsDefined(configured.InitialState.Value))
            {
                throw new InvalidDataException(
                    $"SemanticGateway.Workers[{index}].InitialState must " +
                    "be available, draining, or unavailable.");
            }

            SemanticGatewayWorkerDefinition definition;
            try
            {
                definition = new SemanticGatewayWorkerDefinition(
                    nodeId,
                    configured.AdmissionCapacity,
                    configured.InitialState.Value);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"SemanticGateway.Workers[{index}] has invalid " +
                    "identity, state, or capacity.",
                    exception);
            }

            var endpoint = ParsePrivateEndpoint(
                configured.BackhaulHost,
                configured.BackhaulPort,
                $"SemanticGateway.Workers[{index}]");
            if (!endpoints.Add(endpoint))
            {
                throw new InvalidDataException(
                    "Semantic-gateway workers must not share a backhaul " +
                    "IP endpoint.");
            }

            var tlsHost = ValidateHost(
                configured.TlsHost,
                $"SemanticGateway.Workers[{index}].TlsHost");
            var pins = BuildWorkerPins(
                configured.AllowedWorkerCertificateSha256,
                index);
            var target = new SemanticGatewayWorkerTarget(
                nodeId,
                endpoint,
                tlsHost,
                pins);
            if (!targets.TryAdd(nodeId, target))
            {
                throw new InvalidDataException(
                    "Semantic-gateway worker node IDs must be unique.");
            }

            capacities.Add(nodeId, configured.AdmissionCapacity);
            definitions.Add(definition);
        }

        return new ValidatedWorkers(
            definitions,
            targets,
            capacities);
    }

    private static ValidatedRoutes ValidateRoutes(
        IReadOnlyList<SemanticGatewayRouteOptions> options,
        ValidatedWorkers workers)
    {
        var definitions =
            new List<SemanticGatewayStaticRoute>(options.Count);
        var mapTargets = new Dictionary<
            MapId,
            SemanticGatewayRouteTarget>();
        var worldInstances = new HashSet<WorldInstanceId>();
        SemanticGatewayRouteTarget bootstrapTarget = default;
        var bootstrapCount = 0;

        for (var index = 0; index < options.Count; index++)
        {
            var configured = options[index] ??
                throw new InvalidDataException(
                    $"SemanticGateway.Routes[{index}] is null.");
            var realmId = ParseRealmId(configured.RealmId, index);
            var mapId = ParseMapId(configured.MapId, index);
            var worldInstanceId = ParseWorldInstanceId(
                configured.WorldInstanceId,
                index);
            var nodeId = ParseNodeId(
                configured.ServerNodeId,
                $"SemanticGateway.Routes[{index}].ServerNodeId");
            if (!workers.Capacities.TryGetValue(
                    nodeId,
                    out var workerCapacity))
            {
                throw new InvalidDataException(
                    $"SemanticGateway.Routes[{index}] references an unknown " +
                    "worker node.");
            }
            if (configured.AdmissionCapacity is < 1 ||
                configured.AdmissionCapacity > workerCapacity)
            {
                throw new InvalidDataException(
                    $"SemanticGateway.Routes[{index}].AdmissionCapacity " +
                    "must fit its worker capacity.");
            }

            var target = new SemanticGatewayRouteTarget(
                realmId,
                mapId,
                worldInstanceId);
            if (!worldInstances.Add(worldInstanceId))
            {
                throw new InvalidDataException(
                    "Semantic-gateway WorldInstanceId routes must be unique.");
            }
            if (!mapTargets.TryAdd(mapId, target))
            {
                throw new InvalidDataException(
                    "Static semantic-gateway routes must use unique MapId " +
                    "values so legacy map lookup remains exact.");
            }

            definitions.Add(
                new SemanticGatewayStaticRoute(
                    realmId,
                    mapId,
                    worldInstanceId,
                    nodeId,
                    configured.AdmissionCapacity));
            if (configured.Bootstrap)
            {
                bootstrapCount++;
                bootstrapTarget = target;
            }
        }

        if (bootstrapCount != 1)
        {
            throw new InvalidDataException(
                "SemanticGateway.Routes must contain exactly one bootstrap " +
                "route.");
        }

        return new ValidatedRoutes(
            definitions,
            mapTargets,
            bootstrapTarget);
    }

    private static ServerNodeId ParseNodeId(
        string? configured,
        string property)
    {
        try
        {
            return new ServerNodeId(configured!);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{property} is invalid.",
                exception);
        }
    }

    private static RealmId ParseRealmId(int value, int index)
    {
        try
        {
            return new RealmId(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Routes[{index}].RealmId is invalid.",
                exception);
        }
    }

    private static MapId ParseMapId(int value, int index)
    {
        if (value is < 0 or > short.MaxValue)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Routes[{index}].MapId must be between 0 " +
                $"and {short.MaxValue}.");
        }

        return new MapId(checked((short)value));
    }

    private static WorldInstanceId ParseWorldInstanceId(
        Guid value,
        int index)
    {
        try
        {
            return new WorldInstanceId(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"SemanticGateway.Routes[{index}].WorldInstanceId is " +
                "invalid.",
                exception);
        }
    }

    private sealed record ValidatedWorkers(
        IReadOnlyList<SemanticGatewayWorkerDefinition> Definitions,
        IReadOnlyDictionary<
            ServerNodeId,
            SemanticGatewayWorkerTarget> Targets,
        IReadOnlyDictionary<ServerNodeId, int> Capacities);

    private sealed record ValidatedRoutes(
        IReadOnlyList<SemanticGatewayStaticRoute> Definitions,
        IReadOnlyDictionary<
            MapId,
            SemanticGatewayRouteTarget> MapTargets,
        SemanticGatewayRouteTarget BootstrapTarget);
}
