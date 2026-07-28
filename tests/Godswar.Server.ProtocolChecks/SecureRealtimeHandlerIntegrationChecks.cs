using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private const int AccountId = 7_041;
    private const int CharacterId = 8_051;
    private const int ViewerAccountId = 7_042;
    private const int ViewerCharacterId = 8_052;
    private const uint LocalPlayerObjectId = 0x0000_1448;

    private static readonly MethodInfo ProcessTickMethod =
        FindHandlerMethod("ProcessRealtimeMovementTickAsync");
    private static readonly MethodInfo PublishEffectsMethod =
        FindHandlerMethod("PublishRealtimeMovementEffectsAsync");
    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");

    public static async Task RunAsync()
    {
        await CheckAuthoritativeMovementAndFallbackAsync();
        await CheckAcceptanceFaultCorrectionAsync();
        await CheckFailedAcceptanceCorrectionEgressAsync();
        await CheckFirstRejectedInputUsesServerStateAsync();
        await CheckWorldRehydrationPreservesTransportAsync();
        await CheckSecureRealtimeMapTransitionAsync();
        await CheckLegacyMovementRejectedAfterCutoverAsync();
        await CheckAcceptedRealtimeMovementInterruptsCastAsync();
    }

    private static async Task
        CheckAuthoritativeMovementAndFallbackAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var actorSession =
            new ClientSession(transport);
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = CreateCharacter(
            CharacterId,
            AccountId,
            "RealtimeActor");
        var viewer = CreateCharacter(
            ViewerCharacterId,
            ViewerAccountId,
            "RealtimeViewer");
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [],
            TestTime);
        registry.JoinMap(
            actorSession,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        registry.JoinMap(
            viewerSocket.Session,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id));
        var handler = CreateReadyHandler(
            actorSession,
            registry,
            character);

        var initialEffects =
            await ProcessTickAsync(handler);
        AssertNoMovementEffects(
            initialEffects,
            "initial baseline");
        var initialSnapshot = transport.Snapshots.Single();
        Check.True(
            initialSnapshot.Flags ==
                SecureRealtimeSnapshotFlags.Keyframe &&
            initialSnapshot.TransportEpoch == 1 &&
            initialSnapshot.AcknowledgedInputId == 0 &&
            initialSnapshot.PositionRevision == 0 &&
            initialSnapshot.WorldGeneration != 0 &&
            initialSnapshot.MapId == character.CurrentMap &&
            initialSnapshot.X == character.PositionX &&
            initialSnapshot.Z == character.PositionZ,
            "ready handler publishes an immediate authoritative keyframe before input");

        const uint movementState = 0xCAFE_0001;
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initialSnapshot.WorldGeneration,
                movementState,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        var acceptedEffects =
            await ProcessTickAsync(handler);
        var acceptedViewerPacket =
            GetEffectPacket(
                acceptedEffects,
                "ViewerMovement")
            ?? throw new InvalidOperationException(
                "Accepted realtime movement produced no viewer packet.");
        Check.True(
            GetEffectPacket(
                acceptedEffects,
                "ReliableCorrection") is null,
            "accepted realtime movement produces no reliable correction");
        AssertCanonicalWalk(
            acceptedViewerPacket,
            WorldObjectIds.ForPlayer(character.Id),
            movementState,
            0.5f,
            0.25f,
            "accepted realtime viewer packet");
        await PublishEffectsAsync(
            handler,
            acceptedEffects);
        var deliveredViewerPacket =
            await viewerSocket.ReadPacketAsync(20);
        Check.True(
            deliveredViewerPacket.SequenceEqual(
                acceptedViewerPacket),
            "accepted realtime movement broadcasts the canonical 20-byte viewer packet");

        var fallbackRetry = CreateIngress(
            SecureRealtimeTransportSource.Tls,
            SecureRealtimeMovementIngressKind.TransportTransition,
            transportEpoch: 2,
            inputId: 1,
            initialSnapshot.WorldGeneration,
            movementState,
            x: 0.5f,
            z: 0.25f,
            TimeSpan.FromMilliseconds(200));
        transport.EnqueueMovement(fallbackRetry);
        var transitionEffects =
            await ProcessTickAsync(handler);
        AssertNoMovementEffects(
            transitionEffects,
            "first TLS transition retry");

        transport.EnqueueMovement(fallbackRetry);
        var repeatedTransitionEffects =
            await ProcessTickAsync(handler);
        AssertNoMovementEffects(
            repeatedTransitionEffects,
            "repeated TLS transition retry");
        Check.True(
            character.PositionX == 0.5f &&
            character.PositionZ == 0.25f &&
            viewerSocket.Available == 0,
            "transport-transition retry is never applied or broadcast twice");
        var handoffSnapshot = transport.Snapshots.Last();
        Check.True(
            handoffSnapshot.TransportEpoch == 2 &&
            handoffSnapshot.AcknowledgedInputId == 1 &&
            handoffSnapshot.PositionRevision == 1,
            "TLS transition preserves the global input acknowledgement and revision");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 2,
                inputId: 2,
                initialSnapshot.WorldGeneration,
                legacyState: 0xBEEF_0002,
                x: 100f,
                z: 100f,
                TimeSpan.FromMilliseconds(300)));
        var rejectedEffects =
            await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                rejectedEffects,
                "ViewerMovement") is null,
            "rejected TLS fallback movement cannot produce viewer movement");
        var reliableCorrection =
            GetEffectPacket(
                rejectedEffects,
                "ReliableCorrection")
            ?? throw new InvalidOperationException(
                "Rejected TLS fallback movement produced no correction.");
        AssertCanonicalWalk(
            reliableCorrection,
            LocalPlayerObjectId,
            movementState,
            0.5f,
            0.25f,
            "rejected TLS fallback correction");
        await PublishEffectsAsync(
            handler,
            rejectedEffects);
        var deliveredCorrection =
            transport.TakeClearLegacyWrites();
        Check.True(
            deliveredCorrection.SequenceEqual(
                reliableCorrection),
            "rejected TLS fallback movement sends its reliable correction over the legacy TLS stream");
        var correctionSnapshot = transport.Snapshots.Last();
        Check.True(
            (correctionSnapshot.Flags &
                SecureRealtimeSnapshotFlags.Correction) != 0 &&
            correctionSnapshot.Rejection ==
                SecureRealtimeMovementRejection.Distance &&
            correctionSnapshot.AcknowledgedInputId == 2 &&
            correctionSnapshot.PositionRevision == 1,
            "rejected fallback movement publishes a sequenced authoritative correction snapshot");
        Check.True(
            viewerSocket.Available == 0,
            "rejected fallback movement is never broadcast to viewers");

        registry.Remove(actorSession);
        registry.Remove(viewerSocket.Session);
    }

    private static async Task
        CheckLegacyMovementRejectedAfterCutoverAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        transport.ActivateRealtimeMovement();
        await using var session =
            new ClientSession(transport);
        var character = CreateCharacter(
            CharacterId + 10,
            AccountId + 10,
            "RealtimeLegacyReject");
        character.PositionX = 12f;
        character.PositionZ = -7f;
        var handler = CreateReadyHandler(
            session,
            new GameSessionRegistry(),
            character);
        var legacyWalk = CreateLegacyWalk(
            0xDEAD_0001,
            x: 99f,
            z: 101f);

        await InvokePacketAsync(
            handler,
            new GamePacket(legacyWalk));

        Check.True(
            character.PositionX == 12f &&
            character.PositionZ == -7f,
            "raw legacy movement cannot mutate position after secure cutover");
        var correction =
            transport.TakeClearLegacyWrites();
        AssertCanonicalWalk(
            correction,
            LocalPlayerObjectId,
            expectedHighState: 0x0002_0000,
            expectedX: 12f,
            expectedZ: -7f,
            "raw legacy cutover correction");
    }

    private static GameClientHandler CreateReadyHandler(
        ClientSession session,
        GameSessionRegistry registry,
        GameCharacter character,
        SecurePhase4AcceptanceFaults?
            phase4AcceptanceFaults = null)
    {
        var handler = new GameClientHandler(
            session,
            new NoopMovementStore(),
            registry,
            phase4AcceptanceFaults:
                phase4AcceptanceFaults);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = character.AccountId,
                Username = $"realtime-{character.AccountId}"
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

    private static GameCharacter CreateCharacter(
        int characterId,
        int accountId,
        string name) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = 0,
            PositionX = 0f,
            PositionZ = 0f,
            Level = 80,
            CurrentHp = 8_000,
            MaxHp = 8_000,
            CurrentMp = 4_000,
            MaxMp = 4_000
        };

    private static SecureRealtimeMovementIngress CreateIngress(
        SecureRealtimeTransportSource source,
        SecureRealtimeMovementIngressKind kind,
        uint transportEpoch,
        ulong inputId,
        uint worldGeneration,
        uint legacyState,
        float x,
        float z,
        TimeSpan receivedAt,
        byte mapId = 0) =>
        new(
            new SecureRealtimeMovementInput(
                source == SecureRealtimeTransportSource.Tls
                    ? SecureRealtimeMovementFlags.CurrentWorld
                    : SecureRealtimeMovementFlags.None,
                transportEpoch,
                inputId,
                checked(inputId * 50),
                worldGeneration,
                legacyState,
                x,
                z,
                Auxiliary: 1f,
                MapId: mapId),
            source,
            receivedAt,
            kind);

    private static byte[] CreateLegacyWalk(
        uint movementState,
        float x,
        float z)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            movementState);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            1f);
        return packet;
    }

    private static async Task<object> ProcessTickAsync(
        GameClientHandler handler)
    {
        var invocation = ProcessTickMethod.Invoke(
            handler,
            [CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Realtime movement tick did not return a task.");
        await invocation;
        return invocation.GetType().GetProperty("Result")?
            .GetValue(invocation)
            ?? throw new InvalidOperationException(
                "Realtime movement tick returned no effects.");
    }

    private static async Task PublishEffectsAsync(
        GameClientHandler handler,
        object effects)
    {
        var invocation = PublishEffectsMethod.Invoke(
            handler,
            [effects, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Realtime movement publisher did not return a task.");
        await invocation;
    }

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Game packet handler did not return a task.");
        await invocation;
    }

    private static byte[]? GetEffectPacket(
        object effects,
        string name) =>
        effects.GetType().GetProperty(name)?
            .GetValue(effects) as byte[];

    private static void AssertNoMovementEffects(
        object effects,
        string description)
    {
        Check.True(
            GetEffectPacket(effects, "ViewerMovement") is null &&
            GetEffectPacket(
                effects,
                "ReliableCorrection") is null,
            $"{description} produces no legacy movement egress");
    }

    private static void AssertCanonicalWalk(
        byte[] packet,
        uint expectedObjectId,
        uint expectedHighState,
        float expectedX,
        float expectedZ,
        string description)
    {
        Check.Equal(
            20,
            packet.Length,
            $"{description} byte length");
        Check.Equal(
            (ushort)20,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            $"{description} declared length");
        Check.Equal(
            Opcodes.Walk,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2)),
            $"{description} opcode");
        var state = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(4));
        Check.Equal(
            expectedHighState & 0xFFFF_0000u,
            state & 0xFFFF_0000u,
            $"{description} opaque state");
        Check.Equal(
            expectedObjectId & 0xFFFFu,
            state & 0xFFFFu,
            $"{description} authoritative object ID");
        Check.Equal(
            expectedX,
            BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(8)),
            $"{description} X");
        Check.Equal(
            expectedZ,
            BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(12)),
            $"{description} Z");
        Check.Equal(
            1f,
            BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(16)),
            $"{description} auxiliary");
    }

    private static MethodInfo FindHandlerMethod(
        string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

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

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 26, 2, 3, 4, TimeSpan.Zero);

    private sealed class NoopMovementStore : GameStoreTestStub;
}
