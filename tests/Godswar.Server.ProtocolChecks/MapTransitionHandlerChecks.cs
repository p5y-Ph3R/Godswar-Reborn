using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapTransitionHandlerChecks
{
    private const int AccountId = 131;
    private const int CharacterId = 1_331;
    private const int ViewerAccountId = 132;
    private const int ViewerCharacterId = 1_332;
    private const uint LocalPlayerObjectId = 0x00001448;
    private const byte SpartaMapId = 0;
    private const byte SpartaSuburbMapId = 4;
    private const float PortalRadius = 6f;

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 27, 7, 8, 9, TimeSpan.Zero);

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly MethodInfo StopRealtimeMethod =
        typeof(GameClientHandler).GetMethod(
            "StopRealtimeMovementAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.StopRealtimeMovementAsync was not found.");

    public static async Task RunAsync()
    {
        await RunSafetyChecksAsync();

        await using var actorSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = CreateCharacter(
            CharacterId,
            AccountId,
            "MapTransitionHero",
            SpartaMapId,
            x: 190f,
            z: -120f);
        var pet = CreateSummonedPet(character);
        var store = new MapTransitionStore(character, [pet]);
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);
        var viewer = CreateCharacter(
            ViewerCharacterId,
            ViewerAccountId,
            "MapTransitionViewer",
            SpartaMapId,
            x: 185f,
            z: -120f);
        registry.JoinMap(
            actorSocket.Session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(CharacterId),
            worldReady: true,
            joinedAt: TestTime);
        registry.JoinMap(
            viewerSocket.Session,
            ViewerAccountId,
            viewer,
            WorldObjectIds.ForPlayer(ViewerCharacterId),
            worldReady: true,
            joinedAt: TestTime);

        var handler = CreateEnteredHandler(
            actorSocket.Session,
            store,
            registry,
            character);
        var catalog = MapTraversalCatalog.Default;
        var outward = Resolve(
            catalog,
            SpartaMapId,
            SpartaSuburbMapId);

        await InvokePacketAsync(
            handler,
            CreateWalkPacket(
                outward.SourcePortal.X,
                outward.SourcePortal.Z));

        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                outward.TargetArrival.X,
                y: 0f,
                outward.TargetArrival.Z,
                SpartaSuburbMapId),
            "outward scene change");
        await AssertNextPacketAsync(
            viewerSocket,
            PacketBuilder.RemoveWorldObjects(
                WorldObjectIds.ForPlayer(CharacterId)),
            "source viewer removal");

        AssertPersistedPosition(
            store,
            index: 0,
            SpartaSuburbMapId,
            outward.TargetArrival,
            "outward transition");
        AssertHiddenDestination(
            registry,
            actorSocket.Session,
            character,
            sourceMapId: SpartaMapId,
            targetMapId: SpartaSuburbMapId,
            "outward transition");
        Check.Equal(
            0,
            actorSocket.Available,
            "outward transition sends only scene change before readiness");
        Check.Equal(
            0,
            viewerSocket.Available,
            "triggering walk is not broadcast after source removal");

        await InvokePacketAsync(
            handler,
            CreateControlPacket(Opcodes.ClientReady));
        AssertHiddenDestination(
            registry,
            actorSocket.Session,
            character,
            sourceMapId: SpartaMapId,
            targetMapId: SpartaSuburbMapId,
            "outward ClientReady-only gate");
        Check.Equal(
            0,
            actorSocket.Available,
            "ClientReady alone emits no destination world data");

        await InvokePacketAsync(
            handler,
            CreatePlayerDetailRequest());
        await AssertDetailAndCompletedTransitionAsync(
            actorSocket,
            character,
            "outward transition");
        AssertActiveDestination(
            registry,
            actorSocket.Session,
            character,
            SpartaSuburbMapId,
            "outward transition");
        AssertNoFullBootstrapReplay(
            handler,
            store,
            expectedPetPresenceReads: 1,
            "outward transition");

        // The source removal was already observed. Removing the test viewer
        // keeps the reverse transition packet stream focused on the actor.
        registry.Remove(viewerSocket.Session);

        var reverse = Resolve(
            catalog,
            SpartaSuburbMapId,
            SpartaMapId);
        Check.Equal(
            new MapTraversalPosition(193f, -120f),
            reverse.TargetArrival,
            "reverse transition uses walkable Sparta gate anchor");
        await InvokePacketAsync(
            handler,
            CreateWalkPacket(
                reverse.SourcePortal.X,
                reverse.SourcePortal.Z));
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                reverse.TargetArrival.X,
                y: 0f,
                reverse.TargetArrival.Z,
                SpartaMapId),
            "reverse scene change");
        AssertPersistedPosition(
            store,
            index: 1,
            SpartaMapId,
            reverse.TargetArrival,
            "reverse transition");
        AssertHiddenDestination(
            registry,
            actorSocket.Session,
            character,
            sourceMapId: SpartaSuburbMapId,
            targetMapId: SpartaMapId,
            "reverse transition");

        // Reverse the readiness order to prove that neither message can
        // independently activate the destination.
        await InvokePacketAsync(
            handler,
            CreatePlayerDetailRequest());
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.PlayerDetail(character),
            "reverse pre-ready player detail");
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.PlayerStatusUpdate(character, 1f),
            "reverse pre-ready player status");
        AssertHiddenDestination(
            registry,
            actorSocket.Session,
            character,
            sourceMapId: SpartaSuburbMapId,
            targetMapId: SpartaMapId,
            "reverse PlayerDetail-only gate");
        Check.Equal(
            0,
            actorSocket.Available,
            "PlayerDetail alone emits no destination world data");

        await InvokePacketAsync(
            handler,
            CreateControlPacket(Opcodes.ClientReady));
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.PetWorldPresence(
                checked((uint)pet.PetId),
                LocalPlayerObjectId),
            "reverse summoned-pet restore");
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.PlayerStatusUpdate(character, 1f),
            "reverse completed player status");
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.PlayerStatusEffects(
                character,
                [],
                ClientStatusAggregate.Empty),
            "reverse completed status effects");
        AssertActiveDestination(
            registry,
            actorSocket.Session,
            character,
            SpartaMapId,
            "reverse transition");
        AssertNoFullBootstrapReplay(
            handler,
            store,
            expectedPetPresenceReads: 2,
            "reverse transition");

        Check.Equal(
            0,
            actorSocket.Available,
            "reverse completion has no unexpected reliable packets");

        await StopHandlerAsync(handler);
        registry.Remove(actorSocket.Session);
    }

    private static MapTraversalResolution Resolve(
        MapTraversalCatalog catalog,
        byte sourceMapId,
        byte targetMapId)
    {
        Check.True(
            catalog.TryGetAutomaticLink(
                sourceMapId,
                targetMapId,
                out var link),
            $"map link {sourceMapId}->{targetMapId} exists");
        Check.True(
            catalog.TryResolveTargetArrival(
                link,
                PortalRadius,
                out var resolution),
            $"map link {sourceMapId}->{targetMapId} resolves");
        return resolution;
    }

    private static GameClientHandler CreateEnteredHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        GameCharacter character,
        TimeSpan? mapTransitionReadyTimeout = null)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            mapTransitionReadyTimeout:
                mapTransitionReadyTimeout);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = character.AccountId,
                Username = "map-transition-handler"
            });
        SetField(handler, "_character", character);
        SetField(handler, "_registered", true);
        SetField(handler, "_worldPresenceAnnounced", true);

        // Arm every ordinary login prerequisite while leaving the sent flag
        // false. Any map-transition fall-through would now replay the full
        // post-enter bootstrap and be visible to the assertions below.
        SetField(handler, "_clientReadyReceived", true);
        SetField(handler, "_playerDetailSent", true);
        SetField(handler, "_enterUiReadyReceived", true);
        SetField(handler, "_postEnterBootstrapSent", false);
        return handler;
    }

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    private static async Task StopHandlerAsync(
        GameClientHandler handler)
    {
        var task = StopRealtimeMethod.Invoke(
            handler,
            null) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.StopRealtimeMovementAsync returned no task.");
        await task;
    }

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private static bool GetBooleanField(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        return (bool)(field.GetValue(handler)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} returned null."));
    }

    private static GamePacket CreateWalkPacket(
        float targetX,
        float targetZ)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0xA5A5_0001u);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            targetX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            targetZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            1f);
        return new GamePacket(packet);
    }

    private static GamePacket CreateControlPacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            (ushort)packet.Length);
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
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PlayerDetailRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        return new GamePacket(packet);
    }

}
