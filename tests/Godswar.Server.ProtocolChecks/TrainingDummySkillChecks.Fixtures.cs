using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private const uint LocalPlayerObjectId = 0x1448;

    private static SkillCombatDefinition SpearHit() =>
        new(
            294,
            44,
            28,
            3f,
            0f,
            0,
            600,
            2.64m,
            3520m,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(25));

    private static byte[] TrainingSkillCastPacket(
        uint skillId,
        uint targetObjectId,
        uint casterObjectId = LocalPlayerObjectId)
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
            casterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            targetObjectId);
        return packet;
    }

    private static TrainingDummyPolicy Policy()
    {
        var options = new TrainingDummyOptions
        {
            Enabled = true,
            Identities =
            [
                new()
                {
                    CharacterId = 7001,
                    AccountId = 7001,
                    Name = "AresBulwark",
                    Camp = 1,
                    MapId = 0,
                    PositionX = 148f,
                    PositionZ = -154f
                },
                new()
                {
                    CharacterId = 7002,
                    AccountId = 7002,
                    Name = "AresMirage",
                    Camp = 1,
                    MapId = 0,
                    PositionX = 148f,
                    PositionZ = -162f
                }
            ]
        };
        options.Normalize();
        return TrainingDummyPolicy.Create(
            options,
            new ValidatedServerRuntimeProfile(
                ServerRuntimeProfileKind.LocalDevelopment,
                GameStorageProviderKind.Postgres,
                ServerListenerTransport.RawTcp,
                AllowsLegacyAuthentication: true));
    }

    private static GameSessionRegistry Registry() =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs,
            gameplayCatalogs: CapitalRuntimeCatalogs(),
            trainingDummies: Policy());

    private static GameplayRuntimeCatalogs CapitalRuntimeCatalogs()
    {
        var content = GameplayContentTestFixtures.Published with
        {
            Maps = GameplayContentTestFixtures.Published.Maps
                .Select(static map => map.MapId is 0 or 1
                    ? map with { MapMode = 5 }
                    : map)
                .ToArray()
        };
        return GameplayRuntimeCatalogs.Create(content);
    }

    private static GameCharacter Dummy(float positionX = 148f)
    {
        var result = Player(
            7001,
            7001,
            "AresBulwark",
            map: 0,
            camp: 1);
        result.PositionX = positionX;
        result.PositionZ = -154f;
        return result;
    }

    private static GameCharacter Player(
        int id,
        int accountId,
        string name,
        byte map,
        byte camp,
        byte profession = 1)
    {
        var result = new GameCharacter
        {
            Id = id,
            AccountId = accountId,
            Name = name,
            CurrentMap = map,
            Camp = camp,
            Profession = profession,
            Level = 160,
            PositionX = 148f,
            PositionZ = -154f,
            CurrentHp = 10_000_000,
            MaxHp = 10_000_000,
            CurrentMp = 10_000,
            MaxMp = 10_000
        };
        result.CalculatedStats = new CharacterStats
        {
            CharacterId = id,
            AccountId = accountId,
            Name = name,
            Profession = profession,
            Level = 160,
            CurrentHp = result.CurrentHp,
            MaxHp = result.MaxHp,
            CurrentMp = result.CurrentMp,
            MaxMp = result.MaxMp,
            PhysicalAttack = 1_000,
            PhysicalDefense = 100,
            Hit = 5_000,
            Dodge = 0,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            RuntimePolicySessionSocket attackerSocket,
            RuntimePolicySessionSocket targetSocket,
            GameSessionRegistry registry,
            GameCharacter attacker,
            GameCharacter target)
        {
            AttackerSocket = attackerSocket;
            TargetSocket = targetSocket;
            Registry = registry;
            Attacker = attacker;
            Target = target;
        }

        public RuntimePolicySessionSocket AttackerSocket { get; }
        public RuntimePolicySessionSocket TargetSocket { get; }
        public GameSessionRegistry Registry { get; }
        public GameCharacter Attacker { get; }
        public GameCharacter Target { get; }
        public uint AttackerObjectId =>
            Registry.GetRequiredPlayerObjectId(AttackerSocket.Session);
        public uint TargetObjectId =>
            Registry.GetRequiredPlayerObjectId(TargetSocket.Session);

        public static async Task<Fixture> CreateAsync(
            GameCharacter? attacker = null,
            GameCharacter? target = null,
            bool bindElementalOwnership = false)
        {
            var attackerSocket = await RuntimePolicySessionSocket.CreateAsync();
            try
            {
                var targetSocket = await RuntimePolicySessionSocket.CreateAsync();
                var registry = Registry();
                attacker ??= Player(8001, 8001, "Tester", 0, 0);
                target ??= Dummy();
                if (bindElementalOwnership)
                {
                    BindElementalOwnership(
                        registry,
                        attackerSocket.Session,
                        attacker);
                    BindElementalOwnership(
                        registry,
                        targetSocket.Session,
                        target);
                }
                registry.JoinPlayerMap(
                    attackerSocket.Session,
                    attacker.AccountId,
                    attacker);
                registry.JoinPlayerMap(
                    targetSocket.Session,
                    target.AccountId,
                    target);
                return new Fixture(
                    attackerSocket,
                    targetSocket,
                    registry,
                    attacker,
                    target);
            }
            catch
            {
                await attackerSocket.DisposeAsync();
                throw;
            }
        }

        public Task<TrainingDummySkillDecision> ResolveAsync(
            long revision,
            DateTimeOffset now,
            SkillCombatDefinition? skill = null,
            uint casterObjectId = LocalPlayerObjectId)
        {
            var definition = skill ?? SpearHit();
            var targetObjectId = TargetObjectId;
            return Registry.ResolveTrainingDummyDamageScalarAsync(
                AttackerSocket.Session,
                casterObjectId,
                targetObjectId,
                TrainingSkillCastPacket(
                    checked((uint)definition.SkillId),
                    targetObjectId,
                    casterObjectId),
                definition,
                () => revision,
                now,
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(AttackerSocket.Session);
            Registry.Remove(TargetSocket.Session);
            await AttackerSocket.DisposeAsync();
            await TargetSocket.DisposeAsync();
        }
    }
}
