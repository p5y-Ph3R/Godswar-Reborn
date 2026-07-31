using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Bounded in-memory route directory for the pre-Redis B18C2 topology.
/// Routes are immutable after construction. Worker availability and
/// admission counts are mutable under one lock.
/// </summary>
internal sealed class StaticSemanticGatewayRouteDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<GatewayAdmissionId, RouteEntry> _reservations =
        [];
    private readonly Dictionary<WorldInstanceId, RouteEntry> _routes = [];
    private readonly Dictionary<ServerNodeId, WorkerEntry> _workers = [];

    public StaticSemanticGatewayRouteDirectory(
        IEnumerable<SemanticGatewayWorkerDefinition> workers,
        IEnumerable<SemanticGatewayStaticRoute> routes,
        int maximumWorkers = 256,
        int maximumRoutes = 65_536,
        int maximumAdmissions = 100_000)
    {
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(routes);
        if (maximumWorkers is <= 0 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWorkers),
                "Maximum workers must be between 1 and 4,096.");
        }
        if (maximumRoutes is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRoutes),
                "Maximum routes must be between 1 and 100,000.");
        }
        if (maximumAdmissions is <= 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAdmissions),
                "Maximum admissions must be between 1 and 1,000,000.");
        }

        MaximumWorkers = maximumWorkers;
        MaximumRoutes = maximumRoutes;
        MaximumAdmissions = maximumAdmissions;
        AddWorkers(workers);
        AddRoutes(routes);
        if (_workers.Count == 0)
        {
            throw new ArgumentException(
                "At least one semantic-gateway worker is required.",
                nameof(workers));
        }
        if (_routes.Count == 0)
        {
            throw new ArgumentException(
                "At least one semantic-gateway route is required.",
                nameof(routes));
        }
    }

    public int MaximumWorkers { get; }

    public int MaximumRoutes { get; }

    public int MaximumAdmissions { get; }

    public SemanticGatewayRouteSelectionResult TryResolveExact(
        SemanticGatewayRouteTarget target)
    {
        if (!target.IsValid)
        {
            throw new ArgumentException(
                "A valid exact route target is required.",
                nameof(target));
        }

        lock (_gate)
        {
            return ResolveLocked(target, requireCapacity: false);
        }
    }

    public SemanticGatewayRouteSelectionResult TryReserve(
        GatewayAdmissionId admissionId,
        SemanticGatewayRouteTarget target)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentException(
                "A valid admission ID is required.",
                nameof(admissionId));
        }
        if (!target.IsValid)
        {
            throw new ArgumentException(
                "A valid exact route target is required.",
                nameof(target));
        }

        lock (_gate)
        {
            if (_reservations.ContainsKey(admissionId))
            {
                return new(
                    SemanticGatewayRouteSelectionStatus.DuplicateAdmission,
                    null);
            }
            if (_reservations.Count >= MaximumAdmissions)
            {
                return new(
                    SemanticGatewayRouteSelectionStatus
                        .DirectoryCapacityExceeded,
                    null);
            }

            var resolved = ResolveLocked(target, requireCapacity: true);
            if (!resolved.IsSelected)
            {
                return resolved;
            }

            var route = _routes[target.WorldInstanceId];
            var worker = _workers[route.Definition.NodeId];
            _reservations.Add(admissionId, route);
            route.ActiveAdmissions++;
            worker.ActiveAdmissions++;
            return resolved;
        }
    }

    /// <summary>
    /// Revalidates the complete route and worker revision before a reserved
    /// admission becomes committed. A drain or availability transition makes
    /// an older reservation stale.
    /// </summary>
    public SemanticGatewayRouteSelectionStatus ValidateReservation(
        GatewayAdmissionId admissionId,
        SemanticGatewayRouteSelection expected)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentException(
                "A valid admission ID is required.",
                nameof(admissionId));
        }
        ArgumentNullException.ThrowIfNull(expected);

        lock (_gate)
        {
            if (!_reservations.TryGetValue(admissionId, out var route))
            {
                return SemanticGatewayRouteSelectionStatus.RouteNotFound;
            }
            if (route.Definition.Target != expected.Target ||
                route.Definition.NodeId != expected.NodeId)
            {
                return SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch;
            }
            if (!_workers.TryGetValue(expected.NodeId, out var worker))
            {
                return SemanticGatewayRouteSelectionStatus.WorkerNotFound;
            }
            var stateStatus = worker.State switch
            {
                SemanticGatewayWorkerState.Available =>
                    SemanticGatewayRouteSelectionStatus.Selected,
                SemanticGatewayWorkerState.Draining =>
                    SemanticGatewayRouteSelectionStatus.WorkerDraining,
                SemanticGatewayWorkerState.Unavailable =>
                    SemanticGatewayRouteSelectionStatus.WorkerUnavailable,
                _ => throw new InvalidOperationException(
                    "Worker has an invalid lifecycle state.")
            };
            if (stateStatus !=
                SemanticGatewayRouteSelectionStatus.Selected)
            {
                return stateStatus;
            }

            return worker.Revision == expected.WorkerRevision
                ? SemanticGatewayRouteSelectionStatus.Selected
                : SemanticGatewayRouteSelectionStatus.RouteIdentityMismatch;
        }
    }

    /// <summary>
    /// Revalidates an already committed route. Draining rejects new
    /// reservations but preserves established sessions; unavailable workers
    /// fail closed. Revision changes only fence reserve-to-commit races.
    /// </summary>
    public SemanticGatewayRouteSelectionStatus ValidateActiveReservation(
        GatewayAdmissionId admissionId,
        SemanticGatewayRouteSelection expected)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentException(
                "A valid admission ID is required.",
                nameof(admissionId));
        }
        ArgumentNullException.ThrowIfNull(expected);

        lock (_gate)
        {
            if (!_reservations.TryGetValue(admissionId, out var route))
            {
                return SemanticGatewayRouteSelectionStatus.RouteNotFound;
            }
            if (route.Definition.Target != expected.Target ||
                route.Definition.NodeId != expected.NodeId)
            {
                return SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch;
            }
            if (!_workers.TryGetValue(expected.NodeId, out var worker))
            {
                return SemanticGatewayRouteSelectionStatus.WorkerNotFound;
            }

            return worker.State switch
            {
                SemanticGatewayWorkerState.Available or
                    SemanticGatewayWorkerState.Draining =>
                    SemanticGatewayRouteSelectionStatus.Selected,
                SemanticGatewayWorkerState.Unavailable =>
                    SemanticGatewayRouteSelectionStatus.WorkerUnavailable,
                _ => throw new InvalidOperationException(
                    "Worker has an invalid lifecycle state.")
            };
        }
    }

    public bool Release(GatewayAdmissionId admissionId)
    {
        if (!admissionId.IsValid)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_reservations.Remove(admissionId, out var route))
            {
                return false;
            }

            var worker = _workers[route.Definition.NodeId];
            if (route.ActiveAdmissions <= 0 ||
                worker.ActiveAdmissions <= 0)
            {
                throw new InvalidOperationException(
                    "Semantic-gateway route accounting underflow.");
            }

            route.ActiveAdmissions--;
            worker.ActiveAdmissions--;
            return true;
        }
    }

    public SemanticGatewayWorkerUpdateResult UpdateWorkerState(
        ServerNodeId nodeId,
        long expectedRevision,
        SemanticGatewayWorkerState target)
    {
        if (!nodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid server-node ID is required.",
                nameof(nodeId));
        }
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        lock (_gate)
        {
            if (!_workers.TryGetValue(nodeId, out var worker))
            {
                return new(
                    SemanticGatewayWorkerUpdateStatus.WorkerNotFound,
                    null);
            }
            if (worker.Revision != expectedRevision)
            {
                return new(
                    SemanticGatewayWorkerUpdateStatus.RevisionConflict,
                    Snapshot(worker));
            }
            if (worker.State == target)
            {
                return new(
                    SemanticGatewayWorkerUpdateStatus.NoChange,
                    Snapshot(worker));
            }

            worker.State = target;
            worker.Revision = checked(worker.Revision + 1);
            return new(
                SemanticGatewayWorkerUpdateStatus.Updated,
                Snapshot(worker));
        }
    }

    public SemanticGatewayRouteDirectorySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var workers = _workers.Values
                .OrderBy(
                    static worker => worker.Definition.NodeId.ToString(),
                    StringComparer.Ordinal)
                .Select(Snapshot)
                .ToArray();
            return new(
                workers.Length,
                _routes.Count,
                _reservations.Count,
                workers.Count(static worker =>
                    worker.State ==
                    SemanticGatewayWorkerState.Available),
                workers.Count(static worker =>
                    worker.State ==
                    SemanticGatewayWorkerState.Draining),
                workers.Count(static worker =>
                    worker.State ==
                    SemanticGatewayWorkerState.Unavailable),
                workers);
        }
    }

    private SemanticGatewayRouteSelectionResult ResolveLocked(
        SemanticGatewayRouteTarget target,
        bool requireCapacity)
    {
        if (!_routes.TryGetValue(target.WorldInstanceId, out var route))
        {
            return new(
                SemanticGatewayRouteSelectionStatus.RouteNotFound,
                null);
        }
        if (route.Definition.Target != target)
        {
            return new(
                SemanticGatewayRouteSelectionStatus.RouteIdentityMismatch,
                null);
        }
        if (!_workers.TryGetValue(route.Definition.NodeId, out var worker))
        {
            return new(
                SemanticGatewayRouteSelectionStatus.WorkerNotFound,
                null);
        }
        if (worker.State == SemanticGatewayWorkerState.Draining)
        {
            return new(
                SemanticGatewayRouteSelectionStatus.WorkerDraining,
                null);
        }
        if (worker.State == SemanticGatewayWorkerState.Unavailable)
        {
            return new(
                SemanticGatewayRouteSelectionStatus.WorkerUnavailable,
                null);
        }
        if (requireCapacity &&
            worker.ActiveAdmissions >=
                worker.Definition.AdmissionCapacity)
        {
            return new(
                SemanticGatewayRouteSelectionStatus.WorkerCapacityExceeded,
                null);
        }
        if (requireCapacity &&
            route.ActiveAdmissions >=
                route.Definition.AdmissionCapacity)
        {
            return new(
                SemanticGatewayRouteSelectionStatus.RouteCapacityExceeded,
                null);
        }

        return new(
            SemanticGatewayRouteSelectionStatus.Selected,
            new(
                route.Definition.Target,
                worker.Definition.NodeId,
                worker.Revision));
    }

    private void AddWorkers(
        IEnumerable<SemanticGatewayWorkerDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (_workers.Count >= MaximumWorkers)
            {
                throw new ArgumentException(
                    $"Worker definitions exceed the configured maximum of " +
                    $"{MaximumWorkers}.",
                    nameof(definitions));
            }
            if (!_workers.TryAdd(
                    definition.NodeId,
                    new WorkerEntry(definition)))
            {
                throw new ArgumentException(
                    $"Duplicate server-node ID '{definition.NodeId}'.",
                    nameof(definitions));
            }
        }
    }

    private void AddRoutes(
        IEnumerable<SemanticGatewayStaticRoute> definitions)
    {
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (_routes.Count >= MaximumRoutes)
            {
                throw new ArgumentException(
                    $"Route definitions exceed the configured maximum of " +
                    $"{MaximumRoutes}.",
                    nameof(definitions));
            }
            if (!_workers.TryGetValue(
                    definition.NodeId,
                    out var worker))
            {
                throw new ArgumentException(
                    $"Route '{definition.Target.WorldInstanceId}' refers to " +
                    $"unknown worker '{definition.NodeId}'.",
                    nameof(definitions));
            }
            if (!_routes.TryAdd(
                    definition.Target.WorldInstanceId,
                    new RouteEntry(definition)))
            {
                throw new ArgumentException(
                    $"Duplicate world-instance route " +
                    $"'{definition.Target.WorldInstanceId}'.",
                    nameof(definitions));
            }

            worker.RouteCount++;
        }
    }

    private static SemanticGatewayWorkerSnapshot Snapshot(
        WorkerEntry worker) =>
        new(
            worker.Definition.NodeId,
            worker.State,
            worker.Revision,
            worker.ActiveAdmissions,
            worker.Definition.AdmissionCapacity,
            worker.RouteCount);

    private sealed class WorkerEntry
    {
        public WorkerEntry(SemanticGatewayWorkerDefinition definition)
        {
            Definition = definition;
            State = definition.InitialState;
        }

        public SemanticGatewayWorkerDefinition Definition { get; }

        public SemanticGatewayWorkerState State { get; set; }

        public long Revision { get; set; } = 1;

        public int ActiveAdmissions { get; set; }

        public int RouteCount { get; set; }
    }

    private sealed class RouteEntry
    {
        public RouteEntry(SemanticGatewayStaticRoute definition)
        {
            Definition = definition;
        }

        public SemanticGatewayStaticRoute Definition { get; }

        public int ActiveAdmissions { get; set; }
    }
}
