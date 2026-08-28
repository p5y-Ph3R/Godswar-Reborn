using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    internal MedusaPeriodicDamageLedgerMutationOutcome
        SettleOwnerInvariantFault(
            MedusaPeriodicDamageLedgerHandle? handle,
            MedusaPeriodicDamageInvariantSettlementAuthority?
                settlementAuthority)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase !=
                    MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault ||
                entry.ActualOwnerDisposition !=
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault ||
                entry.OwnerReceipt.ActualDisposition !=
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault ||
                settlementAuthority is null ||
                !MatchesInvariantSettlementNonThrowing(
                    settlementAuthority,
                    entry.Identity))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            _entries.Remove(entry.Identity.WorldInstanceId);
            return MedusaPeriodicDamageLedgerMutationOutcome.InvariantSettled;
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome MarkPublished(
        MedusaPeriodicDamageLedgerHandle? handle)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase == MedusaPeriodicDamageLedgerPhase.Published)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }
            var requiredMask = RecipientMask(entry.RecipientCount);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.OwnerAcked ||
                IsLethalHpCommit(entry) &&
                (!entry.LethalLifeAdvanced ||
                 !entry.LethalOwnerCleanupSettled ||
                 !entry.LethalRegistrySideEffectsSettled) ||
                entry.RecipientSettlementMask != requiredMask)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            entry.Phase = MedusaPeriodicDamageLedgerPhase.Published;
            return MedusaPeriodicDamageLedgerMutationOutcome.Published;
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome RemovePublished(
        MedusaPeriodicDamageLedgerHandle? handle,
        MedusaPeriodicDamagePersistenceSettlementAuthority?
            settlementAuthority)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Published ||
                !IsExactPersistenceAuthorityLocked(
                    entry,
                    settlementAuthority) ||
                !MatchesPersistenceSettlementNonThrowing(
                    settlementAuthority!,
                    entry.Identity))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            // There is deliberately no eviction path for Prepared or any
            // post-HP phase. The owning registry removes this per-live-world
            // slot only after publication settlement (and the later one-shot
            // persistence observation) is complete.
            _entries.Remove(entry.Identity.WorldInstanceId);
            return MedusaPeriodicDamageLedgerMutationOutcome.Removed;
        }
    }

    private static ulong RecipientMask(int count) => count <= 0
        ? 0
        : (1UL << count) - 1;

    private static bool MatchesInvariantSettlementNonThrowing(
        MedusaPeriodicDamageInvariantSettlementAuthority authority,
        in MedusaPeriodicDamageIdentity identity)
    {
        try
        {
            return authority.Matches(identity);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesPersistenceSettlementNonThrowing(
        MedusaPeriodicDamagePersistenceSettlementAuthority authority,
        in MedusaPeriodicDamageIdentity identity)
    {
        try
        {
            return authority.Matches(identity);
        }
        catch
        {
            return false;
        }
    }
}
