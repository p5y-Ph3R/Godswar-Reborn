using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    private static void CheckAuthoredCombatV1()
    {
        var attacker = CreateAuthoredAttacker();
        var target = CreateAuthoredTarget();

        Check.Equal(9_114,
            AuthoredCombatV1.CalculateHitChanceBasisPoints(
                attacker,
                target),
            "authored hit rating resolves to basis points");
        Check.Equal(885,
            AuthoredCombatV1.CalculateCriticalChanceBasisPoints(
                attacker,
                target),
            "authored critical rating resolves against resistance");
        Check.Equal(150,
            AuthoredCombatV1.CalculateEffectiveDefense(200, 2_500),
            "ignore defense reduces only the selected defense channel");
        Check.Equal(200,
            AuthoredCombatV1.CalculateEffectiveDefense(1_000, 15_000),
            "ignore defense is capped at eighty percent");

        var skill = new PlayerCombatSkillSnapshot(
            SkillId: 9_001,
            Target: 44,
            AffectObject: 28,
            Distance: 5f,
            AreaRadius: 0f,
            ManaCost: 0,
            Property: 0,
            Power1: 0.5m,
            Power2: 100m);
        var critical = PlayerCombatRules.ResolveSkillDamage(
            attacker,
            target,
            skill,
            eventId: 1);
        var normal = PlayerCombatRules.ResolveSkillDamage(
            attacker,
            target,
            skill,
            eventId: 2);
        var miss = PlayerCombatRules.ResolveSkillDamage(
            attacker,
            target,
            skill,
            eventId: 4);

        Check.True(critical.Outcome == CombatHitOutcome.Critical,
            "event one deterministically resolves a critical");
        Check.Equal(8_485, critical.Rolls.HitRollBasisPoints,
            "critical result records its deterministic hit roll");
        Check.Equal(835, critical.Rolls.CriticalRollBasisPoints,
            "critical result records its deterministic critical roll");
        Check.Equal(2_418u, critical.Damage,
            "critical applies bonus-only reduction before append and mitigation");
        Check.Equal(1_020m, critical.Evidence.CriticalBonusDamage,
            "critical percent and flat reduction affect only critical bonus");

        Check.True(normal.Outcome == CombatHitOutcome.Normal,
            "event two deterministically resolves a normal hit");
        Check.Equal(5_990, normal.Rolls.HitRollBasisPoints,
            "normal result records its deterministic hit roll");
        Check.Equal(4_071, normal.Rolls.CriticalRollBasisPoints,
            "normal result records its failed critical roll");
        Check.Equal(1_500u, normal.Damage,
            "normal physical formula applies defense bonus append reduction and absorption");

        Check.True(miss.Outcome == CombatHitOutcome.Miss,
            "event four deterministically resolves a miss");
        Check.Equal(9_404, miss.Rolls.HitRollBasisPoints,
            "miss records the rejected hit roll");
        Check.Equal(CombatRollEvidence.NotRolled,
            miss.Rolls.CriticalRollBasisPoints,
            "miss never consumes the critical stage");
        Check.Equal(0u, miss.Damage,
            "miss carries zero authoritative damage");

        Check.Equal(5_932,
            DeterministicCombatRandom.RollBasisPoints(
                42,
                0,
                CombatRandomStage.Hit),
            "event roll is pinned for replay");
        Check.Equal(3_575,
            DeterministicCombatRandom.RollBasisPoints(
                42,
                1,
                CombatRandomStage.Hit),
            "area target order owns an independent roll");
        Check.Equal(195,
            DeterministicCombatRandom.RollBasisPoints(
                42,
                0,
                CombatRandomStage.Critical),
            "critical stage has a separate deterministic salt");

        CheckMagicChannelAndLegacyParity(attacker, target);
        CheckTypedAbsorptionAdapter();
    }

    private static void CheckMagicChannelAndLegacyParity(
        in CombatAttackerStats attacker,
        in CombatTargetStats target)
    {
        var snapshot = new PlayerCombatSkillSnapshot(
            SkillId: 9_002,
            Target: 44,
            AffectObject: 28,
            Distance: 5f,
            AreaRadius: 0f,
            ManaCost: 0,
            Property: 1,
            Power1: 0.25m,
            Power2: 20m);
        var ecs = PlayerCombatRules.ResolveSkillDamage(
            attacker,
            target,
            snapshot,
            eventId: 2);
        Check.True(ecs.Channel == CombatDamageChannel.Magic,
            "magic skill selects magic attack defense and mitigation");
        Check.Equal(1_057u, ecs.Damage,
            "magic formula uses magic ignore bonus append reduction and absorption");

        var character = CreateAuthoredCharacter(attacker);
        var legacySkill = ToLegacy(snapshot);
        var legacy = SkillCombatResolver.ResolveDamage(
            character,
            legacySkill,
            target,
            combatEventId: 2);
        Check.Equal(ecs, legacy,
            "legacy and ECS skill hooks return identical replay evidence");

        var ecsBasic = PlayerCombatRules.ResolveBasicAttack(
            attacker,
            target,
            eventId: 2);
        var legacyBasic = MonsterCombatResolver.ResolvePlayerBasicAttack(
            character,
            target,
            combatEventId: 2);
        Check.Equal(933u, ecsBasic.Damage,
            "basic attack uses physical damage bonus and append");
        Check.Equal(ecsBasic, legacyBasic,
            "legacy and ECS basic hooks return identical replay evidence");
    }

    private static void CheckTypedAbsorptionAdapter()
    {
        var target = CombatCharacterStatsAdapter.ToTarget(
            level: 10,
            new CharacterStats
            {
                DamageAbsorb = 42,
                PhysicalFlatAbsorption = 11,
                MagicFlatAbsorption = 22
            });
        Check.Equal(11, target.PhysicalFlatAbsorption,
            "physical combat consumes only the projected typed flat");
        Check.Equal(22, target.MagicFlatAbsorption,
            "magic combat consumes only the projected typed flat");
    }

    private static CombatAttackerStats CreateAuthoredAttacker() =>
        new()
        {
            Level = 100,
            Profession = 0,
            PhysicalAttack = 1_000,
            MagicAttack = 1_200,
            Hit = 500,
            Critical = 600,
            PhysicalDamageBonusBasisPoints = 2_000,
            MagicDamageBonusBasisPoints = 1_000,
            PhysicalAppendDamage = 50,
            MagicAppendDamage = 70,
            IgnorePhysicalDefenseBasisPoints = 2_500,
            IgnoreMagicDefenseBasisPoints = 1_000,
            CriticalDamageBasisPoints = 2_500,
            CriticalDamageFlat = 100,
            LifeAbsorptionBasisPoints = 500
        };

    private static CombatTargetStats CreateAuthoredTarget() =>
        new()
        {
            Level = 100,
            PhysicalDefense = 200,
            MagicDefense = 300,
            Dodge = 400,
            CriticalResistance = 300,
            PhysicalDamageReductionBasisPoints = 1_000,
            MagicDamageReductionBasisPoints = 2_000,
            CriticalDamageReductionBasisPoints = 2_000,
            PhysicalFlatAbsorption = 30,
            MagicFlatAbsorption = 40,
            CriticalDamageFlatReduction = 50,
            DamageReboundBasisPoints = 1_000,
            DamageReboundFlat = 20
        };

    private static GameCharacter CreateAuthoredCharacter(
        in CombatAttackerStats attacker) =>
        new()
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
                PhysicalAppendDamage = attacker.PhysicalAppendDamage,
                MagicAppendDamage = attacker.MagicAppendDamage,
                IgnorePhysicalDefense =
                    attacker.IgnorePhysicalDefenseBasisPoints,
                IgnoreMagicDefense =
                    attacker.IgnoreMagicDefenseBasisPoints,
                CriticalDamagePercent =
                    attacker.CriticalDamageBasisPoints,
                CriticalDamageFlat = attacker.CriticalDamageFlat,
                LifeAbsorption = attacker.LifeAbsorptionBasisPoints
            }
        };
}
