using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private static async Task
        CheckReplacementSessionRealtimeMovementAsync()
    {
        await CheckReplacementRejectsQueuedMovementAsync();
        await CheckReplacementRejectsDelayedEffectsAsync();
    }

    private static async Task
        CheckReplacementRejectsQueuedMovementAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var staleSession =
            new ClientSession(transport);
        await using var replacementTransport =
            new ScriptedLegacyByteTransport();
        await using var replacementSession =
            new ClientSession(
                replacementTransport,
                endpointRole: NetworkEndpointRole.Game);
        var character = CreateCharacter(
            CharacterId + 100,
            AccountId + 100,
            "RealtimeReplacedQueued");
        var registry = new GameSessionRegistry();
        BindOwnedWorld(
            registry,
            staleSession,
            character);
        var handler = CreateReadyHandler(
            staleSession,
            registry,
            character);
        SetField(
            handler,
            "_accountSessionRegistered",
            true);

        await ProcessTickAsync(handler);
        var initialSnapshot = transport.Snapshots.Single();
        var snapshotsBeforeReplacement =
            transport.Snapshots.Count;
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initialSnapshot.WorldGeneration,
                legacyState: 0xA001_0001,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        Check.True(
            ReferenceEquals(
                staleSession,
                registry.ReplaceAccountSession(
                    character.AccountId,
                    replacementSession)),
            "realtime replacement identifies stale queued-input owner");

        var staleEffects = await ProcessTickAsync(handler);
        AssertNoMovementEffects(
            staleEffects,
            "replaced-session queued movement");
        Check.True(
            character.PositionX == 0f &&
            character.PositionZ == 0f,
            "replaced-session queued input cannot change position");
        Check.Equal(
            snapshotsBeforeReplacement,
            transport.Snapshots.Count,
            "replaced-session queued input cannot publish a snapshot");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "replaced realtime session is disconnected at tick gate");
    }

    private static async Task
        CheckReplacementRejectsDelayedEffectsAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var staleSession =
            new ClientSession(transport);
        await using var replacementTransport =
            new ScriptedLegacyByteTransport();
        await using var replacementSession =
            new ClientSession(
                replacementTransport,
                endpointRole: NetworkEndpointRole.Game);
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = CreateCharacter(
            CharacterId + 110,
            AccountId + 110,
            "RealtimeReplacedEffects");
        var viewer = CreateCharacter(
            ViewerCharacterId + 110,
            ViewerAccountId + 110,
            "RealtimeReplacementViewer");
        var checkpoints =
            new GameHandlerCheckpointCoordinatorStub();
        var registry = new GameSessionRegistry();
        BindOwnedWorld(
            registry,
            staleSession,
            character);
        registry.JoinMap(
            viewerSocket.Session,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id));
        var handler = CreateReadyHandler(
            staleSession,
            registry,
            character,
            characterCheckpoints: checkpoints);
        SetField(
            handler,
            "_accountSessionRegistered",
            true);

        await ProcessTickAsync(handler);
        var initialSnapshot = transport.Snapshots.Single();
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initialSnapshot.WorldGeneration,
                legacyState: 0xA002_0001,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        var acceptedEffects = await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                acceptedEffects,
                "ViewerMovement") is not null,
            "current realtime owner produces delayed viewer effects");
        Check.True(
            character.PositionX == 0.5f &&
            character.PositionZ == 0.25f,
            "current realtime owner applies accepted input");
        Check.True(
            ReferenceEquals(
                staleSession,
                registry.ReplaceAccountSession(
                    character.AccountId,
                    replacementSession)),
            "realtime replacement identifies delayed-effect owner");

        await PublishEffectsAsync(
            handler,
            acceptedEffects);
        Check.Equal(
            0,
            viewerSocket.Available,
            "replaced realtime session cannot broadcast delayed movement");
        Check.Equal(
            0,
            checkpoints.PositionEnqueueCount,
            "replaced realtime session cannot enqueue delayed position save");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "replaced realtime session is disconnected at effect gate");

        registry.Remove(viewerSocket.Session);
    }

    private static void BindOwnedWorld(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character)
    {
        character.CheckpointOwnerId = CheckpointOwnerId;
        character.CheckpointOwnerGeneration = 1;
        var ownership = new PlayerOwnershipFence(
            character.CheckpointOwnerId,
            character.CheckpointOwnerGeneration);
        registry.ReplaceAccountSession(
            character.AccountId,
            session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                character.AccountId,
                session,
                ownership),
            "realtime fixture binds exact player ownership");
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
    }
}
