using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    private sealed class PersistenceSettlementAuthority
        : MedusaPeriodicDamagePersistenceSettlementAuthority
    {
        internal PersistenceSettlementAuthority(
            MedusaPeriodicDamageLedger owner,
            Entry entry)
        {
            Owner = owner;
            Entry = entry;
        }

        internal MedusaPeriodicDamageLedger Owner { get; }

        internal Entry Entry { get; }

        internal override bool Matches(
            in MedusaPeriodicDamageIdentity identity) =>
            ReferenceEquals(Entry.Owner, Owner) &&
            Entry.Identity == identity &&
            Entry.PersistenceSettled;
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkLethalLifeAdvanced(
            MedusaPeriodicDamageLedgerHandle? handle) =>
        MarkLethalPostCommitStep(
            handle,
            static entry => true,
            static entry => entry.LethalLifeAdvanced,
            static entry => entry.LethalLifeAdvanced = true);

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkLethalOwnerCleanupSettled(
            MedusaPeriodicDamageLedgerHandle? handle) =>
        MarkLethalPostCommitStep(
            handle,
            static entry => entry.LethalLifeAdvanced,
            static entry => entry.LethalOwnerCleanupSettled,
            static entry => entry.LethalOwnerCleanupSettled = true);

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkLethalRegistrySideEffectsSettled(
            MedusaPeriodicDamageLedgerHandle? handle) =>
        MarkLethalPostCommitStep(
            handle,
            static entry => entry.LethalOwnerCleanupSettled,
            static entry => entry.LethalRegistrySideEffectsSettled,
            static entry =>
                entry.LethalRegistrySideEffectsSettled = true);

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkLethalStatusCleanupSettled(
            MedusaPeriodicDamageLedgerHandle? handle)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (!IsLethalHpCommit(entry) ||
                entry.Phase != MedusaPeriodicDamageLedgerPhase.Published)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (entry.LethalStatusCleanupSettled)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }

            entry.LethalStatusCleanupSettled = true;
            return MedusaPeriodicDamageLedgerMutationOutcome.Published;
        }
    }

    internal bool TryClaimPersistenceAttempt(
        MedusaPeriodicDamageLedgerHandle? handle)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return false;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Published ||
                IsLethalHpCommit(entry) &&
                !entry.LethalStatusCleanupSettled ||
                entry.PersistenceAttemptClaimed)
            {
                return false;
            }

            entry.PersistenceAttemptClaimed = true;
            return true;
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkPersistenceAttemptSettled(
            MedusaPeriodicDamageLedgerHandle? handle)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Published ||
                !entry.PersistenceAttemptClaimed)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (entry.PersistenceSettled)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }

            entry.PersistenceSettled = true;
            entry.PersistenceAuthority ??= new(this, entry);
            return MedusaPeriodicDamageLedgerMutationOutcome.Published;
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        ReleasePersistenceAttempt(
            MedusaPeriodicDamageLedgerHandle? handle)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Published ||
                !entry.PersistenceAttemptClaimed ||
                entry.PersistenceSettled)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            entry.PersistenceAttemptClaimed = false;
            return MedusaPeriodicDamageLedgerMutationOutcome.Published;
        }
    }

    internal bool TryGetPersistenceSettlementAuthority(
        MedusaPeriodicDamageLedgerHandle? handle,
        out MedusaPeriodicDamagePersistenceSettlementAuthority authority)
    {
        lock (_gate)
        {
            if (TryResolveEntryLocked(handle, out var entry))
            {
                SynchronizePostHpLocked(entry);
                if (entry.Phase ==
                        MedusaPeriodicDamageLedgerPhase.Published &&
                    entry.PersistenceSettled)
                {
                    entry.PersistenceAuthority ??= new(this, entry);
                    authority = entry.PersistenceAuthority;
                    return true;
                }
            }

            authority = null!;
            return false;
        }
    }

    private MedusaPeriodicDamageLedgerMutationOutcome
        MarkLethalPostCommitStep(
            MedusaPeriodicDamageLedgerHandle? handle,
            Func<Entry, bool> prerequisite,
            Func<Entry, bool> completed,
            Action<Entry> markCompleted)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (!IsLethalHpCommit(entry) ||
                entry.Phase != MedusaPeriodicDamageLedgerPhase.OwnerAcked ||
                !prerequisite(entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (completed(entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }

            markCompleted(entry);
            return MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked;
        }
    }

    private static bool IsLethalHpCommit(Entry entry) =>
        entry.HpCommit is { AfterHealth: 0 } &&
        entry.ActualOwnerDisposition ==
            MedusaPeriodicDamageDispositionOutcome.Terminal;

    private bool IsExactPersistenceAuthorityLocked(
        Entry entry,
        MedusaPeriodicDamagePersistenceSettlementAuthority? authority) =>
        authority is PersistenceSettlementAuthority exact &&
        ReferenceEquals(exact.Owner, this) &&
        ReferenceEquals(exact.Entry, entry) &&
        ReferenceEquals(entry.PersistenceAuthority, exact) &&
        entry.PersistenceAttemptClaimed &&
        entry.PersistenceSettled;
}
