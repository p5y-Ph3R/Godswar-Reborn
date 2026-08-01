using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private const int AccountId = 737;
    private const int CharacterId = 8_737;
    private const uint LocalObjectId = 0x1448;
    private const uint ThunderSkillId = 530;
    private const uint MonsterObjectId = 9_530;
    private const int InitialMana = 500;
    private const uint InitialMonsterHealth = 2_000;

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly MethodInfo StopPendingSkillCastsMethod =
        typeof(GameClientHandler).GetMethod(
            "StopPendingSkillCastsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.StopPendingSkillCastsAsync was not found.");

    public static async Task RunAsync()
    {
        Check.True(
            GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                checked((int)ThunderSkillId),
                out var combat),
            "Thunder 1 combat definition exists");
        Check.Equal(
            TimeSpan.FromSeconds(1),
            combat.CastTime,
            "Thunder 1 is an ordinary one-second intonation");

        await CheckNativeInterruptionAsync(combat);
        await CheckMovementInterruptionAsync(combat);
        await CheckTargetGenerationRefreshInterruptionAsync(combat);
        await CheckSuccessfulDelayedCompletionAsync(combat);
    }

    private static async Task CheckNativeInterruptionAsync(
        SkillCombatDefinition combat)
    {
        await using var fixture =
            await Fixture.CreateAsync("NativeThunderInterrupt");
        await fixture.BeginCastAsync();
        await fixture.AssertStartOnlyAsync(
            combat,
            "native-interrupted Thunder");

        await InvokePacketAsync(
            fixture.Handler,
            new GamePacket(
                Convert.FromHexString("0800BB2748140000")));
        var interrupted = await fixture.Socket.ReadPacketAsync(8);
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(interrupted),
            "native interruption returns the stock Skill09 frame");

        await fixture.AssertNeverCompletedAsync(
            combat,
            "native-interrupted Thunder");
    }

    private static async Task CheckMovementInterruptionAsync(
        SkillCombatDefinition combat)
    {
        await using var fixture =
            await Fixture.CreateAsync("MovementThunderInterrupt");
        await fixture.BeginCastAsync();
        await fixture.AssertStartOnlyAsync(
            combat,
            "movement-interrupted Thunder");

        await InvokePacketAsync(
            fixture.Handler,
            CreateControlPacket(Opcodes.WalkBegin));
        var interrupted = await fixture.Socket.ReadPacketAsync(8);
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(interrupted),
            "movement interruption returns the stock Skill09 frame");
        var movement = await fixture.Socket.ReadPacketAsync(4);
        Check.Equal(
            Opcodes.WalkBegin,
            ReadOpcode(movement),
            "movement remains accepted after interrupting Thunder");

        await fixture.AssertNeverCompletedAsync(
            combat,
            "movement-interrupted Thunder");
    }

    private static async Task CheckSuccessfulDelayedCompletionAsync(
        SkillCombatDefinition combat)
    {
        await using var fixture =
            await Fixture.CreateAsync("CompletedThunder");
        await fixture.BeginCastAsync();
        await fixture.AssertStartOnlyAsync(
            combat,
            "successful Thunder");

        var damage = await fixture.Socket.ReadPacketAsync(32);
        var impact = await fixture.Socket.ReadPacketAsync(24);
        var mana = await fixture.Socket.ReadPacketAsync(12);
        Check.Equal(
            (ushort)10045,
            ReadOpcode(damage),
            "completed Thunder publishes damage after intonation");
        Check.Equal(
            ThunderSkillId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                damage.AsSpan(20, 4)),
            "completed damage identifies Thunder");
        Check.Equal(
            (ushort)10046,
            ReadOpcode(impact),
            "completed Thunder publishes its impact");
        Check.Equal(
            ThunderSkillId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(12, 4)),
            "completed impact identifies Thunder");
        Check.Equal(
            (ushort)10135,
            ReadOpcode(mana),
            "completed Thunder publishes the authoritative mana");
        Check.Equal(
            InitialMana - combat.Mp,
            BinaryPrimitives.ReadInt32LittleEndian(
                mana.AsSpan(8, 4)),
            "completed Thunder mana packet applies its cost once");

        Check.Equal(
            InitialMana - combat.Mp,
            fixture.Character.CurrentMp,
            "completed Thunder consumes MP once");
        Check.True(
            fixture.CurrentMonsterHealth() <
                InitialMonsterHealth,
            "completed Thunder damages the authoritative monster");
        await fixture.Store.WaitForVitalsWriteAsync();
        Check.Equal(
            1,
            fixture.Store.VitalsWrites,
            "completed Thunder persists one vitals mutation");
        await Task.Delay(50);
        Check.Equal(
            0,
            fixture.Socket.Available,
            "completed Thunder emits no duplicate cast visual");
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

    private static GamePacket CreateSkillCastPacket(
        float casterX,
        float casterZ)
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
            LocalObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            ThunderSkillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            MonsterObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(24),
            casterX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            casterZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32),
            3f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36),
            0f);
        return new GamePacket(packet);
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

    private static CapturedMonsterSpawn CreateMonster()
    {
        const string templateKey = "A_normal_stub_001";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            MonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            InitialMonsterHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            InitialMonsterHealth);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            3f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: 0,
            SceneKey: "Sparta",
            templateKey,
            templateKey,
            MonsterObjectId,
            X: 3f,
            Z: 0f,
            packet);
    }

    private static ushort ReadOpcode(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.Slice(2, 2));

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

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            RuntimePolicySessionSocket socket,
            CombatStore store,
            GameSessionRegistry registry,
            GameCharacter character,
            GameClientHandler handler)
        {
            Socket = socket;
            Store = store;
            Registry = registry;
            Character = character;
            Handler = handler;
        }

        public RuntimePolicySessionSocket Socket { get; }

        public CombatStore Store { get; }

        public GameSessionRegistry Registry { get; }

        public GameCharacter Character { get; }

        public GameClientHandler Handler { get; }

        public static async Task<Fixture> CreateAsync(
            string characterName)
        {
            var socket =
                await RuntimePolicySessionSocket.CreateAsync();
            var character = new GameCharacter
            {
                Id = CharacterId,
                AccountId = AccountId,
                Name = characterName,
                CreatedUtc = DateTime.UtcNow,
                Camp = GameDefaults.SpartaCamp,
                CurrentMap = 0,
                PositionX = 0f,
                PositionZ = 0f,
                Profession = 3,
                Level = 80,
                CurrentHp = 500,
                MaxHp = 500,
                CurrentMp = InitialMana,
                MaxMp = InitialMana,
                CalculatedStats = new CharacterStats
                {
                    MagicAttack = 100
                }
            };
            var store = new CombatStore();
            var registry = new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                MonsterRuntimeMode.Ecs,
                PlayerRuntimeMode.Ecs);
            registry.InitializeMapMonsters(
                character.CurrentMap,
                [CreateMonster()],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                socket.Session,
                AccountId,
                character,
                WorldObjectIds.ForPlayer(CharacterId),
                worldReady: true);
            await using (var transition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    socket.Session,
                    character.CurrentMap,
                    character.PositionX,
                    character.PositionZ,
                    CancellationToken.None)
                ?? throw new InvalidOperationException(
                    "Thunder fixture visibility was unavailable."))
            {
                transition.Commit();
            }

            var handler = new GameClientHandler(
                socket.Session,
                store,
                registry,
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty);
            SetField(
                handler,
                "_account",
                new AccountIdentity(AccountId, "intoned-combat"));
            SetField(handler, "_character", character);
            SetField(handler, "_registered", true);
            SetField(
                handler,
                "_worldPresenceAnnounced",
                true);
            return new Fixture(
                socket,
                store,
                registry,
                character,
                handler);
        }

        public async Task BeginCastAsync()
        {
            await InvokePacketAsync(
                Handler,
                CreateSkillCastPacket(
                    Character.PositionX,
                    Character.PositionZ));
            var visual = await Socket.ReadPacketAsync(40);
            Check.Equal(
                Opcodes.SkillCast,
                ReadOpcode(visual),
                "Thunder publishes its cast visual immediately");
            Check.Equal(
                ThunderSkillId,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    visual.AsSpan(8, 4)),
                "Thunder start visual retains its skill ID");
        }

        public async Task AssertStartOnlyAsync(
            SkillCombatDefinition combat,
            string description)
        {
            await Task.Delay(150);
            Check.Equal(
                0,
                Socket.Available,
                $"{description} has no early effect packet");
            Check.Equal(
                InitialMana,
                Character.CurrentMp,
                $"{description} reserves no MP before completion");
            Check.Equal(
                InitialMonsterHealth,
                CurrentMonsterHealth(),
                $"{description} applies no early damage");
            Check.Equal(
                0,
                Store.VitalsWrites,
                $"{description} persists no early effect");
            Check.True(
                combat.CastTime > TimeSpan.FromMilliseconds(150),
                $"{description} fixture probes before cast completion");
        }

        public async Task AssertNeverCompletedAsync(
            SkillCombatDefinition combat,
            string description)
        {
            await Task.Delay(
                combat.CastTime + TimeSpan.FromMilliseconds(250));
            Check.Equal(
                0,
                Socket.Available,
                $"{description} emits no delayed effect packet");
            Check.Equal(
                InitialMana,
                Character.CurrentMp,
                $"{description} consumes no MP");
            Check.Equal(
                InitialMonsterHealth,
                CurrentMonsterHealth(),
                $"{description} applies no damage");
            Check.Equal(
                0,
                Store.VitalsWrites,
                $"{description} persists no vitals");
        }

        public uint CurrentMonsterHealth()
        {
            Check.True(
                Registry.TryGetMonsterSnapshot(
                    Character.CurrentMap,
                    MonsterObjectId,
                    out var monster),
                "Thunder target remains authoritative");
            return monster.CurrentHealth;
        }

        public async ValueTask DisposeAsync()
        {
            var stop = StopPendingSkillCastsMethod.Invoke(
                Handler,
                null) as Task
                ?? throw new InvalidOperationException(
                    "StopPendingSkillCastsAsync returned no task.");
            await stop;
            Registry.Remove(Socket.Session);
            await Socket.DisposeAsync();
        }
    }

    private sealed class CombatStore : GameStoreTestStub
    {
        private readonly TaskCompletionSource<bool> _vitalsWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int VitalsWrites { get; private set; }

        public Task WaitForVitalsWriteAsync() =>
            _vitalsWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillState>>(
                [new SkillState
                {
                    SkillId = checked((int)ThunderSkillId),
                    Level = 1
                }]);

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            Check.True(
                accountId == AccountId &&
                characterId == CharacterId,
                "Thunder persists the active character");
            VitalsWrites++;
            _vitalsWritten.TrySetResult(true);
            return Task.CompletedTask;
        }
    }
}
