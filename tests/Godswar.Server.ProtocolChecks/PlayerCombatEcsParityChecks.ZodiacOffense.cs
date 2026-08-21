using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    private static void CheckZodiacProjectedReservationRefund()
    {
        var character = new GameCharacter { Profession = 0 };
        character.ZodiacSkillGridLevels[4] = 1;
        character.ZodiacSkillGridSkillIds[4] = 20_010;
        var authored = new SkillCombatDefinition(
            SkillId: 100,
            Target: 44,
            AffectObj: 28,
            Distance: 5f,
            Range: 0f,
            Property: 0,
            Mp: 25,
            Power1: 0.5m,
            Power2: 10m);
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            character,
            authored);
        var snapshot = new PlayerCombatSkillSnapshot(
            checked((uint)projected.Skill.SkillId),
            projected.Skill.Target,
            projected.Skill.AffectObj,
            projected.Skill.Distance,
            projected.Skill.Range,
            projected.Skill.Mp,
            projected.Skill.Property,
            projected.Skill.Power1,
            projected.Skill.Power2);
        var fixture = CreateFixture(currentMp: 100);
        var target = AddTarget(
            fixture,
            objectId: 201,
            currentHealth: 500,
            spawnGeneration: 4,
            healthRevision: 8);
        QueueSingle(fixture, target, snapshot);
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var damage =
            Events<PlayerCombatDamageIntentEvent>(fixture).Single();
        Check.True(
            projected.Skill.Mp == 27 &&
            fixture.World
                .Get<PlayerCombatResourceComponent>(fixture.Player)
                .CurrentMp == 73,
            "ECS reserves the rounded projected Zodiac MP");

        PlayerCombatEcsBoundary.QueueMutationOutcome(
            fixture.World,
            fixture.Player,
            new PlayerCombatMutationOutcomeComponent(
                damage.IntentId,
                damage.TargetOrder,
                damage.TargetObjectId,
                damage.ExpectedSpawnGeneration,
                damage.ExpectedHealthRevision,
                Applied: false,
                BeforeHealth: 0,
                AfterHealth: 0,
                AfterHealthRevision: damage.ExpectedHealthRevision,
                Killed: false,
                PlayerCombatMutationRejectionReason.TargetRejected));
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var refund =
            Events<PlayerCombatResourceRefundedEvent>(fixture).Single();
        Check.True(
            refund.RefundedMana == projected.Skill.Mp &&
            refund.CurrentMana == 100 &&
            fixture.World
                .Get<PlayerCombatResourceComponent>(fixture.Player)
                .CurrentMp == 100,
            "ECS refunds the same projected Zodiac MP after rejected mutation");
    }
}
