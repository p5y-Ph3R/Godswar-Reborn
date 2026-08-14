using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Validates and applies one already-resolved monster hit to typed player
/// vitals. Transport, mitigation lookup, persistence, and aggro remain outside
/// the ECS world.
/// </summary>
internal sealed class MonsterPlayerDamageSystem : IEcsSystem
{
    public const int SystemOrder = 500;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var player in context.World.Query<
                     PlayerIdentityComponent,
                     PlayerVitalsComponent,
                     MonsterPlayerDamageStateComponent>())
        {
            if (!context.World.Has<
                    MonsterPlayerDamageIntentComponent>(player))
            {
                continue;
            }

            var intent = context.World.Get<
                MonsterPlayerDamageIntentComponent>(player);
            context.Commands.Remove<
                MonsterPlayerDamageIntentComponent>(player);
            var identity = context.World.Get<
                PlayerIdentityComponent>(player);
            ref var vitals = ref context.World.Get<
                PlayerVitalsComponent>(player);
            ref var state = ref context.World.Get<
                MonsterPlayerDamageStateComponent>(player);

            if (identity.CharacterId !=
                    intent.ExpectedCharacterId ||
                identity.ObjectId !=
                    intent.ExpectedPlayerObjectId)
            {
                Reject(
                    context,
                    player,
                    intent,
                    vitals,
                    ref state,
                    MonsterPlayerDamageRejectionReason
                        .IdentityMismatch);
                continue;
            }

            if (state.LifeRevision !=
                intent.ExpectedLifeRevision)
            {
                Reject(
                    context,
                    player,
                    intent,
                    vitals,
                    ref state,
                    MonsterPlayerDamageRejectionReason
                        .LifeRevisionMismatch);
                continue;
            }

            if (intent.AttackEventId != 0)
            {
                if (intent.AttackEventId ==
                    state.LastAttackEventId)
                {
                    Reject(
                        context,
                        player,
                        intent,
                        vitals,
                        ref state,
                        MonsterPlayerDamageRejectionReason
                            .DuplicateAttackEvent);
                    continue;
                }

                if (intent.AttackEventId <
                    state.LastAttackEventId)
                {
                    Reject(
                        context,
                        player,
                        intent,
                        vitals,
                        ref state,
                        MonsterPlayerDamageRejectionReason
                            .StaleAttackEvent);
                    continue;
                }

                state.LastAttackEventId =
                    intent.AttackEventId;
            }

            if (vitals.Revision !=
                intent.ExpectedVitalsRevision)
            {
                Reject(
                    context,
                    player,
                    intent,
                    vitals,
                    ref state,
                    MonsterPlayerDamageRejectionReason
                        .VitalsRevisionMismatch);
                continue;
            }

            if (vitals.CurrentHp <= 0)
            {
                Reject(
                    context,
                    player,
                    intent,
                    vitals,
                    ref state,
                    MonsterPlayerDamageRejectionReason
                        .PlayerAlreadyDead);
                continue;
            }

            if (intent.ResolvedDamage == 0)
            {
                Reject(
                    context,
                    player,
                    intent,
                    vitals,
                    ref state,
                    MonsterPlayerDamageRejectionReason.ZeroDamage);
                continue;
            }

            Apply(
                context,
                player,
                identity,
                intent,
                ref vitals,
                ref state);
        }
    }

    private static void Apply(
        EcsSystemContext context,
        EntityId player,
        in PlayerIdentityComponent identity,
        in MonsterPlayerDamageIntentComponent intent,
        ref PlayerVitalsComponent vitals,
        ref MonsterPlayerDamageStateComponent state)
    {
        var beforeHealth = vitals.CurrentHp;
        var beforeVitalsRevision = vitals.Revision;
        var beforeLifeRevision = state.LifeRevision;
        var appliedDamage = (uint)Math.Min(
            (ulong)intent.ResolvedDamage,
            (ulong)beforeHealth);
        vitals.CurrentHp =
            beforeHealth - checked((int)appliedDamage);
        vitals.Revision = checked(vitals.Revision + 1);
        var killed = vitals.CurrentHp == 0;
        if (killed)
        {
            state.LifeRevision =
                checked(state.LifeRevision + 1);
        }

        var decisionSequence = NextDecisionSequence(ref state);
        context.Events.Publish(
            new MonsterPlayerDamageAppliedEvent(
                decisionSequence,
                player,
                intent.AttackEventId,
                intent.MonsterObjectId,
                intent.MonsterSpawnGeneration,
                intent.ResolvedDamage,
                appliedDamage,
                beforeHealth,
                vitals.CurrentHp,
                beforeVitalsRevision,
                vitals.Revision,
                beforeLifeRevision,
                state.LifeRevision,
                killed,
                intent.ResolvedAt));
        if (killed)
        {
            context.Events.Publish(
                new MonsterPlayerDeathDecisionEvent(
                    decisionSequence,
                    player,
                    intent.AttackEventId,
                    intent.MonsterObjectId,
                    identity.CharacterId,
                    identity.ObjectId,
                    beforeLifeRevision,
                    state.LifeRevision,
                    vitals.Revision));
        }
    }

    private static void Reject(
        EcsSystemContext context,
        EntityId player,
        in MonsterPlayerDamageIntentComponent intent,
        in PlayerVitalsComponent vitals,
        ref MonsterPlayerDamageStateComponent state,
        MonsterPlayerDamageRejectionReason reason)
    {
        context.Events.Publish(
            new MonsterPlayerDamageRejectedEvent(
                NextDecisionSequence(ref state),
                player,
                intent.AttackEventId,
                intent.MonsterObjectId,
                intent.MonsterSpawnGeneration,
                reason,
                vitals.CurrentHp,
                vitals.Revision,
                state.LifeRevision,
                state.LastAttackEventId));
    }

    private static ulong NextDecisionSequence(
        ref MonsterPlayerDamageStateComponent state)
    {
        state.DecisionSequence =
            checked(state.DecisionSequence + 1);
        return state.DecisionSequence;
    }
}
