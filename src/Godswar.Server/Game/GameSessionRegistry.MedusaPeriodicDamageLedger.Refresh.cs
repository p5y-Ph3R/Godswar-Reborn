using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
#if DEBUG
    internal Action? ProtocolCheckBeforeRefreshInstallTransition = null;
#endif

    private sealed class ReceiptRefreshAuthority
        : MedusaPeriodicDamageReceiptRefreshAuthority
    {
        internal ReceiptRefreshAuthority(
            MedusaPeriodicDamageLedger owner,
            Entry entry,
            in MedusaPeriodicDamageTargetCapture target,
            ulong replacementAttackEventId,
            RecipientEntry[] replacementRecipients)
        {
            Owner = owner;
            Entry = entry;
            Target = target;
            ReplacementAttackEventId = replacementAttackEventId;
            ReplacementRecipients = replacementRecipients;
        }

        internal MedusaPeriodicDamageLedger Owner { get; }

        internal Entry Entry { get; }

        internal MedusaPeriodicDamageTargetCapture Target { get; }

        internal ulong ReplacementAttackEventId { get; }

        internal RecipientEntry[] ReplacementRecipients { get; }

        internal bool Claimed { get; set; }

        internal override bool TryClaim(
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
                reservation,
            MedusaPreparedPeriodicDamageOwnerReceipt previousReceipt,
            ulong replacementAttackEventId) =>
            Owner.TryClaimRefreshAuthority(
                this,
                reservation,
                previousReceipt,
                replacementAttackEventId);

        private protected override void MarkInstalledCore(
            MedusaPreparedPeriodicDamageOwnerReceipt receipt)
        {
#if DEBUG
            Owner.ProtocolCheckBeforeRefreshInstallTransition?.Invoke();
#endif
            Owner.SynchronizeInstalledRefresh(Entry);
        }
    }

    internal MedusaPeriodicDamageLedgerMutationOutcome
        TryCreateReceiptRefreshAuthority(
            MedusaPeriodicDamageLedgerHandle? handle,
            in MedusaPeriodicDamageTargetCapture target,
            ulong replacementAttackEventId,
            IReadOnlyList<MedusaPeriodicDamageRecipientIdentity>? recipients,
            out MedusaPeriodicDamageReceiptRefreshAuthority? authority)
    {
        authority = null;
        if (handle is null ||
            !TryCaptureRecipients(
                handle.Identity,
                target,
                recipients,
                out var capturedRecipients))
        {
            return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
        }

        lock (_gate)
        {
            if (!TryResolveEntryLocked(handle, out var entry))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.NotFound;
            }
            SynchronizePostHpLocked(entry);
            if (entry.Phase != MedusaPeriodicDamageLedgerPhase.Prepared)
            {
                return entry.Phase ==
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined
                    ? MedusaPeriodicDamageLedgerMutationOutcome.Quarantined
                    : MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (entry.PendingTerminalWithoutHp is not null ||
                entry.PendingPreparedAbort is not null)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase;
            }
            if (entry.PreparationAttempt >= MaximumPreparationAttempts)
            {
                entry.Phase =
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault;
                return MedusaPeriodicDamageLedgerMutationOutcome
                    .AttemptsExhausted;
            }
            if (!target.Matches(entry.Identity) ||
                replacementAttackEventId <= entry.AttackEventId)
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
            }

            if (entry.PendingRefresh is { } pending)
            {
                if (pending.Target == target &&
                    pending.ReplacementAttackEventId ==
                        replacementAttackEventId &&
                    RecipientsMatch(
                        pending.ReplacementRecipients,
                        capturedRecipients))
                {
                    authority = pending;
                    return MedusaPeriodicDamageLedgerMutationOutcome
                        .AlreadyPresent;
                }
                return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
            }

            var replacementRecipients = CreateRecipientEntries(
                this,
                entry,
                capturedRecipients);
            var prepared = new ReceiptRefreshAuthority(
                this,
                entry,
                target,
                replacementAttackEventId,
                replacementRecipients);
            entry.PendingRefresh = prepared;
            authority = prepared;
            return MedusaPeriodicDamageLedgerMutationOutcome.Prepared;
        }
    }

    private bool TryClaimRefreshAuthority(
        ReceiptRefreshAuthority authority,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt previousReceipt,
        ulong replacementAttackEventId)
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
                entry.PreparationAttempt >= MaximumPreparationAttempts ||
                !ReferenceEquals(entry.PendingRefresh, authority) ||
                !ReferenceEquals(entry.Reservation, reservation) ||
                !ReferenceEquals(entry.OwnerReceipt, previousReceipt) ||
                replacementAttackEventId !=
                    authority.ReplacementAttackEventId ||
                replacementAttackEventId <= entry.AttackEventId)
            {
                return false;
            }

            authority.Claimed = true;
            return true;
        }
    }

    private void SynchronizeInstalledRefresh(Entry entry)
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

    private void SynchronizePendingRefreshLocked(Entry entry)
    {
        if (entry.PendingRefresh is not { } pending ||
            !pending.TryReadInstalled(
                out var installed,
                out var contradictory))
        {
            return;
        }
        if (contradictory ||
            installed is null ||
            !pending.Claimed ||
            entry.Phase is not (
                MedusaPeriodicDamageLedgerPhase.Prepared or
                MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault) ||
            entry.PreparationAttempt >= MaximumPreparationAttempts ||
            !IsExactPreparation(
                entry.Reservation,
                pending.Target,
                pending.ReplacementAttackEventId,
                installed))
        {
            entry.Phase = entry.HpCommit is null &&
                entry.Phase is (
                    MedusaPeriodicDamageLedgerPhase.Prepared or
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault)
                ? MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault
                : MedusaPeriodicDamageLedgerPhase.PostHpQuarantined;
            return;
        }

        entry.Target = pending.Target;
        entry.ReplaceAttackEventId(pending.ReplacementAttackEventId);
        entry.ReplaceOwnerReceipt(installed);
        entry.RequestedIntent = installed.RequestedIntent;
        entry.Recipients = pending.ReplacementRecipients;
        entry.RecipientAdmissionMask = 0;
        entry.RecipientSettlementMask = 0;
        entry.PendingPreparedAbort = null;
        entry.PreparationAttempt++;
        entry.PendingRefresh = null;
        entry.Phase = MedusaPeriodicDamageLedgerPhase.Prepared;
    }

    private static bool TryAdoptOwnerAbortedReceiptLocked(
        Entry entry,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        if (receipt.ActualDisposition !=
                MedusaPeriodicDamageDispositionOutcome.InvariantFault)
        {
            return false;
        }

        if (MatchesOwnerReceiptTuple(
                entry.Reservation,
                entry.Target,
                entry.AttackEventId,
                receipt))
        {
            entry.ReplaceOwnerReceipt(receipt);
            entry.RequestedIntent = receipt.RequestedIntent;
            entry.PendingRefresh = null;
            return true;
        }

        if (entry.PendingRefresh is not { } pending ||
            !MatchesOwnerReceiptTuple(
                entry.Reservation,
                pending.Target,
                pending.ReplacementAttackEventId,
                receipt))
        {
            return false;
        }

        entry.Target = pending.Target;
        entry.ReplaceAttackEventId(pending.ReplacementAttackEventId);
        entry.ReplaceOwnerReceipt(receipt);
        entry.RequestedIntent = receipt.RequestedIntent;
        entry.Recipients = pending.ReplacementRecipients;
        entry.RecipientAdmissionMask = 0;
        entry.RecipientSettlementMask = 0;
        entry.PendingRefresh = null;
        entry.PendingTerminalWithoutHp = null;
        return true;
    }
}
