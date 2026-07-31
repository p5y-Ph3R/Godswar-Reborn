using System.Net;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SemanticGatewayChecks
{
    public const string CheckName =
        "B18C2 semantic gateway authority and routes";

    private static readonly RealmId Realm = RealmId.Tempest;
    private static readonly MapId Sparta = new(0);
    private static readonly MapId Athens = new(1);
    private static readonly ServerNodeId NodeA = new("worker-a");
    private static readonly ServerNodeId NodeB = new("worker-b");
    private static readonly WorldInstanceId SpartaInstance =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly WorldInstanceId AthensInstance =
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    public static async Task RunAsync()
    {
        CheckTypedIdentityAndSourceNormalization();
        CheckExactStaticRoutesAndCapacity();
        CheckWorkerLifecycleAndStaleReservations();
        CheckAuthorityLifecycle();
        CheckAuthorityExpiryAndBounds();
        CheckConcurrentDuplicateLogin();
        await CheckLegacyGameLoginProbeAsync();
    }

    private static void CheckTypedIdentityAndSourceNormalization()
    {
        var connection = GatewayConnectionId.New();
        var source = new SemanticGatewayConnectionSource(
            connection,
            IPAddress.Parse("::ffff:192.0.2.10"));
        Check.True(
            source.Address!.Equals(IPAddress.Parse("192.0.2.10")),
            "IPv4-mapped gateway source is normalized to IPv4");
        Check.True(
            source.ConnectionId == connection,
            "normalized source retains independent connection identity");
        Check.True(
            GatewayLoginGenerationId.New().IsValid &&
            GatewayAdmissionId.New().IsValid,
            "gateway opaque IDs are nonzero");

        Check.Throws<ArgumentException>(
            () => _ = new GatewayConnectionId(Guid.Empty),
            "empty gateway connection ID is rejected");
        Check.Throws<ArgumentException>(
            () => _ = new SemanticGatewayConnectionSource(
                connection,
                IPAddress.Any),
            "unspecified source address is rejected");
        Check.Throws<ArgumentException>(
            () => _ = new SemanticGatewayPrincipal(1, "bad name"),
            "noncanonical username is rejected");
    }

    private static void CheckExactStaticRoutesAndCapacity()
    {
        var routes = CreateDirectory(
            workerACapacity: 2,
            workerBCapacity: 1,
            spartaCapacity: 1,
            athensCapacity: 1);
        var target = Target(Sparta, SpartaInstance);
        var firstId = GatewayAdmissionId.New();
        var first = routes.TryReserve(firstId, target);
        Check.True(
            first.IsSelected &&
            first.Selection!.NodeId == NodeA,
            "exact Sparta instance selects its configured worker");
        Check.Equal(
            1L,
            first.Selection!.WorkerRevision,
            "initial worker route revision is one");

        var wrongRealm = routes.TryReserve(
            GatewayAdmissionId.New(),
            new SemanticGatewayRouteTarget(
                new RealmId(2),
                Sparta,
                SpartaInstance));
        Check.True(
            wrongRealm.Status ==
                SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch,
            "same world ID with a different realm fails closed");
        var wrongMap = routes.TryReserve(
            GatewayAdmissionId.New(),
            Target(Athens, SpartaInstance));
        Check.True(
            wrongMap.Status ==
                SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch,
            "same world ID with a different map fails closed");
        var absent = routes.TryResolveExact(
            Target(
                Sparta,
                new WorldInstanceId(Guid.NewGuid())));
        Check.True(
            absent.Status ==
                SemanticGatewayRouteSelectionStatus.RouteNotFound,
            "unknown world instance does not fall back by map");
        var full = routes.TryReserve(
            GatewayAdmissionId.New(),
            target);
        Check.True(
            full.Status ==
                SemanticGatewayRouteSelectionStatus
                    .RouteCapacityExceeded,
            "per-route admission capacity is authoritative");
        Check.True(
            routes.TryReserve(firstId, target).Status ==
                SemanticGatewayRouteSelectionStatus.DuplicateAdmission,
            "duplicate admission ID is rejected before capacity");
        Check.True(
            routes.Release(firstId),
            "route reservation releases exactly once");
        Check.True(
            !routes.Release(firstId),
            "duplicate route release cannot underflow accounting");

        var bounded = new StaticSemanticGatewayRouteDirectory(
            [
                new SemanticGatewayWorkerDefinition(NodeA, 2),
                new SemanticGatewayWorkerDefinition(NodeB, 2)
            ],
            [
                new SemanticGatewayStaticRoute(
                    Realm,
                    Sparta,
                    SpartaInstance,
                    NodeA,
                    2),
                new SemanticGatewayStaticRoute(
                    Realm,
                    Athens,
                    AthensInstance,
                    NodeB,
                    2)
            ],
            maximumAdmissions: 1);
        Check.True(
            bounded.TryReserve(
                GatewayAdmissionId.New(),
                Target(Sparta, SpartaInstance)).IsSelected,
            "global directory admits up to its exact bound");
        Check.True(
            bounded.TryReserve(
                GatewayAdmissionId.New(),
                Target(Athens, AthensInstance)).Status ==
                SemanticGatewayRouteSelectionStatus
                    .DirectoryCapacityExceeded,
            "global directory capacity prevents unbounded reservations");
    }

    private static void CheckWorkerLifecycleAndStaleReservations()
    {
        var routes = CreateDirectory();
        var admissionId = GatewayAdmissionId.New();
        var selected = routes.TryReserve(
            admissionId,
            Target(Sparta, SpartaInstance));
        var draining = routes.UpdateWorkerState(
            NodeA,
            expectedRevision: 1,
            SemanticGatewayWorkerState.Draining);
        Check.True(
            draining.Status ==
                SemanticGatewayWorkerUpdateStatus.Updated &&
            draining.Worker!.Revision == 2,
            "worker drain advances its route revision");
        Check.True(
            routes.TryReserve(
                GatewayAdmissionId.New(),
                Target(Sparta, SpartaInstance)).Status ==
                SemanticGatewayRouteSelectionStatus.WorkerDraining,
            "draining worker rejects new admission");
        Check.True(
            routes.ValidateReservation(
                admissionId,
                selected.Selection!) ==
                SemanticGatewayRouteSelectionStatus.WorkerDraining,
            "drain rejects a not-yet-committed reservation");
        Check.True(
            routes.UpdateWorkerState(
                NodeA,
                expectedRevision: 1,
                SemanticGatewayWorkerState.Available).Status ==
                SemanticGatewayWorkerUpdateStatus.RevisionConflict,
            "stale worker lifecycle update is rejected");
        var unavailable = routes.UpdateWorkerState(
            NodeA,
            expectedRevision: 2,
            SemanticGatewayWorkerState.Unavailable);
        Check.True(
            unavailable.Status ==
                SemanticGatewayWorkerUpdateStatus.Updated,
            "worker can transition from drain to unavailable");
        Check.True(
            routes.TryResolveExact(
                Target(Sparta, SpartaInstance)).Status ==
                SemanticGatewayRouteSelectionStatus.WorkerUnavailable,
            "unavailable worker is not selected");
        Check.True(
            routes.GetSnapshot().ActiveReservations == 1,
            "worker lifecycle does not silently discard resident route state");
        Check.True(
            routes.Release(admissionId),
            "resident route remains explicitly releasable");
    }

    private static StaticSemanticGatewayRouteDirectory CreateDirectory(
        int workerACapacity = 4,
        int workerBCapacity = 4,
        int spartaCapacity = 4,
        int athensCapacity = 4) =>
        new(
            [
                new SemanticGatewayWorkerDefinition(
                    NodeA,
                    workerACapacity),
                new SemanticGatewayWorkerDefinition(
                    NodeB,
                    workerBCapacity)
            ],
            [
                new SemanticGatewayStaticRoute(
                    Realm,
                    Sparta,
                    SpartaInstance,
                    NodeA,
                    spartaCapacity),
                new SemanticGatewayStaticRoute(
                    Realm,
                    Athens,
                    AthensInstance,
                    NodeB,
                    athensCapacity)
            ]);

    private static SemanticGatewayRouteTarget Target(
        MapId mapId,
        WorldInstanceId instanceId) =>
        new(Realm, mapId, instanceId);

    private static SemanticGatewayConnectionSource Source(
        string address = "192.0.2.20") =>
        new(
            GatewayConnectionId.New(),
            IPAddress.Parse(address));

    private static SemanticGatewayAdmissionClaim Claim(
        SemanticGatewayAdmissionLease lease) =>
        new(
            lease.AdmissionId,
            lease.GenerationId,
            lease.Principal,
            lease.Source,
            lease.Route.Target,
            lease.Route.NodeId,
            lease.Route.WorkerRevision);
}
