using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private static async Task
        CheckWorldRehydrationPreservesTransportAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session =
            new ClientSession(transport);
        var character = CreateCharacter(
            CharacterId + 30,
            AccountId + 30,
            "RealtimeWorldRehydrate");
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(0, [], TestTime);
        registry.InitializeMapMonsters(1, [], TestTime);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var handler = CreateReadyHandler(
            session,
            registry,
            character);

        await ProcessTickAsync(handler);
        var initial = transport.Snapshots.Single();
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initial.WorldGeneration,
                legacyState: 0xABCD_0001,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        await ProcessTickAsync(handler);
        var accepted = transport.Snapshots.Last();
        Check.True(
            accepted.AcknowledgedInputId == 1 &&
            accepted.PositionRevision == 1,
            "pre-transition input establishes the global acknowledgement and revision");

        character.CurrentMap = 1;
        character.PositionX = 10f;
        character.PositionZ = 20f;
        registry.UpdateCharacter(
            session,
            character,
            advanceWorldRevision: true);
        await ProcessTickAsync(handler);
        var mapKeyframe = transport.Snapshots.Last();
        Check.True(
            (mapKeyframe.Flags &
                SecureRealtimeSnapshotFlags.Keyframe) != 0 &&
            mapKeyframe.TransportEpoch == 1 &&
            mapKeyframe.AcknowledgedInputId == 1 &&
            mapKeyframe.PositionRevision == 2 &&
            mapKeyframe.WorldGeneration >
                initial.WorldGeneration &&
            mapKeyframe.MapId == 1 &&
            mapKeyframe.X == 10f &&
            mapKeyframe.Z == 20f,
            "map rehydration publishes a non-rollback keyframe");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 2,
                initial.WorldGeneration,
                legacyState: 0xEEEE_0002,
                x: 0.75f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(150)));
        var staleEffects = await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                staleEffects,
                "ReliableCorrection") is not null,
            "old-world input receives a reliable correction");
        var staleCorrection = transport.Snapshots.Last();
        Check.True(
            staleCorrection.Rejection ==
                SecureRealtimeMovementRejection.StaleInput &&
            staleCorrection.AcknowledgedInputId == 2 &&
            staleCorrection.PositionRevision == 2 &&
            staleCorrection.WorldGeneration ==
                mapKeyframe.WorldGeneration,
            "processed old-world input is acknowledged without rolling back the new world");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 3,
                mapKeyframe.WorldGeneration,
                legacyState: 0xBCDE_0003,
                x: 10.25f,
                z: 20f,
                TimeSpan.FromMilliseconds(200),
                mapId: 1));
        await ProcessTickAsync(handler);
        await ProcessTickAsync(handler);
        var resumed = transport.Snapshots.Last();
        Check.True(
            resumed.AcknowledgedInputId == 3 &&
            resumed.PositionRevision == 3 &&
            character.PositionX == 10.25f &&
            character.PositionZ == 20f,
            "movement resumes on the rehydrated world without transport fallback");

        SetField(handler, "_worldPresenceAnnounced", false);
        await ProcessTickAsync(handler);
        character.CurrentMap = 0;
        character.PositionX = 165f;
        character.PositionZ = -97f;
        SetField(handler, "_worldPresenceAnnounced", true);
        await ProcessTickAsync(handler);
        var reviveKeyframe = transport.Snapshots.Last();
        Check.True(
            (reviveKeyframe.Flags &
                SecureRealtimeSnapshotFlags.Keyframe) != 0 &&
            reviveKeyframe.TransportEpoch == 1 &&
            reviveKeyframe.AcknowledgedInputId == 3 &&
            reviveKeyframe.PositionRevision == 4 &&
            reviveKeyframe.WorldGeneration >
                mapKeyframe.WorldGeneration &&
            reviveKeyframe.MapId == 0,
            "revive/world re-entry preserves the session transport and acknowledgement");

        registry.Remove(session);
    }
}
