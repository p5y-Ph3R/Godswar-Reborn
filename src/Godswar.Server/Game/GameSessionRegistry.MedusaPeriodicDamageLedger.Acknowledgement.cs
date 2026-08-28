using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    private sealed class OwnerAcknowledgementAuthority
        : MedusaPeriodicDamageOwnerAcknowledgementAuthority
    {
        internal OwnerAcknowledgementAuthority(
            MedusaPeriodicDamageLedger owner,
            Entry entry,
            MedusaPreparedPeriodicDamageOwnerReceipt receipt,
            in MedusaPeriodicDamageHpCommitEvidence hpCommit)
        {
            Owner = owner;
            Entry = entry;
            Reservation = entry.Reservation;
            Receipt = receipt;
            AttackEventId = entry.AttackEventId;
            HpCommit = hpCommit;
        }

        internal MedusaPeriodicDamageLedger Owner { get; }

        internal Entry Entry { get; }

        internal MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            Reservation { get; }

        internal MedusaPreparedPeriodicDamageOwnerReceipt Receipt { get; }

        internal ulong AttackEventId { get; }

        internal MedusaPeriodicDamageHpCommitEvidence HpCommit { get; }

        internal bool Claimed { get; set; }

        internal override bool TryClaim(
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
                reservation,
            MedusaPreparedPeriodicDamageOwnerReceipt receipt) =>
            Owner.TryClaimOwnerAcknowledgement(
                this,
                reservation,
                receipt);
    }

    internal bool TryGetOwnerAcknowledgementAuthority(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaPeriodicDamageOwnerAcknowledgementAuthority authority)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                authority = null!;
                return false;
            }

            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.HPCommitted ||
                entry.HpCommit is not { } hpCommit ||
                !hpCommit.IsValid ||
                !MatchesHpCommit(entry, hpCommit))
            {
                authority = null!;
                return false;
            }

            entry.PendingOwnerAcknowledgement ??= new(
                this,
                entry,
                entry.OwnerReceipt,
                hpCommit);
            authority = entry.PendingOwnerAcknowledgement;
            return true;
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome MarkOwnerAcked(
        MedusaPeriodicDamageLedgerHandle? handle,
        MedusaPeriodicDamageOwnerAcknowledgementAuthority? authority,
        in MedusaPeriodicDamageOwnerReconcileResult reconciliation)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (!IsOwnerAcknowledgementBoundLocked(entry, authority) ||
                authority is not OwnerAcknowledgementAuthority
                    { Claimed: true })
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (entry.Phase ==
                MedusaPeriodicDamageLedgerPhase.PostHpQuarantined)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.Quarantined;
            }
            if (entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault &&
                MatchesOwnerWrittenReconciliation(
                    entry,
                    reconciliation) &&
                reconciliation.ActualDisposition ==
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }
            if ((entry.Phase is
                    MedusaPeriodicDamageLedgerPhase.OwnerAcked or
                    MedusaPeriodicDamageLedgerPhase.Published) &&
                MatchesExpectedOwnerAck(entry, reconciliation))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.HPCommitted)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (!MatchesOwnerWrittenReconciliation(
                    entry,
                    reconciliation) ||
                reconciliation.ActualDisposition is not { } actual)
            {
                entry.Phase =
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined;
                return MedusaPeriodicDamageLedgerMutationOutcome.Quarantined;
            }
            if (actual ==
                MedusaPeriodicDamageDispositionOutcome.InvariantFault)
            {
                entry.ActualOwnerDisposition = actual;
                entry.Phase =
                    MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault;
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .OwnerInvariantFault;
            }
            if (actual != DispositionFor(entry.RequestedIntent))
            {
                entry.Phase =
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined;
                return MedusaPeriodicDamageLedgerMutationOutcome.Quarantined;
            }

            entry.ActualOwnerDisposition = actual;
            entry.Phase = MedusaPeriodicDamageLedgerPhase.OwnerAcked;
            return MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked;
        }
    }

    private bool TryClaimOwnerAcknowledgement(
        OwnerAcknowledgementAuthority authority,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(authority.Entry, out var entry))
            {
                return false;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.HPCommitted ||
                !IsOwnerAcknowledgementBoundLocked(entry, authority) ||
                !ReferenceEquals(authority.Reservation, reservation) ||
                !ReferenceEquals(authority.Receipt, receipt))
            {
                return false;
            }

            authority.Claimed = true;
            return true;
        }
    }

    private bool IsOwnerAcknowledgementBoundLocked(
        Entry entry,
        MedusaPeriodicDamageOwnerAcknowledgementAuthority? authority) =>
        authority is OwnerAcknowledgementAuthority exact &&
        ReferenceEquals(exact.Owner, this) &&
        ReferenceEquals(exact.Entry, entry) &&
        ReferenceEquals(entry.PendingOwnerAcknowledgement, exact) &&
        ReferenceEquals(exact.Reservation, entry.Reservation) &&
        ReferenceEquals(exact.Receipt, entry.OwnerReceipt) &&
        exact.AttackEventId == entry.AttackEventId &&
        entry.HpCommit is { } hpCommit &&
        hpCommit == exact.HpCommit &&
        hpCommit.IsValid &&
        MatchesHpCommit(entry, hpCommit);
}
