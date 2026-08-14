using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal sealed partial class PlayerCombatIntentSystem
{
    private static void ResolveAndReserveBasicAttack(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatTargetComponent target,
        ulong admittedCombatRevision,
        ref PlayerCombatResourceComponent resources,
        int basicAttackIntervalMilliseconds)
    {
        var identity = context.World
            .Get<PlayerCombatIdentityComponent>(player);
        var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
            identity.CharacterId,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            admittedCombatRevision);
        var attacker = CombatCharacterStatsAdapter.FromOffense(offense);
        var targetStats = SnapshotTargetStats(target);
        var resolution = PlayerCombatRules.ResolveBasicAttack(
            attacker,
            targetStats,
            eventId,
            targetOrder: 0);
        ReserveResolvedBasicAttack(
            context,
            player,
            intent,
            identity,
            target,
            resolution,
            admittedCombatRevision,
            ref resources,
            basicAttackIntervalMilliseconds);
    }

    private static void ReserveResolvedBasicAttack(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatIdentityComponent identity,
        in PlayerCombatTargetComponent target,
        in CombatResolution resolution,
        ulong admittedCombatRevision,
        ref PlayerCombatResourceComponent resources,
        int basicAttackIntervalMilliseconds)
    {
        var previousNextBasicAttackAt = resources.NextBasicAttackAt;
        ReserveResources(
            intent,
            manaCost: 0,
            admittedCombatRevision,
            ref resources,
            basicAttackIntervalMilliseconds);
        context.Events.Publish(new PlayerCombatResourceReservedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            intent.Kind,
            ReservedMana: 0,
            resources.CurrentMp,
            resources.VitalsRevision,
            resources.NextBasicAttackAt,
            resources.CombatRevision,
            TargetCount: 1));
        context.Events.Publish(new PlayerCombatTargetResolvedEvent(
            NextSequence(ref resources),
            player,
            intent.IntentId,
            intent.Kind,
            TargetOrder: 0,
            TargetCount: 1,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            resolution));

        if (!resolution.Hit)
        {
            context.Events.Publish(
                new PlayerCombatReservationCompletedEvent(
                    NextSequence(ref resources),
                    player,
                    intent.IntentId,
                    intent.Kind,
                    AcceptedTargetCount: 1,
                    RejectedTargetCount: 0,
                    ResourcesRefunded: false));
            return;
        }

        var reservedTarget = new PlayerCombatReservedTarget(
            TargetOrder: 0,
            target.ObjectId,
            target.CurrentHealth,
            target.SpawnGeneration,
            target.HealthRevision,
            resolution.Damage);
        var targets = ImmutableArray.Create(reservedTarget);
        var mapId = context.World
            .Get<PlayerCombatTransformComponent>(player)
            .MapId;
        context.Events.Publish(new PlayerCombatDamageIntentEvent(
            NextSequence(ref resources),
            player,
            identity.CharacterId,
            identity.ObjectId,
            mapId,
            intent.IntentId,
            intent.Kind,
            SkillId: 0,
            TargetOrder: 0,
            TargetCount: 1,
            target.ObjectId,
            resolution.Damage,
            target.SpawnGeneration,
            target.HealthRevision));
        context.Commands.Add(
            player,
            new PlayerCombatReservationComponent(
                intent.IntentId,
                intent.Kind,
                skillId: 0,
                reservedMana: 0,
                previousNextBasicAttackAt,
                refundOnRejectedTarget: true,
                targets));
    }

    private static CombatTargetStats SnapshotTargetStats(
        in PlayerCombatTargetComponent target) =>
        new()
        {
            Level = target.Level,
            PhysicalDefense = target.PhysicalDefense,
            MagicDefense = target.MagicDefense,
            Dodge = target.Dodge,
            CriticalResistance = target.CriticalResistance,
            PhysicalDamageReductionBasisPoints =
                target.PhysicalDamageReductionBasisPoints,
            MagicDamageReductionBasisPoints =
                target.MagicDamageReductionBasisPoints,
            CriticalDamageReductionBasisPoints =
                target.CriticalDamageReductionBasisPoints,
            PhysicalFlatAbsorption = target.PhysicalFlatAbsorption,
            MagicFlatAbsorption = target.MagicFlatAbsorption,
            CriticalDamageFlatReduction =
                target.CriticalDamageFlatReduction,
            DamageReboundBasisPoints = target.DamageReboundBasisPoints,
            DamageReboundFlat = target.DamageReboundFlat
        };
}
