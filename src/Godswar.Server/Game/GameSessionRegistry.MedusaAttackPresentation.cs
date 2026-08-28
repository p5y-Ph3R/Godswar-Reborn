using Godswar.Server.Application.Characters;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const uint DefaultMonsterImpactSkillId = 2000;

    private static uint ResolveMedusaMonsterImpactSkillId(
        MonsterRuntimeUpdate attack,
        in MonsterAttackEcsTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(attack);
        if (transaction.MedusaOutcome !=
                MedusaMonsterPlayerHitCommitOutcome.AppliedWithEffect ||
            transaction.TargetContext is not { } target ||
            !transaction.Decision.Applied ||
            transaction.Decision.AppliedDamage == 0 ||
            transaction.Decision.Killed ||
            transaction.MedusaMechanicsResult is not { } mechanics ||
            mechanics.Outcome is not (
                MedusaMechanicHitOutcome.Applied or
                MedusaMechanicHitOutcome.Refreshed) ||
            mechanics.Effect is not { } effect ||
            mechanics.PeriodicDamage is not null)
        {
            return DefaultMonsterImpactSkillId;
        }

        return ResolveMedusaMonsterImpactSkillIdForEffect(
            target.MapId,
            target.Ownership,
            transaction.Decision.AfterLifeRevision,
            target.WorldMembershipEpoch,
            attack.Monster.ObjectId,
            attack.Monster.SpawnGeneration,
            effect);
    }

    internal static uint ResolveMedusaMonsterImpactSkillIdForEffect(
        byte targetMapId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        in MedusaActiveEncounterEffectSnapshot effect)
    {
        if (targetWorldMembershipEpoch <= 0 ||
            effect.Definition.Kind == MedusaEncounterEffectKind.Bleed ||
            effect.TargetOwnership != targetOwnership ||
            effect.TargetLifeRevision != targetLifeRevision ||
            effect.TargetWorldMembershipEpoch !=
                targetWorldMembershipEpoch ||
            effect.SourceObjectId != sourceObjectId ||
            effect.SourceSpawnGeneration !=
                sourceSpawnGeneration ||
            !effect.Definition.ClientProjection.MayEmitNativeReferenceStatus ||
            !TryResolveMedusaAuthoredSkillBinding(
                targetMapId,
                effect.SourceRosterSpawnId,
                effect.Definition.Kind,
                out var binding) ||
            binding.Mechanic != effect.Definition.Mechanic ||
            binding.StatusId !=
                effect.Definition.ClientProjection.EmittableStatusId)
        {
            return DefaultMonsterImpactSkillId;
        }

        return checked((uint)binding.SkillId);
    }
}
