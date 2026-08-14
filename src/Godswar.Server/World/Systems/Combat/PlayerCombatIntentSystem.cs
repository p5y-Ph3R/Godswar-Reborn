using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Validates queued player actions, reserves player-owned resources, and emits
/// guarded monster-mutation intents. It never mutates monster state directly.
/// </summary>
internal sealed partial class PlayerCombatIntentSystem : IEcsSystem
{
    public const int SystemOrder = 500;
    private readonly Func<ulong>? _nextAdmittedCombatRevision;

    public PlayerCombatIntentSystem()
    {
    }

    public PlayerCombatIntentSystem(
        Func<ulong> nextAdmittedCombatRevision)
    {
        ArgumentNullException.ThrowIfNull(nextAdmittedCombatRevision);
        _nextAdmittedCombatRevision = nextAdmittedCombatRevision;
    }

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var player in context.World.Query<
                     PlayerCombatIdentityComponent,
                     PlayerCombatResourceComponent,
                     PlayerCombatIntentComponent>())
        {
            ref var resources = ref context.World
                .Get<PlayerCombatResourceComponent>(player);
            var intent = context.World
                .Get<PlayerCombatIntentComponent>(player);
            context.Commands.Remove<PlayerCombatIntentComponent>(player);

            if (!context.World.Has<PlayerCombatTransformComponent>(player) ||
                !context.World.Has<PlayerCombatOffenseComponent>(player))
            {
                Reject(
                    context,
                    player,
                    intent,
                    ref resources,
                    PlayerCombatRejectionReason.UnsupportedIntent);
                continue;
            }

            if (context.World.Has<PlayerCombatReservationComponent>(player))
            {
                Reject(
                    context,
                    player,
                    intent,
                    ref resources,
                    PlayerCombatRejectionReason.ReservationPending);
                continue;
            }

            if (resources.CurrentHp <= 0)
            {
                Reject(
                    context,
                    player,
                    intent,
                    ref resources,
                    PlayerCombatRejectionReason.SourceDead);
                continue;
            }

            var transform = context.World
                .Get<PlayerCombatTransformComponent>(player);
            var offense = context.World
                .Get<PlayerCombatOffenseComponent>(player);

            switch (intent.Kind)
            {
                case PlayerCombatIntentKind.BasicAttack:
                    ProcessBasicAttack(
                        context,
                        player,
                        intent,
                        transform,
                        offense,
                        ref resources);
                    break;
                case PlayerCombatIntentKind.SingleTargetSkill:
                    ProcessSingleTargetSkill(
                        context,
                        player,
                        intent,
                        transform,
                        offense,
                        ref resources);
                    break;
                case PlayerCombatIntentKind.AreaSkill:
                    ProcessAreaSkill(
                        context,
                        player,
                        intent,
                        transform,
                        offense,
                        ref resources);
                    break;
                default:
                    Reject(
                        context,
                        player,
                        intent,
                        ref resources,
                        PlayerCombatRejectionReason.UnsupportedIntent);
                    break;
            }
        }
    }

    private void ProcessBasicAttack(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatTransformComponent transform,
        in PlayerCombatOffenseComponent offense,
        ref PlayerCombatResourceComponent resources)
    {
        if (!TryFindSingleTarget(
                context.World,
                transform.MapId,
                intent.TargetObjectId,
                out var target))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.TargetUnavailable);
            return;
        }

        if (!TryValidateTargetGuard(
                intent,
                target,
                out var guardRejection))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                guardRejection);
            return;
        }

        if (!PlayerCombatRules.TryResolveBasicAttackPosition(
                transform.X,
                transform.Z,
                intent.ReportedAttackerX,
                intent.ReportedAttackerZ,
                out var attackX,
                out var attackZ))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.InvalidCoordinates);
            return;
        }

        if (!PlayerCombatRules.IsWithinBasicAttackRange(
                attackX,
                attackZ,
                target.X,
                target.Z,
                target.BasicAttackRange))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.OutOfRange);
            return;
        }

        if (intent.RequestedAt < resources.NextBasicAttackAt)
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.CooldownActive);
            return;
        }

        var admittedCombatRevision = NextAdmittedCombatRevision(
            resources);
        ResolveAndReserveBasicAttack(
            context,
            player,
            intent,
            offense,
            target,
            admittedCombatRevision,
            ref resources,
            offense.BasicAttackIntervalMilliseconds);
    }

    private void ProcessSingleTargetSkill(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatTransformComponent transform,
        in PlayerCombatOffenseComponent offense,
        ref PlayerCombatResourceComponent resources)
    {
        if (!PlayerCombatRules.IsHostileSingleTargetSkill(intent.Skill))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.UnsupportedIntent);
            return;
        }

        if (!TryFindSingleTarget(
                context.World,
                transform.MapId,
                intent.TargetObjectId,
                out var target))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.TargetUnavailable);
            return;
        }

        if (!TryValidateTargetGuard(
                intent,
                target,
                out var guardRejection))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                guardRejection);
            return;
        }

        if (!PlayerCombatRules.IsWithinSkillRange(
                transform.X,
                transform.Z,
                target.X,
                target.Z,
                intent.Skill))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.OutOfRange);
            return;
        }

        var manaCost = Math.Max(0, intent.Skill.ManaCost);
        if (resources.CurrentMp < manaCost)
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.InsufficientMana);
            return;
        }

        var admittedCombatRevision = NextAdmittedCombatRevision(
            resources);
        ResolveAndReserveSingleTargetSkill(
            context,
            player,
            intent,
            offense,
            target,
            admittedCombatRevision,
            ref resources,
            manaCost);
    }

    private void ProcessAreaSkill(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatIntentComponent intent,
        in PlayerCombatTransformComponent transform,
        in PlayerCombatOffenseComponent offense,
        ref PlayerCombatResourceComponent resources)
    {
        if (!PlayerCombatRules.IsHostileAreaSkill(intent.Skill))
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.UnsupportedIntent);
            return;
        }

        var areaCenterX = transform.X;
        var areaCenterZ = transform.Z;
        if (PlayerCombatRules.IsHostileGroundAreaSkill(intent.Skill))
        {
            if (!intent.HasReportedTargetPosition ||
                !float.IsFinite(intent.ReportedTargetX) ||
                !float.IsFinite(intent.ReportedTargetZ))
            {
                Reject(
                    context,
                    player,
                    intent,
                    ref resources,
                    PlayerCombatRejectionReason.InvalidCoordinates);
                return;
            }

            if (!PlayerCombatRules.IsWithinGroundTargetRange(
                    transform.X,
                    transform.Z,
                    intent.ReportedTargetX,
                    intent.ReportedTargetZ,
                    intent.Skill))
            {
                Reject(
                    context,
                    player,
                    intent,
                    ref resources,
                    PlayerCombatRejectionReason.OutOfRange);
                return;
            }

            areaCenterX = intent.ReportedTargetX;
            areaCenterZ = intent.ReportedTargetZ;
        }

        var manaCost = Math.Max(0, intent.Skill.ManaCost);
        if (resources.CurrentMp < manaCost)
        {
            Reject(
                context,
                player,
                intent,
                ref resources,
                PlayerCombatRejectionReason.InsufficientMana);
            return;
        }

        var admittedCombatRevision = NextAdmittedCombatRevision(
            resources);
        ResolveAndReserveAreaSkill(
            context,
            player,
            intent,
            transform.MapId,
            areaCenterX,
            areaCenterZ,
            offense,
            admittedCombatRevision,
            ref resources,
            manaCost);
    }

    private ulong NextAdmittedCombatRevision(
        in PlayerCombatResourceComponent resources) =>
        _nextAdmittedCombatRevision?.Invoke() ??
        checked(resources.CombatRevision + 1UL);
}
