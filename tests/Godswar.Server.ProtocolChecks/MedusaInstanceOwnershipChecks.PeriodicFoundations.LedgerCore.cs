using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicLedgerPreparationAndAcknowledgement()
    {
        CheckPeriodicLedgerFullIdentityCollision();

        var prepared = PrepareSimplePeriodicFoundation(attackEventId: 701);
        var ledger = new MedusaPeriodicDamageLedger(2);
        var handle = PrepareLedgerEntry(ledger, prepared);
        Check.True(
            ledger.TryPrepare(
                prepared.Reservation,
                prepared.Target,
                prepared.AttackEventId,
                prepared.Receipt,
                prepared.Recipients,
                out var replayHandle) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent &&
            ReferenceEquals(handle, replayHandle),
            "an exact full-tuple ledger prepare reacquires the retained handle");

        Check.True(
            prepared.Map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                prepared.Reservation,
                prepared.AttackEventId,
                MedusaPeriodicDamageOwnerIntent.Applied,
                out var ownerReplay) &&
            ownerReplay.Outcome ==
                MedusaPeriodicDamageOwnerPrepareOutcome.AlreadyPrepared &&
            ReferenceEquals(ownerReplay.Receipt, prepared.Receipt) &&
            prepared.Map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                prepared.Reservation,
                prepared.AttackEventId,
                MedusaPeriodicDamageOwnerIntent.Terminal,
                out var conflictingIntent) &&
            conflictingIntent.Outcome ==
                MedusaPeriodicDamageOwnerPrepareOutcome
                    .ConflictingPreparation &&
            prepared.Map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                prepared.Reservation,
                prepared.AttackEventId + 1,
                MedusaPeriodicDamageOwnerIntent.Applied,
                out var missingRefresh) &&
            missingRefresh.Outcome ==
                MedusaPeriodicDamageOwnerPrepareOutcome
                    .RefreshAuthorityRequired &&
            prepared.Map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                prepared.Reservation,
                prepared.AttackEventId - 1,
                MedusaPeriodicDamageOwnerIntent.Applied,
                out var staleEvent) &&
            staleEvent.Outcome ==
                MedusaPeriodicDamageOwnerPrepareOutcome.NonMonotonicEvent,
            "owner preparation replays exactly and rejects conflicting intent, unproved refresh, and nonmonotonic events");

        var before = RequiredOwnership(prepared.Map);
        Check.True(
            prepared.Map.TryReconcileMedusaPeriodicDamageOwnerReceipt(
                prepared.Receipt,
                acknowledgementAuthority: null,
                out var unprovedReconciliation) &&
            unprovedReconciliation.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.ForeignReservation &&
            prepared.Map.TryAbortPreparedMedusaPeriodicDamageOwnerReceipt(
                prepared.Receipt,
                abortAuthority: null,
                out var unprovedAbort) &&
            unprovedAbort.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.ForeignReservation &&
            prepared.Reservation.State ==
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Pending,
            "receipt-only pre-HP reconciliation and abort are rejected");
        var unchanged = RequiredOwnership(prepared.Map);
        Check.True(
            unchanged.Run.LastObservedAt == before.Run.LastObservedAt &&
            unchanged.Run.State == before.Run.State &&
            unchanged.Mechanics.LastObservedAt ==
                before.Mechanics.LastObservedAt &&
            unchanged.Mechanics.Characters.Single().ActiveEffects.Single()
                .EmittedPeriodicTicks ==
                before.Mechanics.Characters.Single().ActiveEffects.Single()
                    .EmittedPeriodicTicks,
            "missing consume authority causes zero owner clock or effect mutation");

        Check.True(
            ledger.TryGetPreparedAttempt(
                handle,
                out var exactTarget,
                out var exactEventId,
                out var hpObserver) &&
            exactTarget == prepared.Target &&
            exactEventId == prepared.AttackEventId,
            "only the Prepared phase exposes the exact HP attempt capability");
#if DEBUG
        ledger.ProtocolCheckBeforeHpCommitTransition = () =>
            throw new InvalidOperationException(
                "simulated post-HP callback fault");
#endif
        var evidence = PeriodicFoundationHpEvidence(prepared);
        hpObserver.MarkHpCommitted(evidence);
#if DEBUG
        ledger.ProtocolCheckBeforeHpCommitTransition = null;
#endif
        Check.True(
            ledger.TryGetRetained(
                prepared.Reservation.Identity.WorldInstanceId,
                out var retained,
                out var hpCommitted) &&
            ReferenceEquals(retained, handle) &&
            hpCommitted.Phase ==
                MedusaPeriodicDamageLedgerPhase.HPCommitted &&
            hpCommitted.HpCommit == evidence &&
            !ledger.TryGetPreparedAttempt(
                handle,
                out _,
                out _,
                out _),
            "the base HP marker survives callback failure and permanently removes pre-HP capability access");

        MedusaPeriodicDamageOwnerAcknowledgementAuthority reacquired = null!;
        Check.True(
            ledger.TryGetOwnerAcknowledgementAuthority(
                handle,
                out var acknowledgement) &&
            ledger.TryGetOwnerAcknowledgementAuthority(
                handle,
                out reacquired) &&
            ReferenceEquals(acknowledgement, reacquired),
            "the exact HPCommitted acknowledgement authority is reacquirable after a lost result");
        MedusaPeriodicDamageOwnerReconcileResult exactReplay = default;
        Check.True(
            prepared.Map.TryReconcileMedusaPeriodicDamageOwnerReceipt(
                prepared.Receipt,
                acknowledgement,
                out var first) &&
            first.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.Applied &&
            first.IsAuthoritativeApplied &&
            prepared.Map.TryReconcileMedusaPeriodicDamageOwnerReceipt(
                prepared.Receipt,
                reacquired,
                out exactReplay) &&
            exactReplay.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            exactReplay.ActualDisposition ==
                MedusaPeriodicDamageDispositionOutcome.Applied &&
            exactReplay.IsAuthoritativeApplied &&
            prepared.Reservation.State ==
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Applied,
            "HPCommitted authority consumes once and a lost-result retry reads the owner-written Applied disposition");
        Check.True(
            ledger.MarkOwnerAcked(handle, acknowledgement, first) ==
                MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked &&
            ledger.MarkOwnerAcked(handle, reacquired, exactReplay) ==
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent,
            "ledger acknowledgement records the exact owner disposition once");
    }

    private static void CheckPeriodicLedgerFullIdentityCollision()
    {
        var first = PrepareSimplePeriodicFoundation(attackEventId: 601);
        var ledger = new MedusaPeriodicDamageLedger(2);
        _ = PrepareLedgerEntry(ledger, first);

        var descriptor = WorldInstanceDescriptor.Create(
            RealmId.Tempest,
            first.Map.WorldInstanceId,
            new Godswar.Server.Domain.World.Instances.MapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            StartedAt);
        var collisionMap = new MapInstance(descriptor);
        var bound = Bind(
            collisionMap,
            MedusaEncounterDifficulty.Enhanced,
            characters: [202]).Snapshot!;
        var bleed = Binding(bound, "Chrysaor");
        MedusaOwnedClockResult due = default;
        Check.True(
            collisionMap.TryCommitOwnerMechanicForInvariantTest(
                202,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out _) &&
            collisionMap.TryObserveMedusaTime(
                StartedAt.AddSeconds(3),
                out due) &&
            due.MechanicsResult?.PeriodicDamage is { } collision,
            "identity collision fixture reserves a second authored tick");
        var reservation = due.MechanicsResult!.Value.PeriodicDamage!;
        var identity = reservation.Identity;
        var target = new MedusaPeriodicDamageTargetCapture(
            new(
                identity.WorldInstanceId,
                WorldRevision: 0,
                identity.TargetOwnership,
                identity.TargetCharacterId,
                ObjectId: 2,
                identity.TargetLifeRevision,
                VitalsRevision: 0,
                identity.TargetWorldMembershipEpoch),
            CurrentHealth: 1_000_000);
        var receipt = PreparePeriodicFoundationOwnerReceipt(
            collisionMap,
            reservation,
            attackEventId: 602,
            MedusaPeriodicDamageOwnerIntent.Applied);
        Check.True(
            identity.WorldInstanceId ==
                first.Reservation.Identity.WorldInstanceId &&
            identity != first.Reservation.Identity &&
            ledger.TryPrepare(
                reservation,
                target,
                attackEventId: 602,
                receipt,
                Array.Empty<MedusaPeriodicDamageRecipientIdentity>(),
                out var collisionHandle) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .IdentityMismatch &&
            collisionHandle is null,
            "one world slot rejects a value-colliding but nonidentical full periodic identity");
    }
}
