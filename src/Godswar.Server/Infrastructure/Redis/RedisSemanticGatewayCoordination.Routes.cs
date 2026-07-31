using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Application.Gateway;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisSemanticGatewayCoordination
{
    private async ValueTask<RedisSemanticGatewayRouteProof>
        ResolveRouteProofAsync(
            SemanticGatewayRouteTarget target,
            bool established,
            ServerNodeId? expectedNode,
            long? expectedProof,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        SemanticGatewayRouteSelectionResult local;
        if (expectedProof is null)
        {
            local = _routes.TryResolveExact(target);
        }
        else
        {
            var expected = new SemanticGatewayRouteSelection(
                target,
                expectedNode ??
                throw new ArgumentNullException(nameof(expectedNode)),
                expectedProof.Value);
            var status = established
                ? _routes.ValidateTrustedActiveAdmission(expected)
                : _routes.ValidateTrustedReservation(expected);
            local = status ==
                SemanticGatewayRouteSelectionStatus.Selected
                    ? new(status, expected)
                    : new(status, null);
        }
        if (!local.IsSelected)
        {
            return new(local.Status, null, null, null);
        }

        var capacities =
            _routes.TryGetConfiguredAdmissionCapacities(
                local.Selection!);
        if (capacities is null)
        {
            return new(
                SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch,
                null,
                null,
                null);
        }

        var coordinated = await _workerRoutes.FindRouteAsync(
            new CoordinatedWorldRoute(
                target.RealmId,
                target.MapId,
                target.WorldInstanceId),
            deadline,
            cancellationToken);
        if (!coordinated.IsFound)
        {
            return new(
                coordinated.Status ==
                    CoordinationOperationStatus.NotFound
                        ? SemanticGatewayRouteSelectionStatus
                            .WorkerNotFound
                        : SemanticGatewayRouteSelectionStatus
                            .WorkerUnavailable,
                null,
                null,
                null);
        }

        var route = coordinated.Route!;
        if (route.Route != new CoordinatedWorldRoute(
                target.RealmId,
                target.MapId,
                target.WorldInstanceId) ||
            route.NodeId != local.Selection!.NodeId)
        {
            return new(
                SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch,
                null,
                null,
                null);
        }
        if (!Enum.IsDefined(route.WorkerState))
        {
            return new(
                SemanticGatewayRouteSelectionStatus.WorkerUnavailable,
                null,
                null,
                null);
        }
        if (!established &&
            route.WorkerState == CoordinatedWorkerState.Draining)
        {
            return new(
                SemanticGatewayRouteSelectionStatus.WorkerDraining,
                null,
                null,
                null);
        }

        var proof = WorkerProof(route.BootId, route.Revision);
        if (expectedProof is not null &&
            proof != expectedProof.Value)
        {
            return new(
                SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch,
                null,
                null,
                null);
        }

        return new(
            SemanticGatewayRouteSelectionStatus.Selected,
            new SemanticGatewayRouteSelection(
                target,
                route.NodeId,
                proof),
            capacities,
            route);
    }

    private static long WorkerProof(Guid bootId, long revision)
    {
        if (bootId == Guid.Empty || revision <= 0)
        {
            throw new InvalidDataException(
                "A coordinated worker proof is invalid.");
        }

        Span<byte> input = stackalloc byte[24];
        Span<byte> hash = stackalloc byte[32];
        bootId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], revision);
        SHA256.HashData(input, hash);
        var proof =
            BinaryPrimitives.ReadInt64BigEndian(hash) & long.MaxValue;
        CryptographicOperations.ZeroMemory(input);
        CryptographicOperations.ZeroMemory(hash);
        return proof == 0 ? 1 : proof;
    }

    private readonly record struct RedisSemanticGatewayRouteProof(
        SemanticGatewayRouteSelectionStatus Status,
        SemanticGatewayRouteSelection? Selection,
        SemanticGatewayAdmissionCapacities? Capacities,
        CoordinatedRouteSnapshot? CoordinatedRoute)
    {
        public bool IsSelected =>
            Status == SemanticGatewayRouteSelectionStatus.Selected &&
            Selection is not null &&
            Capacities is not null &&
            CoordinatedRoute is not null;
    }
}
