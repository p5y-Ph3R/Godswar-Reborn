namespace Godswar.Server.Application.Gateway;

internal readonly record struct SemanticGatewayAdmissionCapacities(
    int Worker,
    int Route);

internal sealed partial class StaticSemanticGatewayRouteDirectory
{
    /// <summary>
    /// Returns only capacities from the trusted static configuration. The
    /// result contains no endpoint or certificate material and Redis cannot
    /// use it to introduce a route that was not configured locally.
    /// </summary>
    public SemanticGatewayAdmissionCapacities?
        TryGetConfiguredAdmissionCapacities(
            SemanticGatewayRouteSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        lock (_gate)
        {
            if (!_routes.TryGetValue(
                    selection.Target.WorldInstanceId,
                    out var route) ||
                route.Definition.Target != selection.Target ||
                route.Definition.NodeId != selection.NodeId ||
                !_workers.TryGetValue(
                    selection.NodeId,
                    out var worker))
            {
                return null;
            }

            return new(
                worker.Definition.AdmissionCapacity,
                route.Definition.AdmissionCapacity);
        }
    }

    /// <summary>
    /// Revalidates a Redis-backed reservation against the exact trusted
    /// route and current process-local readiness. Redis worker boot and
    /// revision proofs are revalidated separately by the adapter.
    /// </summary>
    public SemanticGatewayRouteSelectionStatus
        ValidateTrustedReservation(
            SemanticGatewayRouteSelection expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_gate)
        {
            return ValidateTrustedSelectionLocked(
                expected,
                allowDraining: false);
        }
    }

    /// <summary>
    /// Revalidates an established Redis-backed session. Draining preserves
    /// established sessions; unavailable or mismatched static routes fail.
    /// </summary>
    public SemanticGatewayRouteSelectionStatus
        ValidateTrustedActiveAdmission(
            SemanticGatewayRouteSelection expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_gate)
        {
            return ValidateTrustedSelectionLocked(
                expected,
                allowDraining: true);
        }
    }

    private SemanticGatewayRouteSelectionStatus
        ValidateTrustedSelectionLocked(
            SemanticGatewayRouteSelection expected,
            bool allowDraining)
    {
        if (!_routes.TryGetValue(
                expected.Target.WorldInstanceId,
                out var route))
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
        if (worker.State == SemanticGatewayWorkerState.Unavailable)
        {
            return SemanticGatewayRouteSelectionStatus.WorkerUnavailable;
        }
        if (!allowDraining &&
            worker.State == SemanticGatewayWorkerState.Draining)
        {
            return SemanticGatewayRouteSelectionStatus.WorkerDraining;
        }

        return SemanticGatewayRouteSelectionStatus.Selected;
    }
}
