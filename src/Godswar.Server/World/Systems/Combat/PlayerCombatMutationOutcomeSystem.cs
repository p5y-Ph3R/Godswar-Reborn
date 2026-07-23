using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Reconciles adapter-reported monster mutation outcomes. Single-target and
/// basic attacks refund reserved player resources when the guarded mutation is
/// rejected; area casts retain their reserved mana, matching the live cast.
/// </summary>
internal sealed class PlayerCombatMutationOutcomeSystem : IEcsSystem
{
    public const int SystemOrder = 600;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var player in context.World.Query<
                     PlayerCombatMutationOutcomeComponent>())
        {
            var outcome = context.World
                .Get<PlayerCombatMutationOutcomeComponent>(player);
            context.Commands
                .Remove<PlayerCombatMutationOutcomeComponent>(player);

            if (!context.World.Has<PlayerCombatResourceComponent>(player))
            {
                continue;
            }

            ref var resources = ref context.World
                .Get<PlayerCombatResourceComponent>(player);
            if (!context.World.Has<PlayerCombatReservationComponent>(player))
            {
                PublishIgnored(
                    context,
                    player,
                    outcome,
                    ref resources,
                    PlayerCombatMutationRejectionReason.OutcomeOutOfOrder);
                continue;
            }

            ref var reservation = ref context.World
                .Get<PlayerCombatReservationComponent>(player);
            if (!TryGetExpectedTarget(
                    reservation,
                    outcome,
                    out var expected,
                    out var correlationRejection))
            {
                PublishIgnored(
                    context,
                    player,
                    outcome,
                    ref resources,
                    correlationRejection);
                continue;
            }

            var rejection = ResolveMutationRejection(
                expected,
                outcome);
            if (rejection != PlayerCombatMutationRejectionReason.None)
            {
                ReconcileRejectedMutation(
                    context,
                    player,
                    expected,
                    rejection,
                    ref reservation,
                    ref resources);
                continue;
            }

            ReconcileCommittedMutation(
                context,
                player,
                expected,
                outcome,
                ref reservation,
                ref resources);
        }
    }

    private static bool TryGetExpectedTarget(
        in PlayerCombatReservationComponent reservation,
        in PlayerCombatMutationOutcomeComponent outcome,
        out PlayerCombatReservedTarget expected,
        out PlayerCombatMutationRejectionReason rejection)
    {
        expected = default;
        if (reservation.IntentId != outcome.IntentId ||
            reservation.NextOutcomeIndex < 0 ||
            reservation.NextOutcomeIndex >= reservation.Targets.Length)
        {
            rejection =
                PlayerCombatMutationRejectionReason.OutcomeOutOfOrder;
            return false;
        }

        expected = reservation.Targets[reservation.NextOutcomeIndex];
        if (outcome.TargetOrder != expected.TargetOrder ||
            outcome.TargetObjectId != expected.ObjectId)
        {
            rejection =
                PlayerCombatMutationRejectionReason.OutcomeOutOfOrder;
            return false;
        }

        rejection = PlayerCombatMutationRejectionReason.None;
        return true;
    }

    private static PlayerCombatMutationRejectionReason
        ResolveMutationRejection(
            in PlayerCombatReservedTarget expected,
            in PlayerCombatMutationOutcomeComponent outcome)
    {
        if (!outcome.Applied)
        {
            return outcome.RejectionReason ==
                   PlayerCombatMutationRejectionReason.None
                ? PlayerCombatMutationRejectionReason.TargetRejected
                : outcome.RejectionReason;
        }

        if (outcome.SpawnGeneration !=
            expected.ExpectedSpawnGeneration)
        {
            return PlayerCombatMutationRejectionReason.GenerationMismatch;
        }

        if (expected.ExpectedHealthRevision == ulong.MaxValue ||
            outcome.BeforeHealthRevision !=
                expected.ExpectedHealthRevision ||
            outcome.AfterHealthRevision !=
                expected.ExpectedHealthRevision + 1)
        {
            return PlayerCombatMutationRejectionReason.RevisionMismatch;
        }

        if (outcome.BeforeHealth != expected.BeforeHealth ||
            outcome.AfterHealth >= outcome.BeforeHealth ||
            outcome.AfterHealth !=
                (expected.RequestedDamage >= expected.BeforeHealth
                    ? 0
                    : expected.BeforeHealth - expected.RequestedDamage))
        {
            return PlayerCombatMutationRejectionReason.NoHealthChange;
        }

        var killed = outcome.AfterHealth == 0;
        if (outcome.Killed != killed)
        {
            return
                PlayerCombatMutationRejectionReason.InvalidDeathTransition;
        }

        return PlayerCombatMutationRejectionReason.None;
    }

    private static void ReconcileRejectedMutation(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatReservedTarget expected,
        PlayerCombatMutationRejectionReason rejection,
        ref PlayerCombatReservationComponent reservation,
        ref PlayerCombatResourceComponent resources)
    {
        reservation.RejectedTargetCount =
            checked(reservation.RejectedTargetCount + 1);
        reservation.NextOutcomeIndex =
            checked(reservation.NextOutcomeIndex + 1);
        context.Events.Publish(
            new PlayerCombatTargetMutationRejectedEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                player,
                reservation.IntentId,
                reservation.Kind,
                expected.ObjectId,
                expected.ExpectedSpawnGeneration,
                expected.ExpectedHealthRevision,
                rejection,
                reservation.RefundOnRejectedTarget));

        if (reservation.RefundOnRejectedTarget)
        {
            PlayerCombatIntentSystem.RefundResources(
                reservation.ReservedMana,
                reservation.PreviousNextBasicAttackAt,
                ref resources);
            context.Events.Publish(
                new PlayerCombatResourceRefundedEvent(
                    PlayerCombatIntentSystem.NextSequence(ref resources),
                    player,
                    reservation.IntentId,
                    reservation.ReservedMana,
                    resources.CurrentMp,
                    resources.VitalsRevision,
                    resources.NextBasicAttackAt,
                    resources.CombatRevision));
            Complete(
                context,
                player,
                reservation,
                ref resources,
                resourcesRefunded: true);
            return;
        }

        if (reservation.NextOutcomeIndex == reservation.Targets.Length)
        {
            Complete(
                context,
                player,
                reservation,
                ref resources,
                resourcesRefunded: false);
        }
    }

    private static void ReconcileCommittedMutation(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatReservedTarget expected,
        in PlayerCombatMutationOutcomeComponent outcome,
        ref PlayerCombatReservationComponent reservation,
        ref PlayerCombatResourceComponent resources)
    {
        var appliedDamage = outcome.BeforeHealth - outcome.AfterHealth;
        context.Events.Publish(
            new PlayerCombatTargetMutationCommittedEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                player,
                reservation.IntentId,
                reservation.Kind,
                reservation.SkillId,
                expected.TargetOrder,
                expected.ObjectId,
                expected.RequestedDamage,
                appliedDamage,
                outcome.BeforeHealth,
                outcome.AfterHealth,
                outcome.SpawnGeneration,
                outcome.BeforeHealthRevision,
                outcome.AfterHealthRevision,
                outcome.Killed));

        reservation.AcceptedTargetCount =
            checked(reservation.AcceptedTargetCount + 1);
        reservation.NextOutcomeIndex =
            checked(reservation.NextOutcomeIndex + 1);
        if (outcome.Killed &&
            context.World.Has<PlayerCombatKillLedgerComponent>(player) &&
            context.World.Has<PlayerCombatIdentityComponent>(player))
        {
            var guard = new PlayerCombatKillGuard(
                reservation.IntentId,
                outcome.TargetObjectId,
                outcome.SpawnGeneration,
                outcome.AfterHealthRevision);
            ref var ledger = ref context.World
                .Get<PlayerCombatKillLedgerComponent>(player);
            ledger.Add(guard);
            var identity = context.World
                .Get<PlayerCombatIdentityComponent>(player);
            context.Events.Publish(
                new MonsterKilledByPlayerCombatEvent(
                    PlayerCombatIntentSystem.NextSequence(ref resources),
                    player,
                    identity.CharacterId,
                    guard.CombatIntentId,
                    guard.MonsterObjectId,
                    guard.MonsterSpawnGeneration,
                    guard.MonsterHealthRevision));
        }

        if (reservation.NextOutcomeIndex == reservation.Targets.Length)
        {
            Complete(
                context,
                player,
                reservation,
                ref resources,
                resourcesRefunded: false);
        }
    }

    private static void Complete(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatReservationComponent reservation,
        ref PlayerCombatResourceComponent resources,
        bool resourcesRefunded)
    {
        context.Events.Publish(
            new PlayerCombatReservationCompletedEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                player,
                reservation.IntentId,
                reservation.Kind,
                reservation.AcceptedTargetCount,
                reservation.RejectedTargetCount,
                resourcesRefunded));
        context.Commands
            .Remove<PlayerCombatReservationComponent>(player);
    }

    private static void PublishIgnored(
        EcsSystemContext context,
        EntityId player,
        in PlayerCombatMutationOutcomeComponent outcome,
        ref PlayerCombatResourceComponent resources,
        PlayerCombatMutationRejectionReason rejection)
    {
        context.Events.Publish(
            new PlayerCombatMutationOutcomeIgnoredEvent(
                PlayerCombatIntentSystem.NextSequence(ref resources),
                player,
                outcome.IntentId,
                outcome.TargetObjectId,
                rejection));
    }
}
