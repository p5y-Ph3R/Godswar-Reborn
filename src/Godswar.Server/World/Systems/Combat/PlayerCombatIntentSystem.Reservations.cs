using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal sealed partial class PlayerCombatIntentSystem
{
    private static void ReserveAndImmediatelyRefundZeroDamage(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        ref PlayerCombatResourceComponent resources,
        int manaCost,
        ulong admittedCombatRevision,
        in PlayerCombatTargetComponent target)
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
            TargetCount: 0));
        context.Events.Publish(new PlayerCombatIntentRejectedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            intent.Kind,
            PlayerCombatRejectionReason.ZeroDamage,
            target.ObjectId,
            resources.CurrentMp,
            resources.NextBasicAttackAt));
        RefundResources(
            manaCost,
            previousNextBasicAttackAt,
            ref resources);
        context.Events.Publish(new PlayerCombatResourceRefundedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            manaCost,
            resources.CurrentMp,
            resources.VitalsRevision,
            resources.NextBasicAttackAt,
            resources.CombatRevision));
        context.Events.Publish(new PlayerCombatReservationCompletedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            intent.Kind,
            AcceptedTargetCount: 0,
            RejectedTargetCount: 1,
            ResourcesRefunded: true));
    }

    private static void ReserveResources(
        in PlayerCombatIntentComponent intent,
        int manaCost,
        ulong admittedCombatRevision,
        ref PlayerCombatResourceComponent resources,
        int basicAttackIntervalMilliseconds = 1500)
    {
        resources.CurrentMp -= manaCost;
        if (manaCost > 0)
        {
            resources.VitalsRevision =
                checked(resources.VitalsRevision + 1);
        }

        if (intent.Kind == PlayerCombatIntentKind.BasicAttack)
        {
            resources.NextBasicAttackAt =
                intent.RequestedAt +
                PlayerCombatRules.ResolveBasicAttackCooldown(
                    basicAttackIntervalMilliseconds);
        }

        // Refund bookkeeping historically advances this local diagnostic
        // counter as well. The process-owned admitted revision remains the
        // event-identity authority, while the ECS copy must stay monotonic.
        resources.CombatRevision = Math.Max(
            resources.CombatRevision,
            admittedCombatRevision);
    }

    internal static void RefundResources(
        int mana,
        DateTimeOffset previousNextBasicAttackAt,
        ref PlayerCombatResourceComponent resources)
    {
        resources.CurrentMp = (int)Math.Min(
            Math.Max(0, resources.MaximumMp),
            (long)resources.CurrentMp + Math.Max(0, mana));
        if (mana > 0)
        {
            resources.VitalsRevision =
                checked(resources.VitalsRevision + 1);
        }

        resources.NextBasicAttackAt = previousNextBasicAttackAt;
        resources.CombatRevision =
            checked(resources.CombatRevision + 1);
    }

    internal static ulong NextSequence(
        ref PlayerCombatResourceComponent resources)
    {
        resources.EventSequence =
            checked(resources.EventSequence + 1);
        return resources.EventSequence;
    }

    private static void Reject(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        ref PlayerCombatResourceComponent resources,
        PlayerCombatRejectionReason reason)
    {
        context.Events.Publish(new PlayerCombatIntentRejectedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            intent.Kind,
            reason,
            intent.TargetObjectId,
            resources.CurrentMp,
            resources.NextBasicAttackAt));
    }

    private static bool TryFindSingleTarget(
        EcsWorld world,
        byte mapId,
        uint objectId,
        out PlayerCombatTargetComponent target)
    {
        var found = false;
        target = default;
        EntityId selectedEntity = default;
        foreach (var entity in world.Query<PlayerCombatTargetComponent>())
        {
            var candidate = world.Get<PlayerCombatTargetComponent>(entity);
            if (candidate.ObjectId != objectId ||
                candidate.MapId != mapId ||
                !IsAvailable(candidate))
            {
                continue;
            }

            if (!found || entity.CompareTo(selectedEntity) < 0)
            {
                found = true;
                selectedEntity = entity;
                target = candidate;
            }
        }

        return found;
    }

    private static bool TryValidateTargetGuard(
        in PlayerCombatIntentComponent intent,
        in PlayerCombatTargetComponent target,
        out PlayerCombatRejectionReason rejection)
    {
        if (target.SpawnGeneration !=
            intent.ExpectedTargetSpawnGeneration)
        {
            rejection =
                PlayerCombatRejectionReason.TargetGenerationMismatch;
            return false;
        }

        if (target.HealthRevision !=
            intent.ExpectedTargetHealthRevision)
        {
            rejection =
                PlayerCombatRejectionReason.TargetRevisionMismatch;
            return false;
        }

        rejection = PlayerCombatRejectionReason.None;
        return true;
    }

    private static ImmutableArray<PlayerCombatTargetComponent>
        SelectAreaTargets(
            EcsWorld world,
            byte mapId,
            float centerX,
            float centerZ,
            float radius)
    {
        var candidates =
            new List<(EntityId Entity, PlayerCombatTargetComponent Target)>();
        foreach (var entity in world.Query<PlayerCombatTargetComponent>())
        {
            var target = world.Get<PlayerCombatTargetComponent>(entity);
            if (target.MapId == mapId &&
                IsAvailable(target) &&
                PlayerCombatRules.IsWithinArea(
                    centerX,
                    centerZ,
                    target.X,
                    target.Z,
                    radius))
            {
                candidates.Add((entity, target));
            }
        }

        candidates.Sort(static (left, right) =>
        {
            var objectComparison =
                left.Target.ObjectId.CompareTo(right.Target.ObjectId);
            return objectComparison != 0
                ? objectComparison
                : left.Entity.CompareTo(right.Entity);
        });

        var targets =
            ImmutableArray.CreateBuilder<PlayerCombatTargetComponent>(
                candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var target = candidates[index].Target;
            targets.Add(target);
        }

        return targets.MoveToImmutable();
    }

    private static bool IsAvailable(
        in PlayerCombatTargetComponent target) =>
        target.IsVisible &&
        target.IsSpawned &&
        target.IsAlive &&
        target.CurrentHealth > 0;
}
