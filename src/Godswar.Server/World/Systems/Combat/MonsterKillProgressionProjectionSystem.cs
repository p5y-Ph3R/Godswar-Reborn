using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Projects an already committed progression result into ordered domain events.
/// Persistence remains the authority; this system never recomputes rewards,
/// level thresholds, or talent-point carry.
/// </summary>
internal sealed class MonsterKillProgressionProjectionSystem : IEcsSystem
{
    public const int SystemOrder = 700;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var player in context.World.Query<
                     MonsterKillProgressionProjectionComponent,
                     PlayerCommittedProgressionComponent,
                     PlayerCombatResourceComponent>())
        {
            var projection = context.World
                .Get<MonsterKillProgressionProjectionComponent>(player);
            context.Commands
                .Remove<MonsterKillProgressionProjectionComponent>(player);
            ref var resources = ref context.World
                .Get<PlayerCombatResourceComponent>(player);
            ref var progression = ref context.World
                .Get<PlayerCommittedProgressionComponent>(player);

            if (projection.ProjectionId <= progression.LastProjectionId)
            {
                Reject(
                    context,
                    player,
                    projection,
                    ref resources,
                    MonsterKillProgressionRejectionReason
                        .ProjectionOutOfOrder);
                continue;
            }

            if (projection.ExpectedProgressionRevision !=
                progression.Revision)
            {
                Reject(
                    context,
                    player,
                    projection,
                    ref resources,
                    MonsterKillProgressionRejectionReason
                        .ProgressionRevisionMismatch);
                continue;
            }

            if (!context.World.Has<PlayerCombatKillLedgerComponent>(player))
            {
                Reject(
                    context,
                    player,
                    projection,
                    ref resources,
                    MonsterKillProgressionRejectionReason.KillGuardMissing);
                continue;
            }

            var guard = new PlayerCombatKillGuard(
                projection.CombatIntentId,
                projection.MonsterObjectId,
                projection.MonsterSpawnGeneration,
                projection.MonsterHealthRevision);
            ref var ledger = ref context.World
                .Get<PlayerCombatKillLedgerComponent>(player);
            if (!ledger.TryConsume(guard))
            {
                Reject(
                    context,
                    player,
                    projection,
                    ref resources,
                    MonsterKillProgressionRejectionReason.KillGuardMissing);
                continue;
            }

            ApplyCommittedProjection(
                context,
                player,
                projection,
                ref progression,
                ref resources);
        }
    }

    private static void ApplyCommittedProjection(
        EcsSystemContext context,
        EntityId player,
        in MonsterKillProgressionProjectionComponent projection,
        ref PlayerCommittedProgressionComponent progression,
        ref PlayerCombatResourceComponent resources)
    {
        var committed = projection.Committed;
        progression.Level = committed.CurrentLevel;
        progression.Experience = committed.CurrentExperience;
        progression.TalentExperience =
            committed.CurrentTalentExperience;
        progression.TalentPoints = committed.CurrentTalentPoints;
        progression.Revision = checked(progression.Revision + 1);
        progression.LastProjectionId = projection.ProjectionId;

        var projectionOrder = 0;
        var levelUps = committed.LevelUps.IsDefault
            ? []
            : committed.LevelUps;
        foreach (var levelUp in levelUps)
        {
            context.Events.Publish(
                new MonsterKillLevelUpProjectedEvent(
                    PlayerCombatIntentSystem.NextSequence(ref resources),
                    projectionOrder++,
                    player,
                    projection.ProjectionId,
                    levelUp.Level,
                    levelUp.CurrentExperience,
                    levelUp.NextLevelExperience));
        }

        if (committed.ExperienceGained > 0)
        {
            context.Events.Publish(
                new MonsterKillExperienceProjectedEvent(
                    PlayerCombatIntentSystem.NextSequence(ref resources),
                    projectionOrder++,
                    player,
                    projection.ProjectionId,
                    committed.ExperienceGained,
                    committed.CurrentExperience,
                    committed.NextLevelExperience));
        }

        if (committed.TalentExperienceGained > 0)
        {
            context.Events.Publish(
                new MonsterKillTalentExperienceProjectedEvent(
                    PlayerCombatIntentSystem.NextSequence(ref resources),
                    projectionOrder++,
                    player,
                    projection.ProjectionId,
                    committed.TalentExperienceGained,
                    committed.CurrentTalentExperience));
        }

        context.Events.Publish(
            new MonsterDeathProgressionProjectedEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                projectionOrder++,
                player,
                projection.ProjectionId,
                projection.MonsterObjectId,
                projection.MonsterSpawnGeneration,
                committed.CurrentExperience,
                committed.CurrentTalentExperience,
                committed.CurrentTalentPoints));

        if (committed.TalentPointsGained > 0)
        {
            context.Events.Publish(
                new MonsterKillTalentPointsProjectedEvent(
                    PlayerCombatIntentSystem.NextSequence(ref resources),
                    projectionOrder++,
                    player,
                    projection.ProjectionId,
                    committed.TalentPointsGained,
                    committed.CurrentTalentPoints));
        }

        context.Events.Publish(
            new MonsterKillProgressionAppliedEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                projectionOrder,
                player,
                projection.ProjectionId,
                committed.PreviousLevel,
                committed.CurrentLevel,
                progression.Revision));
    }

    private static void Reject(
        EcsSystemContext context,
        EntityId player,
        in MonsterKillProgressionProjectionComponent projection,
        ref PlayerCombatResourceComponent resources,
        MonsterKillProgressionRejectionReason reason)
    {
        context.Events.Publish(
            new MonsterKillProgressionRejectedEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                player,
                projection.ProjectionId,
                projection.CombatIntentId,
                projection.MonsterObjectId,
                reason));
    }
}
