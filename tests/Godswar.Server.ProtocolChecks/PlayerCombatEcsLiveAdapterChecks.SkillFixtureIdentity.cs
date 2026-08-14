using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsLiveAdapterChecks
{
    private static void StabilizeLiveSkillFixtureCharacterId(
        GameSessionRegistry registry,
        GameCharacter character,
        IReadOnlyList<CapturedMonsterSpawn> monsters)
    {
        var normal = new SkillCombatDefinition(
            2_001,
            Target: 44,
            AffectObj: 28,
            Distance: 5f,
            Range: 0f,
            Property: 0,
            Mp: 25,
            Power1: 0m,
            Power2: 0m);
        var area = new SkillCombatDefinition(
            3_001,
            Target: 1,
            AffectObj: 8,
            Distance: 0f,
            Range: 10f,
            Property: 0,
            Mp: 30,
            Power1: 0m,
            Power2: 0m);
        var zeroDamage = normal with
        {
            SkillId = 2_002,
            Mp = 10,
            Power1 = -1m
        };
        var lethal = normal with
        {
            SkillId = 2_010,
            Mp = 5,
            Power2 = 10_000m
        };
        var attacker = CombatCharacterStatsAdapter.FromCharacter(character);
        for (var characterId = 8_000;
             characterId < 30_000;
             characterId++)
        {
            if (ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[0],
                    healthRevision: 0,
                    admittedRevision: 1,
                    skill: null,
                    targetOrder: 0,
                    attacker) &&
                ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[1],
                    healthRevision: 0,
                    admittedRevision: 2,
                    normal,
                    targetOrder: 0,
                    attacker) &&
                ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[1],
                    healthRevision: 1,
                    admittedRevision: 3,
                    zeroDamage,
                    targetOrder: 0,
                    attacker) &&
                ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[0],
                    healthRevision: 1,
                    admittedRevision: 4,
                    area,
                    targetOrder: 0,
                    attacker) &&
                ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[1],
                    healthRevision: 1,
                    admittedRevision: 4,
                    area,
                    targetOrder: 1,
                    attacker) &&
                ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[2],
                    healthRevision: 0,
                    admittedRevision: 4,
                    area,
                    targetOrder: 2,
                    attacker) &&
                ResolvesHit(
                    characterId,
                    registry,
                    character.CurrentMap,
                    monsters[2],
                    healthRevision: 1,
                    admittedRevision: 5,
                    lethal,
                    targetOrder: 0,
                    attacker))
            {
                character.Id = characterId;
                return;
            }
        }

        throw new InvalidOperationException(
            "No deterministic live skill fixture identity was found.");
    }

    private static bool ResolvesHit(
        int characterId,
        GameSessionRegistry registry,
        byte mapId,
        CapturedMonsterSpawn monster,
        ulong healthRevision,
        ulong admittedRevision,
        SkillCombatDefinition? skill,
        int targetOrder,
        in CombatAttackerStats attacker)
    {
        Check.True(
            registry.TryGetMonsterSnapshot(
                mapId,
                monster.ObjectId,
                out var snapshot),
            "deterministic skill fixture target exists");
        var eventId = skill is null
            ? CombatEventIdentity.ForPlayerMonsterBasicAttack(
                characterId,
                monster.ObjectId,
                snapshot.SpawnGeneration,
                healthRevision,
                admittedRevision)
            : CombatEventIdentity.ForPlayerMonsterSkill(
                characterId,
                monster.ObjectId,
                snapshot.SpawnGeneration,
                healthRevision,
                admittedRevision,
                (uint)skill.Value.SkillId,
                targetOrder);
        var target = MonsterCombatProfileCatalog.Empty
            .Resolve(monster)
            .ToTargetStats();
        var resolution = skill is null
            ? PlayerCombatRules.ResolveBasicAttack(
                attacker,
                target,
                eventId,
                targetOrder)
            : SkillCombatResolver.ResolveDamage(
                new GameCharacter
                {
                    Profession = attacker.Profession,
                    Level = attacker.Level,
                    CalculatedStats = new CharacterStats
                    {
                        PhysicalAttack = attacker.PhysicalAttack,
                        MagicAttack = attacker.MagicAttack,
                        Hit = attacker.Hit,
                        Critical = attacker.Critical,
                        PhysicalDamageBonus =
                            attacker.PhysicalDamageBonusBasisPoints,
                        MagicDamageBonus =
                            attacker.MagicDamageBonusBasisPoints,
                        PhysicalAppendDamage =
                            attacker.PhysicalAppendDamage,
                        MagicAppendDamage = attacker.MagicAppendDamage
                    }
                },
                skill.Value,
                target,
                eventId,
                targetOrder);
        return resolution.Hit;
    }
}
