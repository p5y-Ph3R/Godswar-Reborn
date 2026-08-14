using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Checks for the transport-neutral combat slice. These run independently of
/// socket and persistence adapters so the ECS rules stay deterministic.
/// </summary>
internal static partial class PlayerCombatEcsParityChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly PlayerCombatSkillSnapshot SingleSkill = new(
        SkillId: 2001,
        Target: 44,
        AffectObject: 28,
        Distance: 5f,
        AreaRadius: 0f,
        ManaCost: 25,
        Property: 0,
        Power1: 0.5m,
        Power2: 10m);

    private static readonly PlayerCombatSkillSnapshot AreaSkill = new(
        SkillId: 3001,
        Target: 1,
        AffectObject: 8,
        Distance: 0f,
        AreaRadius: 10f,
        ManaCost: 30,
        Property: 1,
        Power1: 0.25m,
        Power2: 4m);

    private static readonly PlayerCombatSkillSnapshot GroundAreaSkill = new(
        SkillId: 564,
        Target: 16,
        AffectObject: 28,
        Distance: 11f,
        AreaRadius: 3f,
        ManaCost: 40,
        Property: 1,
        Power1: 0.5m,
        Power2: 20m);

    public static Task RunAsync()
    {
        CheckResolverParity();
        CheckAuthoredCombatV1();
        CheckEcsBasicAttackResolution();
        CheckEcsHostileSkillResolution();
        CheckRequiredRejections();
        CheckSingleTargetReservationRefund();
        CheckAreaOrderingAndReservation();
        CheckGroundAreaSelectionAndRange();
        CheckCommittedProgressionProjection();
        return Task.CompletedTask;
    }

    private static void CheckResolverParity()
    {
        var offense = CreateOffense();
        var character = new GameCharacter
        {
            Profession = offense.Profession,
            CalculatedStats = new CharacterStats
            {
                PhysicalAttack = offense.PhysicalAttack,
                MagicAttack = offense.MagicAttack,
                PhysicalDamageBonus = offense.PhysicalDamageBonus,
                MagicDamageBonus = offense.MagicDamageBonus,
                PhysicalAppendDamage = offense.PhysicalAppendDamage,
                MagicAppendDamage = offense.MagicAppendDamage
            }
        };
        var legacySkill = ToLegacy(SingleSkill);

        Check.Equal(
            MonsterCombatResolver.CalculatePlayerBasicAttack(character),
            PlayerCombatRules.CalculateBasicAttack(offense),
            "ECS basic-attack damage matches the live resolver");
        Check.Equal(
            SkillCombatResolver.CalculateDamage(character, legacySkill),
            PlayerCombatRules.CalculateSkillDamage(offense, SingleSkill),
            "ECS skill damage matches the live resolver");
        Check.Equal(
            SkillCombatResolver.IsWithinRange(
                0f,
                0f,
                7.99f,
                0f,
                legacySkill),
            PlayerCombatRules.IsWithinSkillRange(
                0f,
                0f,
                7.99f,
                0f,
                SingleSkill),
            "ECS selected-target range matches the live resolver");

        var legacyArea = ToLegacy(AreaSkill);
        Check.Equal(
            SkillCombatResolver.IsWithinArea(
                0f,
                0f,
                9.99f,
                0f,
                legacyArea),
            PlayerCombatRules.IsWithinArea(
                0f,
                0f,
                9.99f,
                0f,
                AreaSkill.AreaRadius),
            "ECS area range matches the live resolver");
        Check.True(
            !PlayerCombatRules.IsWithinArea(
                0f,
                0f,
                AreaSkill.AreaRadius,
                0f,
                AreaSkill.AreaRadius),
            "ECS area selection retains the strict-radius boundary");
    }

    private static void CheckRequiredRejections()
    {
        var dead = CreateFixture(currentHp: 0);
        var deadTarget = AddTarget(dead, objectId: 100);
        QueueBasic(dead, deadTarget, Start);
        dead.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            dead,
            PlayerCombatRejectionReason.SourceDead,
            "dead source");

        var cooldown = CreateFixture(
            nextBasicAttackAt: Start.AddSeconds(1));
        var cooldownTarget = AddTarget(cooldown, objectId: 101);
        QueueBasic(cooldown, cooldownTarget, Start);
        cooldown.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            cooldown,
            PlayerCombatRejectionReason.CooldownActive,
            "basic cooldown");

        var range = CreateFixture();
        var rangeTarget = AddTarget(
            range,
            objectId: 102,
            x: 100f);
        QueueSingle(range, rangeTarget, SingleSkill);
        range.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            range,
            PlayerCombatRejectionReason.OutOfRange,
            "single-target range");

        var mana = CreateFixture(currentMp: 24);
        var manaTarget = AddTarget(mana, objectId: 103);
        QueueSingle(mana, manaTarget, SingleSkill);
        mana.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            mana,
            PlayerCombatRejectionReason.InsufficientMana,
            "skill mana");

        var generation = CreateFixture();
        var generationTarget = AddTarget(
            generation,
            objectId: 104,
            spawnGeneration: 9);
        var staleIntent = SingleIntent(generationTarget) with
        {
            ExpectedTargetSpawnGeneration = 8
        };
        PlayerCombatEcsBoundary.QueueIntent(
            generation.World,
            generation.Player,
            staleIntent);
        generation.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            generation,
            PlayerCombatRejectionReason.TargetGenerationMismatch,
            "target generation");

        var revision = CreateFixture();
        var revisionTarget = AddTarget(
            revision,
            objectId: 105,
            healthRevision: 12);
        var staleRevision = SingleIntent(revisionTarget) with
        {
            ExpectedTargetHealthRevision = 11
        };
        PlayerCombatEcsBoundary.QueueIntent(
            revision.World,
            revision.Player,
            staleRevision);
        revision.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            revision,
            PlayerCombatRejectionReason.TargetRevisionMismatch,
            "target revision");
    }

    private static void CheckSingleTargetReservationRefund()
    {
        var fixture = CreateFixture();
        var target = AddTarget(
            fixture,
            objectId: 200,
            currentHealth: 500,
            spawnGeneration: 3,
            healthRevision: 7);
        QueueSingle(fixture, target, SingleSkill);
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var damageIntent =
            Events<PlayerCombatDamageIntentEvent>(fixture).Single();
        Check.Equal(75, fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player)
            .CurrentMp, "single-target mana is reserved");
        Check.Equal(3u, damageIntent.ExpectedSpawnGeneration,
            "damage intent carries the spawn-generation guard");
        Check.Equal(7UL, damageIntent.ExpectedHealthRevision,
            "damage intent carries the health-revision guard");
        Check.True(
            fixture.World.Has<PlayerCombatReservationComponent>(
                fixture.Player),
            "single-target mutation remains reserved");

        PlayerCombatEcsBoundary.QueueMutationOutcome(
            fixture.World,
            fixture.Player,
            new PlayerCombatMutationOutcomeComponent(
                damageIntent.IntentId,
                damageIntent.TargetOrder,
                damageIntent.TargetObjectId,
                damageIntent.ExpectedSpawnGeneration,
                damageIntent.ExpectedHealthRevision,
                Applied: false,
                BeforeHealth: 0,
                AfterHealth: 0,
                AfterHealthRevision: damageIntent.ExpectedHealthRevision,
                Killed: false,
                PlayerCombatMutationRejectionReason.TargetRejected));
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var refund =
            Events<PlayerCombatResourceRefundedEvent>(fixture).Single();
        Check.Equal(25, refund.RefundedMana,
            "rejected single-target mutation reports its refund");
        Check.Equal(100, refund.CurrentMana,
            "rejected single-target mutation restores mana");
        Check.True(
            !fixture.World.Has<PlayerCombatReservationComponent>(
                fixture.Player),
            "rejected single-target reservation is closed");
    }

    private static void CheckAreaOrderingAndReservation()
    {
        var fixture = CreateFixture();
        AddTarget(fixture, objectId: 330, x: 3f);
        AddTarget(fixture, objectId: 110, x: 1f);
        AddTarget(fixture, objectId: 220, x: 2f);
        AddTarget(
            fixture,
            objectId: 440,
            x: AreaSkill.AreaRadius);
        AddTarget(
            fixture,
            objectId: 55,
            x: 1f,
            isVisible: false);

        PlayerCombatEcsBoundary.QueueIntent(
            fixture.World,
            fixture.Player,
            new PlayerCombatIntentComponent(
                IntentId: 20,
                PlayerCombatIntentKind.AreaSkill,
                Start,
                TargetObjectId: uint.MaxValue,
                ExpectedTargetSpawnGeneration: 0,
                ExpectedTargetHealthRevision: 0,
                ReportedAttackerX: 0f,
                ReportedAttackerZ: 0f,
                HasReportedTargetPosition: false,
                ReportedTargetX: float.NaN,
                ReportedTargetZ: float.NaN,
                AreaSkill));
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var intents = Events<PlayerCombatDamageIntentEvent>(fixture);
        Check.Equal(3, intents.Length,
            "area cast selects only visible targets inside the radius");
        Check.Equal(110u, intents[0].TargetObjectId,
            "area targets are ordered by object ID first");
        Check.Equal(220u, intents[1].TargetObjectId,
            "area targets are ordered by object ID second");
        Check.Equal(330u, intents[2].TargetObjectId,
            "area targets are ordered by object ID third");
        Check.Equal(70, fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player)
            .CurrentMp, "area cast reserves mana once");

        var first = intents[0];
        PlayerCombatEcsBoundary.QueueMutationOutcome(
            fixture.World,
            fixture.Player,
            new PlayerCombatMutationOutcomeComponent(
                first.IntentId,
                first.TargetOrder,
                first.TargetObjectId,
                first.ExpectedSpawnGeneration,
                first.ExpectedHealthRevision,
                Applied: false,
                BeforeHealth: 0,
                AfterHealth: 0,
                AfterHealthRevision: first.ExpectedHealthRevision,
                Killed: false,
                PlayerCombatMutationRejectionReason.TargetRejected));
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        Check.Equal(
            0,
            Events<PlayerCombatResourceRefundedEvent>(fixture).Length,
            "rejected area target does not refund the cast");
        Check.Equal(70, fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player)
            .CurrentMp, "area mana remains committed after a rejected target");
    }

    private static void CheckGroundAreaSelectionAndRange()
    {
        var fixture = CreateFixture();
        AddTarget(fixture, objectId: 610, x: 1f);
        AddTarget(fixture, objectId: 620, x: 12f);

        PlayerCombatEcsBoundary.QueueIntent(
            fixture.World,
            fixture.Player,
            new PlayerCombatIntentComponent(
                IntentId: 21,
                PlayerCombatIntentKind.AreaSkill,
                Start,
                TargetObjectId: uint.MaxValue,
                ExpectedTargetSpawnGeneration: 0,
                ExpectedTargetHealthRevision: 0,
                ReportedAttackerX: 0f,
                ReportedAttackerZ: 0f,
                HasReportedTargetPosition: true,
                ReportedTargetX: 10f,
                ReportedTargetZ: 0f,
                GroundAreaSkill));
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var intents = Events<PlayerCombatDamageIntentEvent>(fixture);
        Check.Equal(1, intents.Length,
            "ground area selects from the validated cursor centre");
        Check.Equal(620u, intents[0].TargetObjectId,
            "ground area excludes a monster near only the caster");
        Check.Equal(60, fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player)
            .CurrentMp, "ground area reserves mana once");

        var outOfRange = CreateFixture();
        PlayerCombatEcsBoundary.QueueIntent(
            outOfRange.World,
            outOfRange.Player,
            new PlayerCombatIntentComponent(
                IntentId: 22,
                PlayerCombatIntentKind.AreaSkill,
                Start,
                TargetObjectId: uint.MaxValue,
                ExpectedTargetSpawnGeneration: 0,
                ExpectedTargetHealthRevision: 0,
                ReportedAttackerX: 0f,
                ReportedAttackerZ: 0f,
                HasReportedTargetPosition: true,
                ReportedTargetX: 12f,
                ReportedTargetZ: 0f,
                GroundAreaSkill));
        outOfRange.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            outOfRange,
            PlayerCombatRejectionReason.OutOfRange,
            "ground area cursor range");
        Check.Equal(100, outOfRange.World
            .Get<PlayerCombatResourceComponent>(outOfRange.Player)
            .CurrentMp, "rejected ground area does not reserve mana");
    }

    private static void CheckCommittedProgressionProjection()
    {
        var fixture = CreateFixture();
        var target = AddTarget(
            fixture,
            objectId: 200,
            currentHealth: 50,
            spawnGeneration: 3,
            healthRevision: 7);
        QueueSingle(fixture, target, SingleSkill);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        var damage = Events<PlayerCombatDamageIntentEvent>(fixture).Single();

        PlayerCombatEcsBoundary.QueueMutationOutcome(
            fixture.World,
            fixture.Player,
            new PlayerCombatMutationOutcomeComponent(
                damage.IntentId,
                damage.TargetOrder,
                damage.TargetObjectId,
                damage.ExpectedSpawnGeneration,
                damage.ExpectedHealthRevision,
                Applied: true,
                BeforeHealth: 50,
                AfterHealth: 0,
                AfterHealthRevision:
                    damage.ExpectedHealthRevision + 1,
                Killed: true,
                PlayerCombatMutationRejectionReason.None));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        var kill = Events<MonsterKilledByPlayerCombatEvent>(
            fixture).Single();
        var guard = new PlayerCombatKillGuard(
            kill.CombatIntentId,
            kill.MonsterObjectId,
            kill.MonsterSpawnGeneration,
            kill.MonsterHealthRevision);

        var mutableLevelUps = new List<PlayerLevelUpProgression>
        {
            new(11, 210, 900),
            new(12, 5, 1_000)
        };
        var committed = new CharacterProgressionResult(
            ExperienceGained: 250,
            PreviousLevel: 10,
            CurrentLevel: 12,
            CurrentExperience: 5,
            NextLevelExperience: 1_000,
            mutableLevelUps,
            TalentExperienceGained: 20,
            CurrentTalentExperience: 10,
            TalentPointsGained: 1,
            CurrentTalentPoints: 4);
        PlayerCombatEcsBoundary.QueueCommittedProgression(
            fixture.World,
            fixture.Player,
            projectionId: 1,
            guard,
            expectedProgressionRevision: 0,
            committed);
        mutableLevelUps.Clear();
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var levelUps =
            Events<MonsterKillLevelUpProjectedEvent>(fixture);
        var experience =
            Events<MonsterKillExperienceProjectedEvent>(fixture).Single();
        var talentExperience =
            Events<MonsterKillTalentExperienceProjectedEvent>(
                fixture).Single();
        var death =
            Events<MonsterDeathProgressionProjectedEvent>(fixture).Single();
        var talentPoints =
            Events<MonsterKillTalentPointsProjectedEvent>(fixture).Single();
        var applied =
            Events<MonsterKillProgressionAppliedEvent>(fixture).Single();

        Check.Equal(2, levelUps.Length,
            "boundary copies the committed level-up list");
        Check.Equal(0, levelUps[0].ProjectionOrder,
            "first committed level-up preserves order");
        Check.Equal(1, levelUps[1].ProjectionOrder,
            "second committed level-up preserves order");
        Check.Equal(2, experience.ProjectionOrder,
            "experience follows level-ups");
        Check.Equal(3, talentExperience.ProjectionOrder,
            "talent experience follows fighter experience");
        Check.Equal(4, death.ProjectionOrder,
            "death progression follows gained experience");
        Check.Equal(5, talentPoints.ProjectionOrder,
            "talent-point carry follows death progression");
        Check.Equal(6, applied.ProjectionOrder,
            "projection summary is emitted last");
        Check.True(
            levelUps[0].Sequence < levelUps[1].Sequence &&
            levelUps[1].Sequence < experience.Sequence &&
            experience.Sequence < talentExperience.Sequence &&
            talentExperience.Sequence < death.Sequence &&
            death.Sequence < talentPoints.Sequence &&
            talentPoints.Sequence < applied.Sequence,
            "typed progression events carry a total order");

        var state = fixture.World
            .Get<PlayerCommittedProgressionComponent>(fixture.Player);
        Check.Equal(committed.CurrentLevel, state.Level,
            "ECS consumes the committed level verbatim");
        Check.Equal(committed.CurrentExperience, state.Experience,
            "ECS consumes committed fighter experience verbatim");
        Check.Equal(
            committed.CurrentTalentExperience,
            state.TalentExperience,
            "ECS consumes committed talent experience verbatim");
        Check.Equal(committed.CurrentTalentPoints, state.TalentPoints,
            "ECS consumes committed talent points verbatim");
    }
}
