using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal enum MedusaPeriodicDamageLedgerPhase : byte
{
    Prepared = 1,
    HPCommitted = 2,
    OwnerAcked = 3,
    Published = 4,
    PostHpQuarantined = 5,
    OwnerInvariantFault = 6,
    PreHpInvariantFault = 7
}

internal enum MedusaPeriodicDamageLedgerMutationOutcome : byte
{
    Prepared = 1,
    AlreadyPresent = 2,
    Refreshed = 3,
    HPCommitted = 4,
    OwnerAcked = 5,
    Published = 6,
    Removed = 7,
    NotFound = 8,
    IdentityMismatch = 9,
    Invalid = 10,
    WrongPhase = 11,
    AttemptsExhausted = 12,
    Quarantined = 13,
    OwnerInvariantFault = 14,
    InvariantSettled = 15,
    CapacityExhausted = 16,
    OwnerAbortRequired = 17,
    InvariantSettlementRequired = 18,
    ReconciliationRequired = 19,
    PreHpInvariantFault = 20
}

internal readonly record struct MedusaPeriodicDamageLedgerSnapshot(
    MedusaPeriodicDamageIdentity Identity,
    MedusaPeriodicDamageLedgerPhase Phase,
    int PreparationAttempt,
    ulong AttackEventId,
    MedusaPeriodicDamageTargetCapture Target,
    MedusaPeriodicDamageOwnerIntent RequestedIntent,
    MedusaPeriodicDamageDispositionOutcome? ActualOwnerDisposition,
    MedusaPeriodicDamageTerminalWithoutHpReason? TerminalWithoutHpReason,
    MedusaPeriodicDamageHpCommitEvidence? HpCommit,
    int RecipientCount,
    bool RecipientsConfigured,
    ulong RecipientAdmissionMask,
    ulong RecipientSettlementMask,
    bool LethalLifeAdvanced,
    bool LethalOwnerCleanupSettled,
    bool LethalRegistrySideEffectsSettled,
    bool LethalStatusCleanupSettled,
    bool PersistenceAttemptClaimed,
    bool PersistenceSettled);

internal abstract class MedusaPeriodicDamageLedgerHandle
{
    private protected MedusaPeriodicDamageLedgerHandle()
    {
    }

    internal abstract MedusaPeriodicDamageIdentity Identity { get; }
}
