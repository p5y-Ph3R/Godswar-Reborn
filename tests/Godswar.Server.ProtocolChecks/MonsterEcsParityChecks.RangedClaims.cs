using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterEcsParityChecks
{
    private static void CheckRangedReachAndFirstHitClaimParity()
    {
        CheckRangedAttackReachParity();
        CheckAuthoredMeleeCollisionReachParity();
        CheckFirstHitClaimParity();
        CheckFirstHitClaimPresentation();
    }

    private static void CheckFirstHitClaimPresentation()
    {
        var owner = PacketBuilder.MonsterClaimState(0x262);

        Check.Equal(
            12,
            owner.Length,
            "the owner receives the captured first-hit claim frame");
        Check.True(
            owner.SequenceEqual(Convert.FromHexString(
                "0C0052286202000001FFFFFF")),
            "the claim frame matches the external first-hit packet");
    }

    private static void CheckRangedAttackReachParity()
    {
        var magicalTemplate = GameplayContentTestFixtures.Published.MonsterTemplates
            .First(value =>
                value.AttackType == 2 &&
                value.SourceMapId is >= byte.MinValue and <= byte.MaxValue);
        AssertRangedAttackReach(
            11004,
            magicalTemplate,
            "magical ranged monster");

        var physicalArcher = GameplayContentTestFixtures.Published
            .MonsterTemplates
            .First(value =>
                value.AttackType == 1 &&
                value.DisplayName.Contains(
                    "Archer",
                    StringComparison.OrdinalIgnoreCase) &&
                value.SourceMapId is >= byte.MinValue and <= byte.MaxValue);
        AssertRangedAttackReach(
            11007,
            physicalArcher,
            "physical archer");
    }

    private static void AssertRangedAttackReach(
        uint objectId,
        GameplayMonsterTemplateDefinition template,
        string description)
    {
        var mapId = checked((byte)template.SourceMapId!.Value);
        var monster = CreateMonster(
            objectId,
            100f,
            50f,
            tier: 30,
            mapId: mapId,
            templateKey: template.TemplateKey,
            displayName: template.DisplayName);
        var profiles =
            GameplayContentTestFixtures.Runtime.MonsterCombatProfiles;
        var legacy = new MonsterMapRuntime(
            mapId,
            [monster],
            Start,
            monsterCombatProfiles: profiles);
        var ecs = new EcsMonsterMapRuntime(
            mapId,
            [monster],
            Start,
            monsterCombatProfiles: profiles);
        var target = Target(821, monster.X + 8f, monster.Z);

        AssertTickEqual(
            legacy.Advance(Start, [target]),
            ecs.Advance(Start, [target]),
            $"{description} target acquisition");
        var attack = AdvancePair(
            legacy,
            ecs,
            Start + MonsterMapRuntime.TickInterval,
            [target],
            $"{description} attack reach");
        AssertAttackTarget(
            attack,
            target.CharacterId,
            $"a {description} attacks without walking into melee reach");
    }

    private static void CheckAuthoredMeleeCollisionReachParity()
    {
        var template = GameplayContentTestFixtures.Published.MonsterTemplates
            .First(value =>
                value.SourceMapId == 204 &&
                value.AttackType == 1 &&
                value.CollisionRange > MonsterAttackRangePolicy.MeleeRange &&
                !value.DisplayName.Contains(
                    "Archer",
                    StringComparison.OrdinalIgnoreCase));
        var monster = CreateMonster(
            11008,
            100f,
            50f,
            tier: 30,
            mapId: 204,
            templateKey: template.TemplateKey,
            displayName: template.DisplayName);
        var profiles =
            GameplayContentTestFixtures.Runtime.MonsterCombatProfiles;
        var legacy = new MonsterMapRuntime(
            204,
            [monster],
            Start,
            monsterCombatProfiles: profiles);
        var ecs = new EcsMonsterMapRuntime(
            204,
            [monster],
            Start,
            monsterCombatProfiles: profiles);
        var target = Target(
            822,
            monster.X + template.CollisionRange!.Value - 0.1f,
            monster.Z);

        AssertTickEqual(
            legacy.Advance(Start, [target]),
            ecs.Advance(Start, [target]),
            "authored melee collision target acquisition");
        var attack = AdvancePair(
            legacy,
            ecs,
            Start + MonsterMapRuntime.TickInterval,
            [target],
            "authored melee collision attack reach");
        AssertAttackTarget(
            attack,
            target.CharacterId,
            "a large melee monster attacks at its visible collision edge");
    }

    private static void CheckFirstHitClaimParity()
    {
        var monster = CreateMonster(11005, 100f, 50f);
        var legacy = new MonsterMapRuntime(0, [monster], Start);
        var ecs = new EcsMonsterMapRuntime(0, [monster], Start);

        Check.True(
            legacy.TryApplyDamage(
                monster.ObjectId,
                10,
                attackerCharacterId: 831,
                now: Start,
                out var legacyFirst) &&
            ecs.TryApplyDamage(
                monster.ObjectId,
                10,
                attackerCharacterId: 831,
                now: Start,
                out var ecsFirst) &&
            legacyFirst == ecsFirst &&
            legacyFirst.FirstHitCharacterId == 831 &&
            legacyFirst.ClaimEstablished,
            "only the first committed hit establishes the client claim");
        Check.True(
            legacy.TryApplyDamage(
                monster.ObjectId,
                uint.MaxValue,
                attackerCharacterId: 832,
                now: Start,
                out var legacyKill) &&
            ecs.TryApplyDamage(
                monster.ObjectId,
                uint.MaxValue,
                attackerCharacterId: 832,
                now: Start,
                out var ecsKill) &&
            legacyKill == ecsKill &&
            legacyKill.Killed &&
            legacyKill.FirstHitCharacterId == 831 &&
            !legacyKill.ClaimEstablished,
            "the first damage dealer retains the reward claim after aggro changes and another player kills");

        var leashMonster = CreateMonster(11006, 100f, 50f);
        var leashLegacy = new MonsterMapRuntime(0, [leashMonster], Start);
        var leashEcs = new EcsMonsterMapRuntime(0, [leashMonster], Start);
        ApplyDamagePair(
            leashLegacy,
            leashEcs,
            leashMonster.ObjectId,
            attackerCharacterId: 841,
            damage: 10,
            now: Start,
            description: "leash first-hit claim");
        var outside = Target(
            841,
            leashMonster.X + MonsterMapRuntime.CombatLeashRadius + 4f,
            leashMonster.Z);
        AssertTickEqual(
            leashLegacy.Advance(Start, [outside]),
            leashEcs.Advance(Start, [outside]),
            "leash return clears claim");
        Check.True(
            leashLegacy.TryApplyDamage(
                leashMonster.ObjectId,
                1,
                attackerCharacterId: 842,
                now: Start,
                out var legacyReturning) &&
            leashEcs.TryApplyDamage(
                leashMonster.ObjectId,
                1,
                attackerCharacterId: 842,
                now: Start,
                out var ecsReturning) &&
            legacyReturning == ecsReturning &&
            legacyReturning.BeforeHealth == legacyReturning.AfterHealth &&
            legacyReturning.FirstHitCharacterId is null,
            "leash return resets ownership before the replacement generation");
    }
}
