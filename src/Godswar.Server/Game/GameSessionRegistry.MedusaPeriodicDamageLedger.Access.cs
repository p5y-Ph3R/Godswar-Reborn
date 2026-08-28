using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    /// <summary>
    /// Reacquires the process-retained handle after a pump unwind. The ledger
    /// synchronizes every base-owned marker before exposing the current phase.
    /// </summary>
    internal bool TryGetRetained(
        WorldInstanceId worldInstanceId,
        out MedusaPeriodicDamageLedgerHandle handle,
        out MedusaPeriodicDamageLedgerSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(worldInstanceId, out var entry))
            {
                handle = null!;
                snapshot = default;
                return false;
            }

            SynchronizePostHpLocked(entry);
            handle = entry;
            snapshot = Snapshot(entry);
            return true;
        }
    }

    internal bool TryGetCurrentOwnerReceipt(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                receipt = null!;
                return false;
            }

            SynchronizePostHpLocked(entry);
            receipt = entry.OwnerReceipt;
            return true;
        }
    }

    internal bool TryGetPreparedReservation(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            reservation)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                reservation = null!;
                return false;
            }

            SynchronizePostHpLocked(entry);
            if (entry.Phase is not (
                    MedusaPeriodicDamageLedgerPhase.Prepared or
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault))
            {
                reservation = null!;
                return false;
            }

            reservation = entry.Reservation;
            return true;
        }
    }

    internal bool TryGetPreparedAttempt(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaPeriodicDamageTargetCapture target,
        out ulong attackEventId,
        out MedusaPeriodicDamageHpCommitObserver hpCommitObserver)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                target = default;
                attackEventId = 0;
                hpCommitObserver = null!;
                return false;
            }

            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Prepared ||
                entry.PendingRefresh is not null ||
                entry.PendingTerminalWithoutHp is not null ||
                entry.PendingPreparedAbort is not null)
            {
                target = default;
                attackEventId = 0;
                hpCommitObserver = null!;
                return false;
            }

            target = entry.Target;
            attackEventId = entry.AttackEventId;
            hpCommitObserver = entry.Observer;
            return true;
        }
    }

    internal bool TryGetPendingReceiptRefresh(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaPeriodicDamageTargetCapture target,
        out ulong replacementAttackEventId,
        out MedusaPeriodicDamageReceiptRefreshAuthority authority)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                target = default;
                replacementAttackEventId = 0;
                authority = null!;
                return false;
            }

            SynchronizePostHpLocked(entry);
            if (entry.Phase is (
                    MedusaPeriodicDamageLedgerPhase.Prepared or
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault) &&
                entry.PendingRefresh is { } pending)
            {
                target = pending.Target;
                replacementAttackEventId = pending.ReplacementAttackEventId;
                authority = pending;
                return true;
            }

            target = default;
            replacementAttackEventId = 0;
            authority = null!;
            return false;
        }
    }

    /// <summary>
    /// Returns the immutable recipient and its preallocated settlement marker.
    /// The later exact-send helper must invoke the marker while holding the
    /// registry gate immediately after it receives the exact egress outcome.
    /// </summary>
    internal bool TryGetRecipientIdentity(
        MedusaPeriodicDamageLedgerHandle? handle,
        int index,
        out MedusaPeriodicDamageRecipientIdentity identity)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                identity = default;
                return false;
            }

            SynchronizePostHpLocked(entry);
            if ((uint)index >= (uint)entry.Recipients.Length)
            {
                identity = default;
                return false;
            }

            identity = entry.Recipients[index].Identity;
            return true;
        }
    }

    internal bool TryGetNextRecipientSettlement(
        MedusaPeriodicDamageLedgerHandle? handle,
        out int index,
        out MedusaPeriodicDamageRecipientIdentity identity,
        out MedusaPeriodicDamageRecipientSettlementObserver observer)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                index = -1;
                identity = default;
                observer = null!;
                return false;
            }

            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.OwnerAcked ||
                IsLethalHpCommit(entry) &&
                (!entry.LethalLifeAdvanced ||
                 !entry.LethalOwnerCleanupSettled ||
                 !entry.LethalRegistrySideEffectsSettled))
            {
                index = -1;
                identity = default;
                observer = null!;
                return false;
            }

            for (var candidate = 0;
                 candidate < entry.Recipients.Length;
                 candidate++)
            {
                var bit = 1UL << candidate;
                if ((entry.RecipientSettlementMask & bit) != 0)
                {
                    continue;
                }

                index = candidate;
                identity = entry.Recipients[candidate].Identity;
                observer = entry.Recipients[candidate].Observer;
                return true;
            }

            index = -1;
            identity = default;
            observer = null!;
            return false;
        }
    }
}
