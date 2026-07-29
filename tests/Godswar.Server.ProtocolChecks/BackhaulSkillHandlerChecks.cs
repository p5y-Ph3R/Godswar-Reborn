using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private const int AccountId = 413;
    private const int CharacterId = 4_013;
    private const uint LocalPlayerObjectId = 0x00001448;
    private const byte PeloponneseMapId = 13;

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 27, 9, 10, 11, TimeSpan.Zero);

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

    private static readonly MethodInfo StopPendingSkillCastsMethod =
        typeof(GameClientHandler).GetMethod(
            "StopPendingSkillCastsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.StopPendingSkillCastsAsync was not found.");

    public static async Task RunAsync()
    {
        await CheckSuccessfulCastAsync(
            BackhaulSkillCatalog.CitySkillId);
        await CheckSuccessfulCastAsync(
            BackhaulSkillCatalog.SuburbSkillId);
        await CheckUnlearnedCastRejectedAsync();
        await CheckNativeCastInterruptionAsync();
        await CheckMovementCastInterruptionAsync();
        await CheckBasicAttackCastInterruptionAsync();
        await CheckInterruptionBroadcastIdentityAsync();
        await CheckControlStatusCastInterruptionsAsync();
    }

    private static async Task CheckSuccessfulCastAsync(uint skillId)
    {
        Check.True(
            BackhaulSkillCatalog.TryGet(skillId, out var definition),
            $"backhaul handler fixture resolves skill {skillId}");

        await using var socket = await BackhaulSessionSocket.CreateAsync();
        var character = CreateCharacter(
            $"Backhaul{skillId}");
        var store = new BackhaulStore(
            character,
            [new SkillState
            {
                SkillId = checked((int)skillId),
                Level = 1
            }]);
        var registry = CreateRegistry();
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(CharacterId),
            worldReady: true,
            joinedAt: TestTime);
        var handler = CreateEnteredHandler(
            socket.Session,
            store,
            registry,
            character);

        const float forgedTargetX = 8_765f;
        const float forgedTargetZ = -7_654f;
        await InvokePacketAsync(
            handler,
            CreateSkillCastPacket(
                skillId,
                character.PositionX,
                character.PositionZ,
                forgedTargetX,
                forgedTargetZ));

        var packets = await ReadThroughSceneChangeAsync(socket);
        var sceneChange = packets.Single(packet =>
            ReadUInt16(packet, 2) == Opcodes.SceneChange);
        Check.Equal(
            definition.TargetMapId,
            checked((byte)ReadUInt16(sceneChange, 22)),
            $"backhaul skill {skillId} scene destination map");
        Check.Equal(
            definition.TargetX,
            ReadSingle(sceneChange, 8),
            $"backhaul skill {skillId} ignores forged target X");
        Check.Equal(
            definition.TargetZ,
            ReadSingle(sceneChange, 16),
            $"backhaul skill {skillId} ignores forged target Z");
        Check.True(
            ReadSingle(sceneChange, 8) != forgedTargetX &&
            ReadSingle(sceneChange, 16) != forgedTargetZ,
            $"backhaul skill {skillId} does not echo client coordinates");

        var visual = packets.Single(packet =>
            ReadUInt16(packet, 2) == Opcodes.SkillCast);
        Check.Equal(
            skillId,
            ReadUInt32(visual, 8),
            $"backhaul skill {skillId} publishes its native cast visual");
        var mana = packets.Single(packet =>
            ReadUInt16(packet, 2) == 0x2797);
        Check.Equal(
            100,
            ReadInt32(mana, 8),
            $"backhaul skill {skillId} publishes its deducted MP");

        Check.Equal(
            100,
            character.CurrentMp,
            $"backhaul skill {skillId} deducts exactly 50 MP");
        Check.Equal(
            1,
            store.VitalsWrites.Count,
            $"backhaul skill {skillId} persists MP exactly once");
        Check.Equal(
            100,
            store.VitalsWrites[0].CurrentMp,
            $"backhaul skill {skillId} persists deducted MP");
        Check.Equal(
            1,
            store.PositionWrites.Count,
            $"backhaul skill {skillId} persists destination exactly once");
        var position = store.PositionWrites[0];
        Check.Equal(
            definition.TargetMapId,
            position.MapId,
            $"backhaul skill {skillId} persists destination map");
        Check.Equal(
            definition.TargetX,
            position.X,
            $"backhaul skill {skillId} persists catalog X");
        Check.Equal(
            definition.TargetZ,
            position.Z,
            $"backhaul skill {skillId} persists catalog Z");

        AssertHiddenDestination(
            registry,
            socket.Session,
            character,
            definition,
            $"backhaul skill {skillId}");

        await StopHandlerAsync(handler);
        registry.Remove(socket.Session);
    }

    private static async Task CheckUnlearnedCastRejectedAsync()
    {
        await using var socket = await BackhaulSessionSocket.CreateAsync();
        var character = CreateCharacter("UnlearnedBackhaul");
        var store = new BackhaulStore(character, []);
        var registry = CreateRegistry();
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(CharacterId),
            worldReady: true,
            joinedAt: TestTime);
        var handler = CreateEnteredHandler(
            socket.Session,
            store,
            registry,
            character);

        await InvokePacketAsync(
            handler,
            CreateSkillCastPacket(
                BackhaulSkillCatalog.CitySkillId,
                character.PositionX,
                character.PositionZ,
                targetX: 165f,
                targetZ: -97f));

        Check.Equal(
            0,
            socket.Available,
            "unlearned backhaul emits no cast or transition packet");
        Check.Equal(
            150,
            character.CurrentMp,
            "unlearned backhaul consumes no MP");
        Check.Equal(
            0,
            store.VitalsWrites.Count,
            "unlearned backhaul persists no vitals");
        Check.Equal(
            0,
            store.PositionWrites.Count,
            "unlearned backhaul persists no destination");
        Check.Equal(
            PeloponneseMapId,
            character.CurrentMap,
            "unlearned backhaul remains on its source map");
        Check.True(
            registry.GetMapSessions(PeloponneseMapId)
                .Any(context =>
                    ReferenceEquals(context.Session, socket.Session) &&
                    context.WorldReady),
            "unlearned backhaul preserves active source membership");

        await StopHandlerAsync(handler);
        registry.Remove(socket.Session);
    }

    private static GameClientHandler CreateEnteredHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        GameCharacter character,
        TimeSpan? backhaulSkillCastTime = null)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            WorldContentReaderTestFixtures.Empty,
            mapTransitionReadyTimeout: TimeSpan.FromSeconds(5),
            backhaulSkillCastTime:
                backhaulSkillCastTime ?? TimeSpan.Zero);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = character.AccountId,
                Username = "backhaul-handler"
            });
        SetField(handler, "_character", character);
        SetField(handler, "_registered", true);
        SetField(handler, "_worldPresenceAnnounced", true);
        SetField(handler, "_clientReadyReceived", true);
        SetField(handler, "_playerDetailSent", true);
        SetField(handler, "_enterUiReadyReceived", true);
        return handler;
    }

    private static GameSessionRegistry CreateRegistry() =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);

    private static GameCharacter CreateCharacter(string name) =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = name,
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = PeloponneseMapId,
            PositionX = -57f,
            PositionZ = 34f,
            Level = 80,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 150,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static GamePacket CreateSkillCastPacket(
        uint skillId,
        float casterX,
        float casterZ,
        float targetX,
        float targetZ)
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            LocalPlayerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(24),
            casterX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            casterZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32),
            targetX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36),
            targetZ);
        return new GamePacket(packet);
    }

    private static async Task<IReadOnlyList<byte[]>>
        ReadThroughSceneChangeAsync(BackhaulSessionSocket socket)
    {
        var packets = new List<byte[]>();
        for (var index = 0; index < 8; index++)
        {
            var packet = await socket.ReadPacketAsync();
            packets.Add(packet);
            if (ReadUInt16(packet, 2) == Opcodes.SceneChange)
            {
                return packets;
            }
        }

        throw new InvalidOperationException(
            "Backhaul cast did not emit a scene-change packet.");
    }

    private static void AssertHiddenDestination(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        BackhaulSkillDefinition definition,
        string description)
    {
        Check.Equal(
            definition.TargetMapId,
            character.CurrentMap,
            $"{description} updates authoritative map");
        Check.True(
            !registry.GetMapSessions(PeloponneseMapId)
                .Any(context =>
                    ReferenceEquals(context.Session, session)),
            $"{description} removes source membership");
        Check.Equal(
            1,
            registry.GetMapPopulation(definition.TargetMapId),
            $"{description} destination ECS owns hidden player");
        Check.True(
            !registry.GetMapSessions(definition.TargetMapId)
                .Any(context =>
                    ReferenceEquals(context.Session, session)),
            $"{description} hides destination pending client readiness");
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
        var stopCastsTask = StopPendingSkillCastsMethod.Invoke(
            handler,
            null) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.StopPendingSkillCastsAsync returned no task.");
        await stopCastsTask;

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

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(offset, sizeof(ushort)));

    private static uint ReadUInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(offset, sizeof(uint)));

    private static int ReadInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.AsSpan(offset, sizeof(int)));

    private static float ReadSingle(byte[] packet, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(
            packet.AsSpan(offset, sizeof(float)));

    private readonly record struct PositionWrite(
        byte MapId,
        float X,
        float Z);

    private readonly record struct VitalsWrite(
        int CurrentHp,
        int CurrentMp,
        long Revision);

    private sealed class BackhaulStore : GameStoreTestStub
    {
        private readonly GameCharacter _character;
        private readonly IReadOnlyList<SkillState> _skills;

        public BackhaulStore(
            GameCharacter character,
            IReadOnlyList<SkillState> skills)
        {
            _character = character;
            _skills = skills;
        }

        public List<PositionWrite> PositionWrites { get; } = [];

        public List<VitalsWrite> VitalsWrites { get; } = [];

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            Check.True(
                accountId == _character.AccountId &&
                characterId == _character.Id,
                "backhaul persists the active identity");
            PositionWrites.Add(new PositionWrite(
                currentMap,
                positionX,
                positionZ));
            return Task.CompletedTask;
        }

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            Check.True(
                accountId == _character.AccountId &&
                characterId == _character.Id,
                "backhaul vitals persist the active identity");
            VitalsWrites.Add(new VitalsWrite(
                currentHp,
                currentMp,
                vitalsRevision));
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(_skills);
    }

    private sealed class BackhaulSessionSocket : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _inbound;
        private readonly PacketCipher _receiveCipher = new();

        private BackhaulSessionSocket(
            TcpListener listener,
            TcpClient inbound,
            ClientSession session)
        {
            _listener = listener;
            _inbound = inbound;
            Session = session;
        }

        public ClientSession Session { get; }

        public int Available => _inbound.Available;

        public static async Task<BackhaulSessionSocket> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, port);
            var inbound = await accepted;
            return new BackhaulSessionSocket(
                listener,
                inbound,
                new ClientSession(
                    new RawTcpLegacyTransport(outbound)));
        }

        public async Task<byte[]> ReadPacketAsync()
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
            var lengthBytes = new byte[sizeof(ushort)];
            await _inbound.GetStream().ReadExactlyAsync(
                lengthBytes,
                timeout.Token);
            _receiveCipher.Transform(lengthBytes);
            var length =
                BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            Check.True(
                length >= 4 && length <= ushort.MaxValue,
                "backhaul packet has a bounded declared length");

            var packet = new byte[length];
            lengthBytes.CopyTo(packet, 0);
            await _inbound.GetStream().ReadExactlyAsync(
                packet.AsMemory(sizeof(ushort)),
                timeout.Token);
            _receiveCipher.Transform(packet.AsSpan(sizeof(ushort)));
            return packet;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _inbound.Dispose();
            _listener.Stop();
        }
    }
}
