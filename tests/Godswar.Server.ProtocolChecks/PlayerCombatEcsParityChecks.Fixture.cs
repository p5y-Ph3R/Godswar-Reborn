using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    private static Fixture CreateFixture(
        int currentHp = 100,
        int currentMp = 100,
        DateTimeOffset? nextBasicAttackAt = null)
    {
        var world = new EcsWorld();
        var player = PlayerCombatEcsBoundary.HydratePlayer(
            world,
            new PlayerCombatHydrationSnapshot(
                new PlayerCombatIdentityComponent(7, 0x1448),
                new PlayerCombatTransformComponent(2, 0f, 0f),
                CreateOffense(),
                new PlayerCombatResourceSnapshot(
                    currentHp,
                    MaximumHp: 100,
                    currentMp,
                    MaximumMp: 100,
                    VitalsRevision: 0,
                    nextBasicAttackAt ?? DateTimeOffset.MinValue,
                    CombatRevision: 0,
                    EventSequence: 0),
                new PlayerCommittedProgressionSnapshot(
                    Level: 10,
                    Experience: 0,
                    TalentExperience: 90,
                    TalentPoints: 3,
                    Revision: 0,
                    LastProjectionId: 0)));
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new PlayerCombatIntentSystem());
        scheduler.AddSystem(new PlayerCombatMutationOutcomeSystem());
        scheduler.AddSystem(new MonsterKillProgressionProjectionSystem());
        return new Fixture(world, scheduler, player);
    }

    private static PlayerCombatTargetComponent AddTarget(
        Fixture fixture,
        uint objectId,
        float x = 1f,
        uint currentHealth = 500,
        uint spawnGeneration = 1,
        ulong healthRevision = 0,
        bool isVisible = true)
    {
        var target = new PlayerCombatTargetComponent(
            objectId,
            MapId: 2,
            x,
            Z: 0f,
            currentHealth,
            IsSpawned: true,
            IsAlive: currentHealth > 0,
            isVisible,
            spawnGeneration,
            healthRevision,
            BasicAttackRange:
                PlayerCombatRules.DefaultBasicAttackRange);
        PlayerCombatEcsBoundary.HydrateTarget(
            fixture.World,
            target);
        return target;
    }

    private static void QueueBasic(
        Fixture fixture,
        in PlayerCombatTargetComponent target,
        DateTimeOffset requestedAt)
    {
        PlayerCombatEcsBoundary.QueueIntent(
            fixture.World,
            fixture.Player,
            new PlayerCombatIntentComponent(
                IntentId: 1,
                PlayerCombatIntentKind.BasicAttack,
                requestedAt,
                target.ObjectId,
                target.SpawnGeneration,
                target.HealthRevision,
                ReportedAttackerX: 0f,
                ReportedAttackerZ: 0f,
                HasReportedTargetPosition: false,
                ReportedTargetX: float.NaN,
                ReportedTargetZ: float.NaN,
                Skill: default));
    }

    private static void QueueSingle(
        Fixture fixture,
        in PlayerCombatTargetComponent target,
        in PlayerCombatSkillSnapshot skill)
    {
        PlayerCombatEcsBoundary.QueueIntent(
            fixture.World,
            fixture.Player,
            SingleIntent(target) with { Skill = skill });
    }

    private static PlayerCombatIntentComponent SingleIntent(
        in PlayerCombatTargetComponent target) =>
        new(
            IntentId: 10,
            PlayerCombatIntentKind.SingleTargetSkill,
            Start,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            ReportedAttackerX: 0f,
            ReportedAttackerZ: 0f,
            HasReportedTargetPosition: false,
            ReportedTargetX: float.NaN,
            ReportedTargetZ: float.NaN,
            SingleSkill);

    private static void AssertRejected(
        Fixture fixture,
        PlayerCombatRejectionReason expected,
        string context)
    {
        var rejection =
            Events<PlayerCombatIntentRejectedEvent>(fixture).Single();
        Check.True(rejection.Reason == expected,
            $"{context} rejection reason");
        Check.Equal(
            0,
            Events<PlayerCombatDamageIntentEvent>(fixture).Length,
            $"{context} emits no mutation intent");
    }

    private static PlayerCombatOffenseComponent CreateOffense() =>
        new(
            Profession: 0,
            PhysicalAttack: 100,
            MagicAttack: 140,
            PhysicalDamageBonus: 1_000,
            MagicDamageBonus: 500,
            PhysicalAppendDamage: 5,
            MagicAppendDamage: 7);

    private static SkillCombatDefinition ToLegacy(
        in PlayerCombatSkillSnapshot skill) =>
        new(
            (int)skill.SkillId,
            skill.Target,
            skill.AffectObject,
            skill.Distance,
            skill.AreaRadius,
            skill.Property,
            skill.ManaCost,
            skill.Power1,
            skill.Power2);

    private static T[] Events<T>(Fixture fixture)
        where T : struct =>
        fixture.Scheduler.Events.Read<T>().ToArray();

    private readonly record struct Fixture(
        EcsWorld World,
        EcsSystemScheduler Scheduler,
        EntityId Player);
}
