using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal sealed partial class PlayerCombatIntentSystem
{
    private static void ResolveAndReserveSingleTargetSkill(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatTargetComponent target,
        ulong admittedCombatRevision,
        ref PlayerCombatResourceComponent resources,
        int manaCost)
    {
        var identity = context.World
            .Get<PlayerCombatIdentityComponent>(player);
        var resolution = ResolveSkillTarget(
            identity.CharacterId,
            offense,
            intent.Skill,
            target,
            admittedCombatRevision,
            targetOrder: 0);
        if (resolution.Hit && resolution.Damage == 0)
        {
            ReserveAndImmediatelyRefundZeroDamage(
                context,
                player,
                intent,
                ref resources,
                manaCost,
                admittedCombatRevision,
                target);
            return;
        }

        ReserveResolvedSkill(
            context,
            player,
            intent,
            identity,
            [new ResolvedSkillTarget(target, resolution)],
            admittedCombatRevision,
            ref resources,
            manaCost,
            refundOnRejectedTarget: true);
    }

    private static void ResolveAndReserveAreaSkill(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        byte mapId,
        float centerX,
        float centerZ,
        in PlayerCombatOffenseComponent offense,
        ulong admittedCombatRevision,
        ref PlayerCombatResourceComponent resources,
        int manaCost)
    {
        var candidates = SelectAreaTargets(
            context.World,
            mapId,
            centerX,
            centerZ,
            intent.Skill.AreaRadius);
        var identity = context.World
            .Get<PlayerCombatIdentityComponent>(player);
        var resolved = ImmutableArray.CreateBuilder<ResolvedSkillTarget>(
            candidates.Length);
        for (var targetOrder = 0;
             targetOrder < candidates.Length;
             targetOrder++)
        {
            var target = candidates[targetOrder];
            resolved.Add(new ResolvedSkillTarget(
                target,
                ResolveSkillTarget(
                    identity.CharacterId,
                    offense,
                    intent.Skill,
                    target,
                    admittedCombatRevision,
                    targetOrder)));
        }

        ReserveResolvedSkill(
            context,
            player,
            intent,
            identity,
            resolved.MoveToImmutable(),
            admittedCombatRevision,
            ref resources,
            manaCost,
            refundOnRejectedTarget: false);
    }

    private static CombatResolution ResolveSkillTarget(
        int characterId,
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatSkillSnapshot skill,
        in PlayerCombatTargetComponent target,
        ulong admittedCombatRevision,
        int targetOrder)
    {
        var eventId = CombatEventIdentity.ForPlayerMonsterSkill(
            characterId,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            admittedCombatRevision,
            skill.SkillId,
            targetOrder);
        return PlayerCombatRules.ResolveSkillDamage(
            CombatCharacterStatsAdapter.FromOffense(offense),
            SnapshotTargetStats(target),
            skill,
            eventId,
            targetOrder);
    }

    private static void ReserveResolvedSkill(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatIdentityComponent identity,
        ImmutableArray<ResolvedSkillTarget> resolved,
        ulong admittedCombatRevision,
        ref PlayerCombatResourceComponent resources,
        int manaCost,
        bool refundOnRejectedTarget)
    {
        var previousNextBasicAttackAt = resources.NextBasicAttackAt;
        ReserveResources(
            intent,
            manaCost,
            admittedCombatRevision,
            ref resources);
        context.Events.Publish(new PlayerCombatResourceReservedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            intent.Kind,
            manaCost,
            resources.CurrentMp,
            resources.VitalsRevision,
            resources.NextBasicAttackAt,
            resources.CombatRevision,
            resolved.Length));

        var mutationTargets =
            ImmutableArray.CreateBuilder<PlayerCombatReservedTarget>(
                resolved.Length);
        for (var targetOrder = 0;
             targetOrder < resolved.Length;
             targetOrder++)
        {
            var entry = resolved[targetOrder];
            context.Events.Publish(new PlayerCombatTargetResolvedEvent(
                NextSequence(ref resources),
                player,
                intent.IntentId,
                intent.Kind,
                targetOrder,
                resolved.Length,
                entry.Target.ObjectId,
                entry.Target.SpawnGeneration,
                entry.Target.HealthRevision,
                entry.Resolution));
            if (entry.Resolution.Hit && entry.Resolution.Damage > 0)
            {
                mutationTargets.Add(new PlayerCombatReservedTarget(
                    targetOrder,
                    entry.Target.ObjectId,
                    entry.Target.CurrentHealth,
                    entry.Target.SpawnGeneration,
                    entry.Target.HealthRevision,
                    entry.Resolution.Damage));
            }
        }

        if (mutationTargets.Count == 0)
        {
            context.Events.Publish(
                new PlayerCombatReservationCompletedEvent(
                    NextSequence(ref resources),
                    player,
                    intent.IntentId,
                    intent.Kind,
                    AcceptedTargetCount: resolved.Length,
                    RejectedTargetCount: 0,
                    ResourcesRefunded: false));
            return;
        }

        var targets = mutationTargets.ToImmutable();
        var mapId = context.World
            .Get<PlayerCombatTransformComponent>(player)
            .MapId;
        foreach (var target in targets)
        {
            context.Events.Publish(new PlayerCombatDamageIntentEvent(
                NextSequence(ref resources),
                player,
                identity.CharacterId,
                identity.ObjectId,
                mapId,
                intent.IntentId,
                intent.Kind,
                intent.Skill.SkillId,
                target.TargetOrder,
                resolved.Length,
                target.ObjectId,
                target.RequestedDamage,
                target.ExpectedSpawnGeneration,
                target.ExpectedHealthRevision));
        }

        context.Commands.Add(
            player,
            new PlayerCombatReservationComponent(
                intent.IntentId,
                intent.Kind,
                intent.Skill.SkillId,
                manaCost,
                previousNextBasicAttackAt,
                refundOnRejectedTarget,
                targets,
                acceptedTargetCount: resolved.Length - targets.Length));
    }

    private readonly record struct ResolvedSkillTarget(
        PlayerCombatTargetComponent Target,
        CombatResolution Resolution);
}
