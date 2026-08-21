using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class TrainingDummyHostileStatusTestFixture
{
    public const uint LocalPlayerObjectId = 0x1448;

    public static GameSessionRegistry CreateRegistry(
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs)
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
        var policy = TrainingDummyPolicy.Create(
            options,
            new ValidatedServerRuntimeProfile(
                ServerRuntimeProfileKind.LocalDevelopment,
                GameStorageProviderKind.Postgres,
                ServerListenerTransport.RawTcp,
                AllowsLegacyAuthentication: true));
        var published = GameplayContentTestFixtures.Published with
        {
            Maps = GameplayContentTestFixtures.Published.Maps
                .Select(static map => map.MapId is 0 or 1
                    ? map with { MapMode = 5 }
                    : map)
                .ToArray()
        };
        return new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode,
            gameplayCatalogs: GameplayRuntimeCatalogs.Create(published),
            trainingDummies: policy);
    }

    public static GameCharacter CreateAttacker(
        byte profession,
        int id = 8_801,
        string name = "StatusTester") =>
        CreateCharacter(
            id,
            id,
            name,
            profession,
            camp: 0,
            positionX: 148f,
            positionZ: -158f,
            hit: 100_000,
            dodge: 0);

    public static GameCharacter CreateDummy(
        int id = 7001,
        string name = "AresBulwark",
        float positionZ = -154f) =>
        CreateCharacter(
            id,
            id,
            name,
            profession: 1,
            camp: 1,
            positionX: 148f,
            positionZ,
            hit: 0,
            dodge: 0);

    public static async Task<TrainingDummyHostileStatusCastDecision>
        ApplyAsync(
            GameSessionRegistry registry,
            ClientSession attackerSession,
            GameCharacter attacker,
            ClientSession targetSession,
            GameCharacter target,
            int skillId,
            DateTimeOffset now,
            bool shouldApply = true)
    {
        Check.True(
            TrainingDummyHostileStatusSkillCatalog.TryGet(
                skillId,
                out var definition),
            $"hostile status skill {skillId} has catalog data");
        Check.True(
            GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                skillId,
                out var skill),
            $"hostile status skill {skillId} has combat data");
        var revision = FindRevision(
            attacker,
            target,
            definition,
            shouldApply);
        return await registry.ResolveTrainingDummyHostileStatusCastAsync(
            attackerSession,
            LocalPlayerObjectId,
            registry.GetRequiredPlayerObjectId(targetSession),
            skill,
            definition,
            () => revision,
            now,
            CancellationToken.None);
    }

    public static long FindRevision(
        GameCharacter attacker,
        GameCharacter target,
        in HostileStatusEffectDefinition definition,
        bool shouldApply)
    {
        var attackerStats = attacker.CalculatedStats ??
            CharacterStats.FromCharacter(attacker);
        var targetStats = target.CalculatedStats ??
            CharacterStats.FromCharacter(target);
        for (var revision = 1L; revision <= 10_000; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerSkill(
                attacker.Id,
                target.Id,
                attacker.VitalsRevision,
                target.VitalsRevision,
                revision,
                checked((uint)definition.SkillId));
            var proc = HostileStatusProcPolicy.Evaluate(
                new HostileStatusProcRatings(
                    attacker.Level,
                    target.Level,
                    attackerStats.Hit,
                    targetStats.Dodge,
                    attackerStats.StatusHit,
                    targetStats.StatusResistance),
                eventId,
                targetOrder: 0);
            if (proc.Applied == shouldApply)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            $"No deterministic status proc with applied={shouldApply} " +
            $"was found for skill {definition.SkillId}.");
    }

    private static GameCharacter CreateCharacter(
        int id,
        int accountId,
        string name,
        byte profession,
        byte camp,
        float positionX,
        float positionZ,
        int hit,
        int dodge)
    {
        var character = new GameCharacter
        {
            Id = id,
            AccountId = accountId,
            Name = name,
            CreatedUtc = DateTime.UtcNow,
            Camp = camp,
            CurrentMap = 0,
            PositionX = positionX,
            PositionZ = positionZ,
            Profession = profession,
            Level = 160,
            CurrentHp = 1_000_000,
            MaxHp = 1_000_000,
            CurrentMp = 10_000,
            MaxMp = 10_000
        };
        character.CalculatedStats = new CharacterStats
        {
            CharacterId = id,
            AccountId = accountId,
            Name = name,
            Profession = profession,
            Level = character.Level,
            CurrentHp = character.CurrentHp,
            MaxHp = character.MaxHp,
            CurrentMp = character.CurrentMp,
            MaxMp = character.MaxMp,
            PhysicalAttack = 1_000,
            MagicAttack = 1_000,
            PhysicalDefense = 1_000,
            MagicDefense = 1_000,
            Hit = hit,
            Dodge = dodge,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return character;
    }
}
