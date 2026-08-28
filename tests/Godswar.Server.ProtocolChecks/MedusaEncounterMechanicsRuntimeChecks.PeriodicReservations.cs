using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaEncounterMechanicsRuntimeChecks
{
    private static void CheckPeriodicReservationDispositions()
    {
        var runtime = CreateRuntime();
        var source = Source(runtime, "Chrysaor");
        var start = runtime.StartedAt;
        var effect = Hit(runtime, source, 101, start.AddSeconds(1))
            .Effect!.Value;
        var reserved = runtime.ObserveTime(start.AddSeconds(3));
        var capability = reserved.PeriodicDamage!;
        var identity = capability.Identity;
        var reacquired = runtime.ReservePeriodicDamage(
            start.AddSeconds(30));

        Check.True(
            reserved.Outcome ==
                MedusaMechanicsClockOutcome.PeriodicDamageRequired &&
            ReferenceEquals(capability, reacquired.Reservation) &&
            identity.WorldInstanceId == runtime.WorldInstanceId &&
            identity.TargetCharacterId == 101 &&
            identity.TargetOwnership == effect.TargetOwnership &&
            identity.TargetLifeRevision == effect.TargetLifeRevision &&
            identity.TargetWorldMembershipEpoch ==
                effect.TargetWorldMembershipEpoch &&
            identity.SourceRosterSpawnId == source.RosterSpawnId &&
            identity.SourceObjectId == source.ObjectId &&
            identity.SourceSpawnGeneration == source.SpawnGeneration &&
            identity.ApplicationSequence == effect.ApplicationSequence &&
            identity.TickNumber == 1 &&
            identity.DueAt == start.AddSeconds(3) &&
            identity.DueAt.Offset == TimeSpan.Zero &&
            identity.DamageKind ==
                MedusaPeriodicDamageKind.DirectHealthLoss &&
            identity.Damage == 200 &&
            runtime.Snapshot().OutstandingPeriodicDamage == identity &&
            runtime.Snapshot().LastObservedAt == start.AddSeconds(1),
            "one reservation retains the complete exact tick identity");

        Check.True(
            runtime.CancelPeriodicDamage(capability) ==
                MedusaPeriodicDamageDispositionOutcome.Canceled &&
            runtime.CancelPeriodicDamage(capability) ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            runtime.Snapshot().OutstandingPeriodicDamage is null &&
            runtime.Snapshot().LastObservedAt == start.AddSeconds(1),
            "cancel is exact, non-consuming, and duplicate-safe");

        var offeredAgain = runtime.ReservePeriodicDamage(
            start.AddSeconds(3)).Reservation!;
        var foreign = CreateRuntime();
        Check.True(
            offeredAgain.Identity == identity &&
            !ReferenceEquals(offeredAgain, capability) &&
            foreign.CompletePeriodicDamageApplied(offeredAgain) ==
                MedusaPeriodicDamageDispositionOutcome
                    .ForeignReservation &&
            runtime.CompletePeriodicDamageApplied(offeredAgain) ==
                MedusaPeriodicDamageDispositionOutcome.Applied &&
            runtime.CompletePeriodicDamageApplied(offeredAgain) ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            runtime.CompletePeriodicDamageTerminal(offeredAgain) ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            runtime.CancelPeriodicDamage(offeredAgain) ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            runtime.Snapshot().LastObservedAt == start.AddSeconds(3) &&
            Character(runtime.Snapshot(), 101).ActiveEffects.Single() is
            {
                EmittedPeriodicTicks: 1,
                NextPeriodicTickAt: var next
            } &&
            next == start.AddSeconds(5),
            "applied disposition consumes once; foreign and duplicates do not mutate");

        var terminal = runtime.ReservePeriodicDamage(
            start.AddSeconds(5)).Reservation!;
        Check.True(
            runtime.CompletePeriodicDamageTerminal(terminal) ==
                MedusaPeriodicDamageDispositionOutcome.Terminal &&
            runtime.CompletePeriodicDamageTerminal(terminal) ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            Character(runtime.Snapshot(), 101).ActiveEffects.IsEmpty,
            "terminal disposition consumes only the exact application once");
    }

    private static void CheckPeriodicMutatorBarriers()
    {
        var runtime = CreateRuntime([101, 102]);
        var bleed = Source(runtime, "Chrysaor");
        var stun = Source(runtime, "E1-Elite");
        var start = runtime.StartedAt;
        var effect = Hit(runtime, bleed, 101, start.AddSeconds(1))
            .Effect!.Value;
        var snapshot = runtime.CaptureMonsterHitTransactionSnapshot();
        var pending = runtime.ObserveTime(start.AddSeconds(3))
            .PeriodicDamage!;

        Check.Throws<InvalidOperationException>(
            () => runtime.CaptureMonsterHitTransactionSnapshot(),
            "a transaction snapshot cannot omit outstanding periodic work");
        Check.Throws<InvalidOperationException>(
            () => runtime.RestoreMonsterHitTransactionSnapshot(snapshot),
            "a transaction restore cannot replace a reserved effect object");

        var blockedHit = runtime.CommitMonsterHit(
            102,
            stun.ObjectId,
            stun.SpawnGeneration,
            start.AddSeconds(3));
        var blockedRetire = runtime.RetireMonster(
            bleed.ObjectId,
            bleed.SpawnGeneration,
            start.AddSeconds(3));
        var blockedClear = runtime.ClearCharacterLife(
            101,
            effect.TargetOwnership,
            effect.TargetLifeRevision,
            effect.TargetWorldMembershipEpoch,
            start.AddSeconds(3));
        Check.True(
            blockedHit.Outcome ==
                MedusaMechanicHitOutcome.PeriodicDamageRequired &&
            ReferenceEquals(blockedHit.PeriodicDamage, pending) &&
            blockedRetire.Outcome ==
                MedusaMechanicSourceRetireOutcome
                    .PeriodicDamageRequired &&
            ReferenceEquals(blockedRetire.PeriodicDamage, pending) &&
            blockedClear.Outcome ==
                MedusaPeriodicDamageReserveOutcome.Reserved &&
            ReferenceEquals(blockedClear.Reservation, pending) &&
            Character(runtime.Snapshot(), 101).ActiveEffects.Length == 1 &&
            Character(runtime.Snapshot(), 102).ActiveEffects.IsEmpty,
            "hit, retire, snapshot, and life clear hand off one pending capability");

        _ = runtime.CancelPeriodicDamage(pending);
        var unreservedClear = runtime.ClearCharacterLife(
            101,
            effect.TargetOwnership,
            effect.TargetLifeRevision,
            effect.TargetWorldMembershipEpoch,
            start.AddSeconds(3));
        Check.True(
            unreservedClear.Outcome ==
                MedusaPeriodicDamageReserveOutcome.Reserved &&
            unreservedClear.Reservation?.Identity == pending.Identity &&
            Character(runtime.Snapshot(), 101).ActiveEffects.Length == 1,
            "an unreserved due tick blocks exact-life clear without loss");
        _ = runtime.CompletePeriodicDamageTerminal(
            unreservedClear.Reservation);
    }

    private static void CheckPeriodicDeadlineBoundaries()
    {
        var runtime = CreateRuntime();
        var source = Source(runtime, "Chrysaor");
        var deadline = runtime.Deadline;
        _ = Hit(runtime, source, 101, deadline.AddSeconds(-3));
        var beforeDeadline = runtime.ObserveTime(
            deadline.AddSeconds(-1)).PeriodicDamage!;
        Check.True(
            beforeDeadline.Identity.DueAt == deadline.AddSeconds(-1) &&
            runtime.CompletePeriodicDamageApplied(beforeDeadline) ==
                MedusaPeriodicDamageDispositionOutcome.Applied,
            "the final strictly pre-deadline tick remains eligible");
        var boundary = runtime.ObserveTime(deadline);
        Check.True(
            boundary.Outcome ==
                MedusaMechanicsClockOutcome.DeadlineBoundaryUnresolved &&
            boundary.PeriodicDamage is null &&
            runtime.Snapshot().LastObservedAt == deadline,
            "deadline equality advances the clock but never creates a tick");
        var after = runtime.ObserveTime(deadline.AddTicks(1));
        Check.True(
            after.Outcome == MedusaMechanicsClockOutcome.Advanced &&
            after.PeriodicDamage is null &&
            Character(runtime.Snapshot(), 101).ActiveEffects.IsEmpty,
            "post-deadline reconciliation skips equality/later ticks");

        var exact = CreateRuntime();
        var exactSource = Source(exact, "Chrysaor");
        _ = Hit(exact, exactSource, 101, exact.Deadline.AddSeconds(-2));
        Check.True(
            exact.ObserveTime(exact.Deadline) is
            {
                Outcome: MedusaMechanicsClockOutcome
                    .DeadlineBoundaryUnresolved,
                PeriodicDamage: null
            },
            "a first tick exactly at the deadline is never eligible");

        var jump = CreateRuntime();
        var jumpSource = Source(jump, "Chrysaor");
        _ = Hit(jump, jumpSource, 101, jump.Deadline.AddSeconds(-5));
        var first = jump.ObserveTime(jump.Deadline.AddSeconds(1))
            .PeriodicDamage!;
        _ = jump.CompletePeriodicDamageApplied(first);
        var second = jump.ObserveTime(jump.Deadline.AddSeconds(1))
            .PeriodicDamage!;
        _ = jump.CompletePeriodicDamageApplied(second);
        var terminal = jump.ObserveTime(jump.Deadline.AddSeconds(1));
        Check.True(
            first.Identity.DueAt == jump.Deadline.AddSeconds(-3) &&
            second.Identity.DueAt == jump.Deadline.AddSeconds(-1) &&
            terminal.Outcome == MedusaMechanicsClockOutcome.Advanced &&
            terminal.PeriodicDamage is null,
            "a post-deadline jump drains only overdue pre-deadline work in order");
    }
}
