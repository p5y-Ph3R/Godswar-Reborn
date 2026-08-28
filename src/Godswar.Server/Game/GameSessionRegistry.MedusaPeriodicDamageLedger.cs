using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

/// <summary>
/// Bounded process-local replay authority. There is at most one retained
/// entry for each world, and every operation re-proves the full authored
/// periodic identity instead of reducing it to an event ID.
/// </summary>
internal sealed partial class MedusaPeriodicDamageLedger
{
    internal const int MaximumPreparationAttempts = 8;
    internal const int MaximumRecipients =
        MedusaIslandPolicy.MaximumPartySize;

    private readonly object _gate = new();
    private readonly Dictionary<WorldInstanceId, Entry> _entries = [];
    private readonly int _maximumWorldEntries;

    internal MedusaPeriodicDamageLedger(int maximumWorldEntries)
    {
        if (maximumWorldEntries is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWorldEntries));
        }
        _maximumWorldEntries = maximumWorldEntries;
    }

#if DEBUG
    internal Action? ProtocolCheckBeforeHpCommitTransition = null;
    internal Action? ProtocolCheckBeforeRecipientSettlementTransition = null;
#endif

    internal MedusaPeriodicDamageLedgerMutationOutcome TryPrepare(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        in MedusaPeriodicDamageTargetCapture target,
        ulong attackEventId,
        MedusaPreparedPeriodicDamageOwnerReceipt? ownerReceipt,
        IReadOnlyList<MedusaPeriodicDamageRecipientIdentity>? recipients,
        out MedusaPeriodicDamageLedgerHandle? handle)
    {
        handle = null;
        if (!IsExactPreparation(
                reservation,
                target,
                attackEventId,
                ownerReceipt) ||
            !TryCaptureRecipients(
                reservation!.Identity,
                target,
                recipients,
                out var capturedRecipients))
        {
            return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
        }

        lock (_gate)
        {
            var identity = reservation!.Identity;
            if (_entries.TryGetValue(identity.WorldInstanceId, out var current))
            {
                SynchronizePostHpLocked(current);
                if (current.Identity == identity &&
                    ReferenceEquals(current.Reservation, reservation))
                {
                    if (current.Target == target &&
                        current.AttackEventId == attackEventId &&
                        ReferenceEquals(
                            current.OwnerReceipt,
                            ownerReceipt) &&
                        RecipientsMatch(
                            current.Recipients,
                            capturedRecipients))
                    {
                        handle = current;
                        return MedusaPeriodicDamageLedgerMutationOutcome
                            .AlreadyPresent;
                    }

                    return MedusaPeriodicDamageLedgerMutationOutcome
                        .IdentityMismatch;
                }

                return MedusaPeriodicDamageLedgerMutationOutcome
                    .IdentityMismatch;
            }
            if (_entries.Count >= _maximumWorldEntries)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .CapacityExhausted;
            }

            var entry = new Entry(
                this,
                reservation,
                target,
                attackEventId,
                ownerReceipt!,
                capturedRecipients);
            _entries.Add(identity.WorldInstanceId, entry);
            handle = entry;
            return MedusaPeriodicDamageLedgerMutationOutcome.Prepared;
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        MarkTerminalWithoutHp(
            MedusaPeriodicDamageLedgerHandle? handle,
            in MedusaPeriodicDamageOwnerReconcileResult reconciliation)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase is (
                    MedusaPeriodicDamageLedgerPhase.OwnerAcked or
                    MedusaPeriodicDamageLedgerPhase.Published) &&
                entry.ActualOwnerDisposition ==
                    MedusaPeriodicDamageDispositionOutcome.Terminal &&
                MatchesOwnerWrittenReconciliation(entry, reconciliation))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AlreadyPresent;
            }
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Prepared ||
                entry.PendingTerminalWithoutHp is not
                    { Claimed: true } authority ||
                !ReferenceEquals(authority.Receipt, entry.OwnerReceipt) ||
                !MatchesOwnerWrittenReconciliation(entry, reconciliation) ||
                reconciliation.ActualDisposition !=
                    MedusaPeriodicDamageDispositionOutcome.Terminal)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }

            entry.ActualOwnerDisposition =
                MedusaPeriodicDamageDispositionOutcome.Terminal;
            entry.TerminalWithoutHpReason = authority.Reason;
            entry.PendingTerminalWithoutHp = null;
            entry.Phase = MedusaPeriodicDamageLedgerPhase.OwnerAcked;
            return MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked;
        }
    }

    internal bool TryGetSnapshot(
        WorldInstanceId worldInstanceId,
        out MedusaPeriodicDamageLedgerSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(worldInstanceId, out var entry))
            {
                snapshot = default;
                return false;
            }
            SynchronizePostHpLocked(entry);
            snapshot = Snapshot(entry);
            return true;
        }
    }

    private void MarkHpCommittedCore(
        Entry entry,
        in MedusaPeriodicDamageHpCommitEvidence evidence)
    {
        lock (_gate)
        {
            if (!TryResolveEntryLocked(entry, out var current) ||
                !ReferenceEquals(current, entry))
            {
                return;
            }
            SynchronizePostHpLocked(entry);
        }
    }

    private void SynchronizePostHpLocked(Entry entry)
    {
        SynchronizePendingRefreshLocked(entry);
        if (!entry.Observer.TryReadPostHpEvidence(
                out var evidence,
                out var hasValidShape))
        {
            SynchronizeRecipientSettlementsLocked(entry);
            return;
        }

        if (entry.Phase == MedusaPeriodicDamageLedgerPhase.Prepared)
        {
            entry.HpCommit = evidence;
            entry.Phase = hasValidShape && MatchesHpCommit(entry, evidence)
                ? MedusaPeriodicDamageLedgerPhase.HPCommitted
                : MedusaPeriodicDamageLedgerPhase.PostHpQuarantined;
        }
        else if (entry.Phase !=
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined &&
                 (!hasValidShape ||
                  entry.HpCommit is not { } committed ||
                  committed != evidence))
        {
            // A late or contradictory callback can never be treated as an
            // idempotent replay, including after owner acknowledgement or
            // publication. Preserve it as an unresolved post-HP fault.
            entry.HpCommit ??= evidence;
            entry.Phase =
                MedusaPeriodicDamageLedgerPhase.PostHpQuarantined;
        }
        SynchronizeRecipientSettlementsLocked(entry);
    }

    private void SynchronizeRecipientSettlement(Entry entry)
    {
        lock (_gate)
        {
            if (TryResolveEntryLocked(entry, out var current) &&
                ReferenceEquals(current, entry))
            {
                SynchronizePostHpLocked(entry);
            }
        }
    }

    private void SynchronizeRecipientSettlementsLocked(Entry entry)
    {
        if (entry.Phase ==
            MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault)
        {
            // Invariant settlement owns exact roster fail-close, never tick
            // publication. Any earlier pre-HP marker is intentionally ignored
            // after the owner reservation has been terminally consumed.
            return;
        }

        for (var index = 0; index < entry.Recipients.Length; index++)
        {
            if (!entry.Recipients[index].Observer.TryReadSettlement(
                    out var admissionOwned,
                    out var contradictory))
            {
                continue;
            }

            var bit = 1UL << index;
            if (contradictory ||
                entry.Phase is not (
                    MedusaPeriodicDamageLedgerPhase.OwnerAcked or
                    MedusaPeriodicDamageLedgerPhase.Published) ||
                (entry.RecipientSettlementMask & bit) != 0 &&
                ((entry.RecipientAdmissionMask & bit) != 0) !=
                    admissionOwned)
            {
                entry.Phase = entry.HpCommit is null &&
                    entry.Phase is (
                        MedusaPeriodicDamageLedgerPhase.Prepared or
                        MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault)
                    ? MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault
                    : MedusaPeriodicDamageLedgerPhase.PostHpQuarantined;
                return;
            }

            if (admissionOwned)
            {
                entry.RecipientAdmissionMask |= bit;
            }
            entry.RecipientSettlementMask |= bit;
        }
    }

    private bool TryResolveEntryLocked(
        MedusaPeriodicDamageLedgerHandle? handle,
        out Entry entry)
    {
        if (handle is Entry exact &&
            ReferenceEquals(exact.Owner, this) &&
            _entries.TryGetValue(
                exact.Identity.WorldInstanceId,
                out entry!) &&
            ReferenceEquals(entry, exact) &&
            entry.Identity == handle.Identity)
        {
            return true;
        }

        entry = null!;
        return false;
    }

    private static MedusaPeriodicDamageDispositionOutcome DispositionFor(
        MedusaPeriodicDamageOwnerIntent intent) => intent switch
        {
            MedusaPeriodicDamageOwnerIntent.Applied =>
                MedusaPeriodicDamageDispositionOutcome.Applied,
            MedusaPeriodicDamageOwnerIntent.Terminal =>
                MedusaPeriodicDamageDispositionOutcome.Terminal,
            _ => MedusaPeriodicDamageDispositionOutcome.InvariantFault
        };

    private static bool MatchesExpectedOwnerAck(
        Entry entry,
        in MedusaPeriodicDamageOwnerReconcileResult reconciliation) =>
        MatchesOwnerWrittenReconciliation(entry, reconciliation) &&
        reconciliation.ActualDisposition is { } actual &&
        actual == DispositionFor(entry.RequestedIntent);

    private static bool MatchesOwnerWrittenReconciliation(
        Entry entry,
        in MedusaPeriodicDamageOwnerReconcileResult reconciliation) =>
        ReferenceEquals(reconciliation.Receipt, entry.OwnerReceipt) &&
        reconciliation.ActualDisposition is { } actual &&
        entry.OwnerReceipt.ActualDisposition == actual &&
        (reconciliation.Outcome == actual ||
         reconciliation.Outcome ==
            MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted);

    private static MedusaPeriodicDamageLedgerSnapshot Snapshot(Entry entry) =>
        new(
            entry.Identity,
            entry.Phase,
            entry.PreparationAttempt,
            entry.AttackEventId,
            entry.Target,
            entry.RequestedIntent,
            entry.ActualOwnerDisposition,
            entry.TerminalWithoutHpReason,
            entry.HpCommit,
            entry.RecipientCount,
            RecipientsConfigured: true,
            entry.RecipientAdmissionMask,
            entry.RecipientSettlementMask,
            entry.LethalLifeAdvanced,
            entry.LethalOwnerCleanupSettled,
            entry.LethalRegistrySideEffectsSettled,
            entry.LethalStatusCleanupSettled,
            entry.PersistenceAttemptClaimed,
            entry.PersistenceSettled);
}

internal sealed partial class GameSessionRegistry
{
    private readonly MedusaPeriodicDamageLedger
        _medusaPeriodicDamageLedger;

    internal MedusaPeriodicDamageLedger MedusaPeriodicDamageLedger =>
        _medusaPeriodicDamageLedger;
}
