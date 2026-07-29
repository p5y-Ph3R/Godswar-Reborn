using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerMovementEcsLiveAdapterChecks
{
    private const int AccountId = 101;
    private const int CharacterId = 1_031;
    private const int ViewerAccountId = 102;
    private const int ViewerCharacterId = 1_032;
    private const uint MonsterObjectId = 11_001;

    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 23, 4, 5, 6, TimeSpan.Zero);

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly MethodInfo ResetMovementMethod =
        typeof(GameClientHandler).GetMethod(
            "ResetPlayerMovementEcs",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.ResetPlayerMovementEcs was not found.");

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

    private static void ResetMovementAdapter(
        GameClientHandler handler)
    {
        ResetMovementMethod.Invoke(handler, null);
    }

    private static GameClientHandler CreateHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        GameCharacter character,
        bool configureVisibility = true)
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
                Username = $"movement-{character.AccountId}"
            });
        SetField(handler, "_character", character);
        SetField(handler, "_registered", true);
        if (configureVisibility)
        {
            SetField(
                handler,
                "_npcVisibility",
                new WorldSectorVisibilityTracker<
                    NpcSpawnDefinition>(
                    [],
                    static npc => npc.ObjectId,
                    static npc => npc.X,
                    static npc => npc.Z,
                    "NPC"));
        }

        return handler;
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

    private static GamePacket CreateWalkPacket(
        uint opaqueMovementState,
        float targetX,
        float targetZ)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            opaqueMovementState);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8, 4),
            targetX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12, 4),
            targetZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16, 4),
            1f);
        return new GamePacket(packet);
    }

    private static void AssertWalkBroadcast(
        byte[] broadcast,
        GamePacket source,
        uint worldObjectId,
        float expectedX,
        float expectedZ,
        string description)
    {
        Check.Equal(
            Opcodes.Walk,
            BinaryPrimitives.ReadUInt16LittleEndian(
                broadcast.AsSpan(2, 2)),
            $"{description} opcode");
        var sourceState =
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.Buffer.AsSpan(4, 4));
        var broadcastState =
            BinaryPrimitives.ReadUInt32LittleEndian(
                broadcast.AsSpan(4, 4));
        Check.Equal(
            sourceState & 0xFFFF_0000u,
            broadcastState & 0xFFFF_0000u,
            $"{description} preserves opaque movement state");
        Check.Equal(
            worldObjectId & 0xFFFFu,
            broadcastState & 0xFFFFu,
            $"{description} projects server world object ID");
        Check.Equal(
            expectedX,
            BinaryPrimitives.ReadSingleLittleEndian(
                broadcast.AsSpan(8, 4)),
            $"{description} X");
        Check.Equal(
            expectedZ,
            BinaryPrimitives.ReadSingleLittleEndian(
                broadcast.AsSpan(12, 4)),
            $"{description} Z");
    }

    private static GameSessionRegistry CreateRegistry(
        PlayerRuntimeMode mode) =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            mode);

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
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500
        };

    private static CapturedMonsterSpawn CreateMonster()
    {
        const string templateKey = "A_normal_stub_001";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            MonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            1f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: 0,
            SceneKey: "Sparta",
            templateKey,
            templateKey,
            MonsterObjectId,
            X: 1f,
            Z: 0f,
            packet);
    }

    private sealed class BlockingPositionStore :
        GameStoreTestStub
    {
        private readonly TaskCompletionSource<bool>
            _releaseFirstSave = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstSaveStarted
            { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveAttempts { get; private set; }

        public float SavedX { get; private set; }

        public float SavedZ { get; private set; }

        public void ReleaseFirstSave() =>
            _releaseFirstSave.TrySetResult(true);

        public override async Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            SavedX = positionX;
            SavedZ = positionZ;
            if (SaveAttempts != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult(true);
            await _releaseFirstSave.Task.WaitAsync(
                cancellationToken);
        }
    }

    private sealed class RecordingPositionStore :
        GameStoreTestStub
    {
        public int SaveAttempts { get; private set; }

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return Task.CompletedTask;
        }
    }
}
