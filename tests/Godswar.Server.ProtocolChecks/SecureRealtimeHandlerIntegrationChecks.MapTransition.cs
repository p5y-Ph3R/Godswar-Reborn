using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private const byte MapTransitionSourceMapId = 0;
    private const byte MapTransitionTargetMapId = 4;
    private const float MapTransitionStartX = 197.5f;
    private const float MapTransitionEndX = 198.5f;
    private const float MapTransitionZ = -120f;

    private static async Task
        CheckSecureRealtimeMapTransitionAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var actorSession =
            new ClientSession(transport);
        await using var sourceViewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetViewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();

        var actor = CreateCharacter(
            CharacterId + 40,
            AccountId + 40,
            "RealtimeMapTraveler");
        actor.PositionX = MapTransitionStartX;
        actor.PositionZ = MapTransitionZ;
        var sourceViewer = CreateCharacter(
            ViewerCharacterId + 40,
            ViewerAccountId + 40,
            "RealtimeSourceViewer");
        sourceViewer.PositionX = 180f;
        sourceViewer.PositionZ = -120f;
        var targetViewer = CreateCharacter(
            ViewerCharacterId + 41,
            ViewerAccountId + 41,
            "RealtimeTargetViewer");
        targetViewer.CurrentMap = MapTransitionTargetMapId;
        targetViewer.PositionX = 90f;
        targetViewer.PositionZ = -220f;

        var store = new MapTransitionStore(actor);
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            MapTransitionSourceMapId,
            [],
            TestTime);
        registry.InitializeMapMonsters(
            MapTransitionTargetMapId,
            [],
            TestTime);
        registry.JoinMap(
            actorSession,
            actor.AccountId,
            actor,
            WorldObjectIds.ForPlayer(actor.Id));
        registry.JoinMap(
            sourceViewerSocket.Session,
            sourceViewer.AccountId,
            sourceViewer,
            WorldObjectIds.ForPlayer(sourceViewer.Id));
        registry.JoinMap(
            targetViewerSocket.Session,
            targetViewer.AccountId,
            targetViewer,
            WorldObjectIds.ForPlayer(targetViewer.Id));
        var handler = CreateMapTransitionHandler(
            actorSession,
            store,
            registry,
            actor);

        await ProcessTickAsync(handler);
        var sourceKeyframe = transport.Snapshots.Single();
        Check.True(
            sourceKeyframe.MapId == MapTransitionSourceMapId &&
            sourceKeyframe.AcknowledgedInputId == 0 &&
            sourceKeyframe.WorldGeneration != 0,
            "map transition starts from a published source-world keyframe");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                sourceKeyframe.WorldGeneration,
                legacyState: 0xD00D_0001,
                x: MapTransitionEndX,
                z: MapTransitionZ,
                TimeSpan.FromMilliseconds(100),
                mapId: MapTransitionSourceMapId));
        var transitionEffects =
            await ProcessTickAsync(handler);

        AssertNoMovementEffects(
            transitionEffects,
            "accepted portal-transition input");
        Check.True(
            GetEffectPositionSave(transitionEffects) is null,
            "accepted portal-transition input cannot enqueue an old-world position save");
        await PublishEffectsAsync(handler, transitionEffects);
        Check.Equal(
            1,
            store.PositionWrites.Count,
            "portal transition persists exactly one destination relocation");
        var relocation = store.PositionWrites.Single();
        Check.True(
            relocation.MapId == MapTransitionTargetMapId &&
            relocation.X == actor.PositionX &&
            relocation.Z == actor.PositionZ,
            "portal relocation persistence uses the authoritative destination");

        var sceneChange = transport.TakeClearLegacyWrites();
        AssertSceneChange(
            sceneChange,
            MapTransitionTargetMapId,
            actor.PositionX,
            actor.PositionZ);
        var sourceRemove =
            await sourceViewerSocket.ReadPacketAsync(12);
        Check.Equal(
            (ushort)0x2728,
            BinaryPrimitives.ReadUInt16LittleEndian(
                sourceRemove.AsSpan(2)),
            "source observer receives object removal, not old-world movement");
        Check.Equal(
            WorldObjectIds.ForPlayer(actor.Id),
            BinaryPrimitives.ReadUInt32LittleEndian(
                sourceRemove.AsSpan(8)),
            "source removal targets the transitioning player");
        Check.Equal(
            0,
            sourceViewerSocket.Available,
            "source observer receives no trailing movement after removal");

        Check.Equal(
            MapTransitionTargetMapId,
            actor.CurrentMap,
            "accepted portal input moves server authority to the destination");
        Check.True(
            !registry.TryGetMapSessionByObjectId(
                MapTransitionTargetMapId,
                WorldObjectIds.ForPlayer(actor.Id),
                excludeSession: null,
                out _),
            "transitioning player remains hidden in the destination");
        Check.Equal(
            1,
            registry.GetMapSessions(
                MapTransitionTargetMapId).Count,
            "destination readers expose only the pre-existing ready viewer");
        Check.Equal(
            0,
            targetViewerSocket.Available,
            "destination observer sees no player before readiness");
        Check.Equal(
            1,
            transport.Snapshots.Count,
            "transition publishes no destination snapshot before readiness");

        await InvokePacketAsync(
            handler,
            CreateControlPacket(Opcodes.ClientReady));
        Check.True(
            !registry.TryGetMapSessionByObjectId(
                MapTransitionTargetMapId,
                WorldObjectIds.ForPlayer(actor.Id),
                excludeSession: null,
                out _),
            "ClientReady alone cannot expose a partially hydrated player");
        Check.Equal(
            0,
            targetViewerSocket.Available,
            "ClientReady alone emits no destination player spawn");
        Check.Equal(
            1,
            transport.Snapshots.Count,
            "ClientReady alone emits no destination keyframe");

        await InvokePacketAsync(
            handler,
            CreatePlayerDetailRequest());
        Check.True(
            registry.TryGetMapSessionByObjectId(
                MapTransitionTargetMapId,
                WorldObjectIds.ForPlayer(actor.Id),
                excludeSession: null,
                out var readyActor) &&
            readyActor.WorldReady,
            "fresh PlayerDetail after ClientReady activates destination ownership");
        Check.True(
            targetViewerSocket.Available > 0,
            "destination observer receives the player only after both readiness messages");

        var destinationKeyframe = transport.Snapshots.Last();
        Check.True(
            (destinationKeyframe.Flags &
                SecureRealtimeSnapshotFlags.Keyframe) != 0 &&
            destinationKeyframe.WorldGeneration >
                sourceKeyframe.WorldGeneration &&
            destinationKeyframe.MapId ==
                MapTransitionTargetMapId &&
            destinationKeyframe.AcknowledgedInputId == 1 &&
            destinationKeyframe.PositionRevision == 2 &&
            destinationKeyframe.X == actor.PositionX &&
            destinationKeyframe.Z == actor.PositionZ,
            "destination keyframe advances the world while preserving the portal input acknowledgement");

        var destinationX = actor.PositionX;
        var destinationZ = actor.PositionZ;
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 2,
                sourceKeyframe.WorldGeneration,
                legacyState: 0xD00D_0002,
                x: destinationX + 0.25f,
                z: destinationZ,
                TimeSpan.FromMilliseconds(200),
                mapId: MapTransitionSourceMapId));
        var staleEffects =
            await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                staleEffects,
                "ViewerMovement") is null &&
            GetEffectPositionSave(staleEffects) is null &&
            GetEffectPacket(
                staleEffects,
                "ReliableCorrection") is not null,
            "stale source-world UDP receives correction without movement or persistence");
        Check.True(
            actor.CurrentMap == MapTransitionTargetMapId &&
            actor.PositionX == destinationX &&
            actor.PositionZ == destinationZ,
            "stale source-world UDP cannot mutate destination authority");
        var staleCorrection = transport.Snapshots.Last();
        Check.True(
            staleCorrection.Rejection ==
                SecureRealtimeMovementRejection.StaleInput &&
            staleCorrection.WorldGeneration ==
                destinationKeyframe.WorldGeneration &&
            staleCorrection.MapId ==
                MapTransitionTargetMapId &&
            staleCorrection.AcknowledgedInputId == 2 &&
            staleCorrection.PositionRevision ==
                destinationKeyframe.PositionRevision,
            "stale source generation is acknowledged and rejected in the destination generation");
        Check.Equal(
            1,
            store.PositionWrites.Count,
            "stale source-world UDP cannot create another position write");

        registry.Remove(actorSession);
        registry.Remove(sourceViewerSocket.Session);
        registry.Remove(targetViewerSocket.Session);
    }

    private static GameClientHandler CreateMapTransitionHandler(
        ClientSession session,
        MapTransitionStore store,
        GameSessionRegistry registry,
        GameCharacter character)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = character.AccountId,
                Username =
                    $"map-transition-{character.AccountId}"
            });
        SetField(handler, "_character", character);
        SetField(handler, "_registered", true);
        SetField(handler, "_worldPresenceAnnounced", true);
        SetField(
            handler,
            "_npcVisibility",
            new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
                [],
                static npc => npc.ObjectId,
                static npc => npc.X,
                static npc => npc.Z,
                "NPC"));
        return handler;
    }

    private static GamePacket CreateControlPacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        return new GamePacket(packet);
    }

    private static GamePacket CreatePlayerDetailRequest()
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PlayerDetailRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        return new GamePacket(packet);
    }

    private static object? GetEffectPositionSave(object effects) =>
        effects.GetType().GetProperty("PositionSave")?
            .GetValue(effects);

    private static void AssertSceneChange(
        byte[] packet,
        byte expectedMapId,
        float expectedX,
        float expectedZ)
    {
        Check.Equal(
            24,
            packet.Length,
            "transition scene-change byte length");
        Check.Equal(
            Opcodes.SceneChange,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2)),
            "transition scene-change opcode");
        Check.Equal(
            LocalPlayerObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4)),
            "transition scene-change local object");
        Check.Equal(
            expectedX,
            BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(8)),
            "transition scene-change destination X");
        Check.Equal(
            expectedZ,
            BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(16)),
            "transition scene-change destination Z");
        Check.Equal(
            (ushort)expectedMapId,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(22)),
            "transition scene-change destination map");
    }

    private sealed class MapTransitionStore(
        GameCharacter character) : GameStoreTestStub
    {
        private readonly GameCharacter _character = character;

        public List<MapPositionWrite> PositionWrites { get; } = [];

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PositionWrites.Add(
                new MapPositionWrite(
                    accountId,
                    characterId,
                    currentMap,
                    positionX,
                    positionZ));
            return Task.CompletedTask;
        }

        public override Task<CharacterStats?>
            GetCharacterStatsAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CharacterStats?>(
                accountId == _character.AccountId &&
                characterId == _character.Id
                    ? CharacterStats.FromCharacter(_character)
                    : null);
        }

    }

    private sealed record MapPositionWrite(
        int AccountId,
        int CharacterId,
        byte MapId,
        float X,
        float Z);
}
