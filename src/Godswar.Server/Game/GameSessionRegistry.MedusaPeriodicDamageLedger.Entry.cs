using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    private sealed class Entry : MedusaPeriodicDamageLedgerHandle
    {
        internal Entry(
            MedusaPeriodicDamageLedger owner,
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
                reservation,
            in MedusaPeriodicDamageTargetCapture target,
            ulong attackEventId,
            MedusaPreparedPeriodicDamageOwnerReceipt ownerReceipt,
            MedusaPeriodicDamageRecipientIdentity[] recipients)
        {
            Owner = owner;
            Reservation = reservation;
            Identity = reservation.Identity;
            Target = target;
            _attackEventId = attackEventId;
            _ownerReceipt = ownerReceipt;
            RequestedIntent = ownerReceipt.RequestedIntent;
            Observer = new(owner, this);
            Recipients = CreateRecipientEntries(owner, this, recipients);
        }

        internal MedusaPeriodicDamageLedger Owner { get; }

        internal MedusaEncounterMechanicsRuntime
            .PeriodicDamageReservation Reservation { get; }

        internal MedusaPeriodicDamageTargetCapture Target { get; set; }

        private MedusaPreparedPeriodicDamageOwnerReceipt _ownerReceipt;

        internal MedusaPreparedPeriodicDamageOwnerReceipt
            OwnerReceipt => _ownerReceipt;

        internal void ReplaceOwnerReceipt(
            MedusaPreparedPeriodicDamageOwnerReceipt ownerReceipt) =>
            _ownerReceipt = ownerReceipt;

        internal MedusaPeriodicDamageOwnerIntent RequestedIntent
            { get; set; }

        internal MedusaPeriodicDamageLedgerPhase Phase { get; set; } =
            MedusaPeriodicDamageLedgerPhase.Prepared;

        internal int PreparationAttempt { get; set; } = 1;

        internal MedusaPeriodicDamageHpCommitEvidence? HpCommit { get; set; }

        internal MedusaPeriodicDamageDispositionOutcome?
            ActualOwnerDisposition { get; set; }

        internal MedusaPeriodicDamageTerminalWithoutHpReason?
            TerminalWithoutHpReason { get; set; }

        internal ReceiptRefreshAuthority? PendingRefresh { get; set; }

        internal TerminalWithoutHpAuthority? PendingTerminalWithoutHp
            { get; set; }

        internal OwnerAcknowledgementAuthority? PendingOwnerAcknowledgement
            { get; set; }

        internal PreparedAbortAuthority? PendingPreparedAbort { get; set; }

        internal RecipientEntry[] Recipients { get; set; }

        internal ulong RecipientAdmissionMask { get; set; }

        internal ulong RecipientSettlementMask { get; set; }

        internal bool LethalLifeAdvanced { get; set; }

        internal bool LethalOwnerCleanupSettled { get; set; }

        internal bool LethalRegistrySideEffectsSettled { get; set; }

        internal bool LethalStatusCleanupSettled { get; set; }

        internal bool PersistenceAttemptClaimed { get; set; }

        internal bool PersistenceSettled { get; set; }

        internal PersistenceSettlementAuthority? PersistenceAuthority
            { get; set; }

        internal LedgerHpCommitObserver Observer { get; }

        internal override MedusaPeriodicDamageIdentity Identity { get; }

        private ulong _attackEventId;

        internal ulong AttackEventId => _attackEventId;

        internal void ReplaceAttackEventId(ulong attackEventId) =>
            _attackEventId = attackEventId;

        internal int RecipientCount => Recipients.Length;
    }

    private sealed class RecipientEntry
    {
        internal RecipientEntry(
            in MedusaPeriodicDamageRecipientIdentity identity,
            RecipientSettlementObserver observer)
        {
            Identity = identity;
            Observer = observer;
        }

        internal MedusaPeriodicDamageRecipientIdentity Identity { get; }

        internal RecipientSettlementObserver Observer { get; }
    }

    private sealed class RecipientSettlementObserver
        : MedusaPeriodicDamageRecipientSettlementObserver
    {
        private readonly MedusaPeriodicDamageLedger _owner;
        private readonly Entry _entry;

        internal RecipientSettlementObserver(
            MedusaPeriodicDamageLedger owner,
            Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        private protected override void MarkSettledCore(
            bool admissionOwned)
        {
#if DEBUG
            _owner.ProtocolCheckBeforeRecipientSettlementTransition?.Invoke();
#endif
            _owner.SynchronizeRecipientSettlement(_entry);
        }
    }

    private sealed class LedgerHpCommitObserver
        : MedusaPeriodicDamageHpCommitObserver
    {
        private readonly MedusaPeriodicDamageLedger _owner;
        private readonly Entry _entry;

        internal LedgerHpCommitObserver(
            MedusaPeriodicDamageLedger owner,
            Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        private protected override void MarkHpCommittedCore(
            in MedusaPeriodicDamageHpCommitEvidence evidence)
        {
#if DEBUG
            _owner.ProtocolCheckBeforeHpCommitTransition?.Invoke();
#endif
            _owner.MarkHpCommittedCore(_entry, evidence);
        }
    }
}
