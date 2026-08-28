using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    private sealed class PreparedAbortAuthority
        : MedusaPeriodicDamagePreparedAbortAuthority
    {
        internal PreparedAbortAuthority(
            MedusaPeriodicDamageLedger owner,
            Entry entry,
            MedusaPreparedPeriodicDamageOwnerReceipt receipt,
            MedusaPeriodicDamagePreparedAbortReason reason)
        {
            Owner = owner;
            Entry = entry;
            Reservation = entry.Reservation;
            Receipt = receipt;
            Identity = entry.Identity;
            Reason = reason;
        }

        internal MedusaPeriodicDamageLedger Owner { get; }

        internal Entry Entry { get; }

        internal MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            Reservation { get; }

        internal MedusaPreparedPeriodicDamageOwnerReceipt Receipt { get; }

        internal MedusaPeriodicDamageIdentity Identity { get; }

        internal override MedusaPeriodicDamagePreparedAbortReason Reason
            { get; }

        internal bool Claimed { get; set; }

        internal override bool TryClaim(
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
                reservation,
            MedusaPreparedPeriodicDamageOwnerReceipt receipt) =>
            Owner.TryClaimPreparedAbort(this, reservation, receipt);
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        TryCreatePreparedAbortAuthority(
            MedusaPeriodicDamageLedgerHandle? handle,
            MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
            out MedusaPeriodicDamagePreparedAbortAuthority? authority)
    {
        authority = null;
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase !=
                MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault)
            {
                return entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined
                    ? MedusaPeriodicDamageLedgerMutationOutcome.Quarantined
                    : MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            var reason = entry.PreparationAttempt >=
                MaximumPreparationAttempts
                ? MedusaPeriodicDamagePreparedAbortReason.AttemptsExhausted
                : MedusaPeriodicDamagePreparedAbortReason
                    .PreHpInvariantFault;
            return TryCreatePreparedAbortAuthorityLocked(
                entry,
                receipt,
                reason,
                out authority);
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome RetireWorld(
        MedusaRuntimeRetirePermit? retirement,
        MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
        out MedusaPeriodicDamagePreparedAbortAuthority? abortAuthority)
    {
        abortAuthority = null;
        if (retirement is null)
        {
            return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(
                    retirement.WorldInstanceId,
                    out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            switch (entry.Phase)
            {
                case MedusaPeriodicDamageLedgerPhase.Prepared:
                case MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault:
                    return TryCreatePreparedAbortAuthorityLocked(
                        entry,
                        receipt,
                        MedusaPeriodicDamagePreparedAbortReason
                            .RuntimeRetirement,
                        out abortAuthority);
                case MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault:
                    return MedusaPeriodicDamageLedgerMutationOutcome
                        .InvariantSettlementRequired;
                default:
                    // HPCommitted, OwnerAcked, unresolved quarantine, and
                    // Published-with-persistence-pending are never evicted.
                    return MedusaPeriodicDamageLedgerMutationOutcome
                        .ReconciliationRequired;
            }
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkPreparedOwnerAborted(
            MedusaPeriodicDamageLedgerHandle? handle,
            MedusaPeriodicDamagePreparedAbortAuthority? authority,
            in MedusaPeriodicDamageOwnerReconcileResult reconciliation)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault &&
                IsPreparedAbortBoundLocked(entry, authority) &&
                authority is PreparedAbortAuthority { Claimed: true } &&
                MatchesOwnerWrittenReconciliation(
                    entry,
                    reconciliation) &&
                reconciliation.ActualDisposition ==
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }
            if (authority is not PreparedAbortAuthority
                    { Claimed: true } exactAuthority ||
                !IsPreparedAbortBoundLocked(entry, exactAuthority) ||
                !IsAbortPhaseAllowed(entry, exactAuthority.Reason) ||
                !ReferenceEquals(
                    reconciliation.Receipt,
                    exactAuthority.Receipt) ||
                reconciliation.ActualDisposition !=
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault ||
                exactAuthority.Receipt.ActualDisposition !=
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault ||
                reconciliation.Outcome is not (
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault or
                    MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted) ||
                !TryAdoptOwnerAbortedReceiptLocked(
                    entry,
                    exactAuthority.Receipt))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            entry.ActualOwnerDisposition =
                MedusaPeriodicDamageDispositionOutcome.InvariantFault;
            entry.PendingTerminalWithoutHp = null;
            entry.Phase =
                MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault;
            return MedusaPeriodicDamageLedgerMutationOutcome
                .OwnerInvariantFault;
        }
    }

    private MedusaPeriodicDamageLedgerMutationOutcome
        TryCreatePreparedAbortAuthorityLocked(
            Entry entry,
            MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
            MedusaPeriodicDamagePreparedAbortReason reason,
            out MedusaPeriodicDamagePreparedAbortAuthority? authority)
    {
        authority = null;
        if (receipt is null ||
            !CanBindPreparedAbortReceiptLocked(entry, receipt))
        {
            return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
        }
        if (entry.PendingPreparedAbort is { } pending)
        {
            if (ReferenceEquals(pending.Receipt, receipt))
            {
                authority = pending;
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }
            return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
        }

        if (reason ==
                MedusaPeriodicDamagePreparedAbortReason.RuntimeRetirement &&
            entry.Phase == MedusaPeriodicDamageLedgerPhase.Prepared)
        {
            // Fence any later capability reacquisition before returning the
            // retirement authority. A previously handed-out observer still
            // wins safely by moving the entry to post-HP quarantine.
            entry.Phase =
                MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault;
        }

        var created = new PreparedAbortAuthority(
            this,
            entry,
            receipt,
            reason);
        entry.PendingPreparedAbort = created;
        authority = created;
        return MedusaPeriodicDamageLedgerMutationOutcome.Prepared;
    }

    private bool TryClaimPreparedAbort(
        PreparedAbortAuthority authority,
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
            if (!IsAbortPhaseAllowed(entry, authority.Reason) ||
                !IsPreparedAbortBoundLocked(entry, authority) ||
                !ReferenceEquals(authority.Reservation, reservation) ||
                !ReferenceEquals(authority.Receipt, receipt) ||
                !CanBindPreparedAbortReceiptLocked(entry, receipt))
            {
                return false;
            }

            authority.Claimed = true;
            return true;
        }
    }

    private bool IsPreparedAbortBoundLocked(
        Entry entry,
        MedusaPeriodicDamagePreparedAbortAuthority? authority) =>
        authority is PreparedAbortAuthority exact &&
        ReferenceEquals(exact.Owner, this) &&
        ReferenceEquals(exact.Entry, entry) &&
        ReferenceEquals(entry.PendingPreparedAbort, exact) &&
        ReferenceEquals(exact.Reservation, entry.Reservation) &&
        exact.Identity == entry.Identity;

    private static bool IsAbortPhaseAllowed(
        Entry entry,
        MedusaPeriodicDamagePreparedAbortReason reason) => reason switch
        {
            MedusaPeriodicDamagePreparedAbortReason.RuntimeRetirement =>
                entry.Phase is (
                    MedusaPeriodicDamageLedgerPhase.Prepared or
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault),
            MedusaPeriodicDamagePreparedAbortReason.AttemptsExhausted =>
                entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault &&
                entry.PreparationAttempt >= MaximumPreparationAttempts,
            MedusaPeriodicDamagePreparedAbortReason.PreHpInvariantFault =>
                entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault,
            _ => false
        };

    private static bool CanBindPreparedAbortReceiptLocked(
        Entry entry,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        if (receipt.ActualDisposition is not null and not
                MedusaPeriodicDamageDispositionOutcome.InvariantFault ||
            receipt.Identity != entry.Identity ||
            !receipt.MatchesReservation(entry.Reservation))
        {
            return false;
        }
        if (MatchesOwnerReceiptTuple(
                entry.Reservation,
                entry.Target,
                entry.AttackEventId,
                receipt))
        {
            return true;
        }

        return entry.PendingRefresh is { } pending &&
            MatchesOwnerReceiptTuple(
                entry.Reservation,
                pending.Target,
                pending.ReplacementAttackEventId,
                receipt);
    }
}
