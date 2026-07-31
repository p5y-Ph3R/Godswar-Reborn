using System.Net;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GameHandlerCheckpointLifecycleChecks
{
    private static async Task CheckGatewayLocalMapTransitionRouteAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = snapshot.Character ??
            throw new InvalidOperationException(
                "Coordination route fixture requires a character.");
        var issuer = new RecordingLeaseIssuer(acquire: true, []);
        Check.True(
            issuer.TryResolveRoute(
                character.Location.CurrentMap,
                out var initialRoute),
            "coordination fixture owns the initial route");
        Check.True(
            issuer.TryResolveRoute(4, out var destinationRoute),
            "coordination fixture owns the destination route");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var admission = new GatewayWorldAdmission(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            snapshot.AccountId,
            character.Identity.CharacterId,
            "checkpoint-fixture",
            initialRoute.RealmId,
            initialRoute.MapId,
            initialRoute.WorldInstanceId,
            issuer.NodeId,
            now,
            now.AddSeconds(30),
            new IPEndPoint(IPAddress.Loopback, 40_001));
        await using var session = new ClientSession(
            new CoordinationGatewayTransport(admission));
        var handler = CreateHandler(
            session,
            new FixedSnapshotReader(snapshot),
            characterCheckpoints: null,
            issuer);

        Check.True(
            ResolveRoute(
                handler,
                character.Location.CurrentMap,
                requireInitialGatewayRoute: true,
                out var resolvedInitial) &&
            resolvedInitial == initialRoute,
            "gateway entry requires its exact admitted initial route");
        Check.True(
            ResolveRoute(
                handler,
                4,
                requireInitialGatewayRoute: false,
                out var resolvedDestination) &&
            resolvedDestination == destinationRoute,
            "server-authorized portal transition accepts another route " +
            "owned by the admitted worker");
        Check.True(
            !ResolveRoute(
                handler,
                4,
                requireInitialGatewayRoute: true,
                out _),
            "a destination route cannot masquerade as the initial " +
            "gateway admission");

        var wrongNodeAdmission = new GatewayWorldAdmission(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            snapshot.AccountId,
            character.Identity.CharacterId,
            "checkpoint-fixture",
            initialRoute.RealmId,
            initialRoute.MapId,
            initialRoute.WorldInstanceId,
            new ServerNodeId("other-worker"),
            now,
            now.AddSeconds(30),
            new IPEndPoint(IPAddress.Loopback, 40_002));
        await using var wrongNodeSession = new ClientSession(
            new CoordinationGatewayTransport(wrongNodeAdmission));
        var wrongNodeHandler = CreateHandler(
            wrongNodeSession,
            new FixedSnapshotReader(snapshot),
            characterCheckpoints: null,
            issuer);
        Check.True(
            !ResolveRoute(
                wrongNodeHandler,
                4,
                requireInitialGatewayRoute: false,
                out _),
            "gateway admission cannot transition through another worker");
    }

    private static bool ResolveRoute(
        GameClientHandler handler,
        byte mapId,
        bool requireInitialGatewayRoute,
        out CoordinatedWorldRoute route)
    {
        object?[] arguments =
        [
            mapId,
            default(CoordinatedWorldRoute),
            requireInitialGatewayRoute
        ];
        var resolved =
            (bool)(TryResolveCoordinatedRouteMethod.Invoke(
                handler,
                arguments) ?? false);
        route = arguments[1] is CoordinatedWorldRoute value
            ? value
            : default;
        return resolved;
    }

    private sealed class RecordingLeaseIssuer(
        bool acquire,
        List<string> operations) : IPlayerCoordinationLeaseIssuer
    {
        public bool IsEnabled => true;

        public ServerNodeId NodeId { get; } =
            new("checkpoint-worker");

        public bool TryResolveRoute(
            byte legacyMapId,
            out CoordinatedWorldRoute route)
        {
            route = new CoordinatedWorldRoute(
                RealmId.Tempest,
                MapId.FromLegacy(legacyMapId),
                new WorldInstanceId(
                    new Guid(
                        legacyMapId + 1,
                        0,
                        0,
                        new byte[8])));
            return true;
        }

        public ValueTask<IPlayerCoordinationLease?> AcquireAsync(
            int accountId,
            int characterId,
            PlayerOwnershipFence ownership,
            CoordinatedWorldRoute route,
            Action ownershipLost,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IPlayerCoordinationLease?>(
                acquire
                    ? new RecordingPlayerLease(ownership, operations)
                    : null);
    }

    private sealed class RecordingPlayerLease(
        PlayerOwnershipFence ownership,
        List<string> operations) : IPlayerCoordinationLease
    {
        public PlayerOwnershipFence Ownership { get; } = ownership;

        public bool IsCurrent => true;

        public ValueTask<bool> PublishEnteringAsync(
            CoordinatedWorldRoute route,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> PublishOnlineAsync(
            CoordinatedWorldRoute route,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisposeAsync()
        {
            operations.Add("redis-release");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CoordinationGatewayTransport(
        GatewayWorldAdmission admission) :
        ILegacyByteTransport,
        IAuthenticatedGameTransport
    {
        public string RemoteEndPoint => "coordination-gateway-test";

        public SecureBoundGamePrincipal BoundGamePrincipal { get; } =
            admission.CreatePrincipal();

        public GatewayWorldAdmission WorldAdmission { get; } = admission;

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void MarkAuthenticated()
        {
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
