using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    private sealed class TerminalWithoutHpAuthority
        : MedusaPeriodicDamageTerminalWithoutHpAuthority
    {
        internal TerminalWithoutHpAuthority(
            MedusaPeriodicDamageLedger owner,
            Entry entry,
            MedusaPreparedPeriodicDamageOwnerReceipt receipt,
            MedusaPeriodicDamageTerminalWithoutHpReason reason)
        {
            Owner = owner;
            Entry = entry;
            Receipt = receipt;
            Reason = reason;
        }

        internal MedusaPeriodicDamageLedger Owner { get; }

        internal Entry Entry { get; }

        internal MedusaPreparedPeriodicDamageOwnerReceipt Receipt { get; }

        internal override MedusaPeriodicDamageTerminalWithoutHpReason Reason
            { get; }

        internal bool Claimed { get; set; }

        internal override bool TryClaim(
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
                reservation,
            MedusaPreparedPeriodicDamageOwnerReceipt receipt) =>
            Owner.TryClaimTerminalWithoutHp(this, reservation, receipt);
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        TryCreateTerminalWithoutHpAuthority(
            MedusaPeriodicDamageLedgerHandle? handle,
            MedusaPeriodicDamageTerminalClassification? classification,
            out MedusaPeriodicDamageTerminalWithoutHpAuthority? authority)
    {
        authority = null;
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (!TryReadTerminalClassificationNonThrowing(
                    classification,
                    entry.Identity,
                    entry.Target,
                    out var reason))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
            }
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Prepared ||
                entry.PendingRefresh is not null ||
                entry.PendingPreparedAbort is not null)
            {
                return entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined
                    ? MedusaPeriodicDamageLedgerMutationOutcome.Quarantined
                    : MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (entry.PendingTerminalWithoutHp is { } pending)
            {
                if (pending.Reason == reason &&
                    ReferenceEquals(pending.Receipt, entry.OwnerReceipt))
                {
                    authority = pending;
                    return MedusaPeriodicDamageLedgerMutationOutcome
                        .AlreadyPresent;
                }
                return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
            }

            var prepared = new TerminalWithoutHpAuthority(
                this,
                entry,
                entry.OwnerReceipt,
                reason);
            entry.PendingTerminalWithoutHp = prepared;
            authority = prepared;
            return MedusaPeriodicDamageLedgerMutationOutcome.Prepared;
        }
    }

    internal bool TryGetPendingTerminalWithoutHpAuthority(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaPeriodicDamageTerminalWithoutHpAuthority authority)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                authority = null!;
                return false;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase == MedusaPeriodicDamageLedgerPhase.Prepared &&
                entry.PendingTerminalWithoutHp is { } pending)
            {
                authority = pending;
                return true;
            }

            authority = null!;
            return false;
        }
    }

    private bool TryClaimTerminalWithoutHp(
        TerminalWithoutHpAuthority authority,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(authority.Owner, this) ||
                !TryResolveEntryLocked(authority.Entry, out var entry))
            {
                return false;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Prepared ||
                !ReferenceEquals(entry.PendingTerminalWithoutHp, authority) ||
                !ReferenceEquals(entry.Reservation, reservation) ||
                !ReferenceEquals(entry.OwnerReceipt, receipt) ||
                !ReferenceEquals(authority.Receipt, receipt) ||
                receipt.ActualDisposition is not null)
            {
                return false;
            }

            authority.Claimed = true;
            return true;
        }
    }

    private static bool TryReadTerminalClassificationNonThrowing(
        MedusaPeriodicDamageTerminalClassification? classification,
        in MedusaPeriodicDamageIdentity identity,
        in MedusaPeriodicDamageTargetCapture target,
        out MedusaPeriodicDamageTerminalWithoutHpReason reason)
    {
        try
        {
            var observedReason = classification?.Reason;
            if (classification is not null &&
                observedReason is
                    (MedusaPeriodicDamageTerminalWithoutHpReason.TargetStale or
                     MedusaPeriodicDamageTerminalWithoutHpReason.TargetDead or
                     MedusaPeriodicDamageTerminalWithoutHpReason
                         .TargetTransferred) &&
                classification.Matches(identity, target))
            {
                reason = observedReason.Value;
                return true;
            }
        }
        catch
        {
            // An opaque proof must fail closed if its registry evidence cannot
            // be read exactly.
        }

        reason = default;
        return false;
    }
}
