namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaPeriodicDamageOwnerIntent : byte
{
    Applied = 1,
    Terminal = 2
}

internal enum MedusaPeriodicDamageOwnerPrepareOutcome : byte
{
    Prepared = 1,
    AlreadyPrepared = 2,
    ForeignReservation = 3,
    InvalidIdentity = 4,
    InvariantFault = 5,
    ConflictingPreparation = 6,
    NonMonotonicEvent = 7,
    RefreshAuthorityRequired = 8
}

/// <summary>
/// Non-forgeable process-local proof that the authoritative map owner
/// prepared one exact reservation, event ID, and requested disposition.
/// The owner records the actual consumed disposition on this same object so
/// an exact replay can disambiguate the mechanics runtime's generic
/// AlreadyCompleted result.
/// </summary>
internal abstract class MedusaPreparedPeriodicDamageOwnerReceipt
{
    private protected MedusaPreparedPeriodicDamageOwnerReceipt()
    {
    }

    internal abstract MedusaPeriodicDamageIdentity Identity { get; }

    internal abstract ulong AttackEventId { get; }

    internal abstract MedusaPeriodicDamageOwnerIntent RequestedIntent
        { get; }

    internal abstract MedusaPeriodicDamageDispositionOutcome?
        ActualDisposition { get; }

    internal abstract bool MatchesReservation(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            reservation);
}

/// <summary>
/// One-shot proof from the registry ledger that a retained entry is still
/// strictly pre-HP and may replace its prepared receipt after the ECS lane
/// consumed an event ID without applying damage.
/// </summary>
internal abstract class MedusaPeriodicDamageReceiptRefreshAuthority
{
    private const int NotInstalled = 0;
    private const int CaptureInProgress = 1;
    private const int Installed = 2;
    private const int ContradictoryInstall = 3;

    private MedusaPreparedPeriodicDamageOwnerReceipt? _installedReceipt;
    private int _installationState;

    private protected MedusaPeriodicDamageReceiptRefreshAuthority()
    {
    }

    internal abstract bool TryClaim(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt previousReceipt,
        ulong replacementAttackEventId);

    internal void MarkInstalled(
        MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        if (receipt is null)
        {
            Volatile.Write(
                ref _installationState,
                ContradictoryInstall);
            return;
        }
        if (Interlocked.CompareExchange(
                ref _installationState,
                CaptureInProgress,
                NotInstalled) == NotInstalled)
        {
            _installedReceipt = receipt;
            _ = Interlocked.CompareExchange(
                ref _installationState,
                Installed,
                CaptureInProgress);
            InvokeInstalledCoreNonThrowing(receipt);
            return;
        }

        if (Volatile.Read(ref _installationState) == Installed &&
            ReferenceEquals(_installedReceipt, receipt))
        {
            return;
        }

        Volatile.Write(
            ref _installationState,
            ContradictoryInstall);
        InvokeInstalledCoreNonThrowing(receipt);
    }

    internal bool TryReadInstalled(
        out MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
        out bool contradictory)
    {
        var state = Volatile.Read(ref _installationState);
        receipt = _installedReceipt;
        contradictory = state is CaptureInProgress or ContradictoryInstall;
        return state != NotInstalled;
    }

    private void InvokeInstalledCoreNonThrowing(
        MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        try
        {
            MarkInstalledCore(receipt);
        }
        catch
        {
            // The owner has already swapped to the replacement. The base
            // marker lets the retained ledger recover it on its next access.
        }
    }

    private protected abstract void MarkInstalledCore(
        MedusaPreparedPeriodicDamageOwnerReceipt receipt);
}

internal readonly record struct MedusaPeriodicDamageOwnerPrepareResult(
    MedusaPeriodicDamageOwnerPrepareOutcome Outcome,
    MedusaPreparedPeriodicDamageOwnerReceipt? Receipt)
{
    public bool IsPrepared =>
        Outcome is (MedusaPeriodicDamageOwnerPrepareOutcome.Prepared or
            MedusaPeriodicDamageOwnerPrepareOutcome.AlreadyPrepared) &&
        Receipt is not null;
}

internal readonly record struct MedusaPeriodicDamageOwnerReconcileResult(
    MedusaPeriodicDamageDispositionOutcome Outcome,
    MedusaPreparedPeriodicDamageOwnerReceipt? Receipt,
    MedusaPeriodicDamageDispositionOutcome? ActualDisposition)
{
    public bool IsExactReplay =>
        Outcome == MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
        Receipt is { } receipt &&
        ActualDisposition is { } actual &&
        receipt.ActualDisposition == actual;

    public bool IsAuthoritativeApplied =>
        IsOwnerWrittenDisposition(
            MedusaPeriodicDamageOwnerIntent.Applied,
            MedusaPeriodicDamageDispositionOutcome.Applied);

    public bool IsAuthoritativeTerminal =>
        IsOwnerWrittenDisposition(
            expectedIntent: null,
            expectedActual:
                MedusaPeriodicDamageDispositionOutcome.Terminal);

    private bool IsOwnerWrittenDisposition(
        MedusaPeriodicDamageOwnerIntent? expectedIntent,
        MedusaPeriodicDamageDispositionOutcome expectedActual) =>
        Receipt is { } receipt &&
        (expectedIntent is null || receipt.RequestedIntent == expectedIntent) &&
        ActualDisposition == expectedActual &&
        receipt.ActualDisposition == expectedActual &&
        (Outcome == expectedActual ||
         Outcome == MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted);
}

internal enum MedusaPeriodicDamageTerminalWithoutHpReason : byte
{
    TargetStale = 1,
    TargetDead = 2,
    TargetTransferred = 3
}

/// <summary>
/// Non-forgeable evidence minted by the registry while its exact target gate
/// is held. The later live slice supplies the private concrete classifier;
/// callers cannot turn a raw reason enum into terminal authority.
/// </summary>
internal abstract class MedusaPeriodicDamageTerminalClassification
{
    private protected MedusaPeriodicDamageTerminalClassification()
    {
    }

    internal abstract MedusaPeriodicDamageTerminalWithoutHpReason Reason
        { get; }

    internal abstract bool Matches(
        in MedusaPeriodicDamageIdentity identity,
        in MedusaPeriodicDamageTargetCapture target);
}

/// <summary>
/// Opaque registry proof that an exact prepared entry still has no HP marker
/// and its target was classified terminal by the retained registry gate.
/// This permits owner consumption as Terminal even when the originally
/// requested intent was Applied.
/// </summary>
internal abstract class MedusaPeriodicDamageTerminalWithoutHpAuthority
{
    private protected MedusaPeriodicDamageTerminalWithoutHpAuthority()
    {
    }

    internal abstract MedusaPeriodicDamageTerminalWithoutHpReason Reason
        { get; }

    internal abstract bool TryClaim(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt);
}

/// <summary>
/// Opaque ledger proof that the exact prepared attempt crossed its validated
/// HP boundary and is ready for owner acknowledgement. The proof remains
/// reacquirable while acknowledgement is unresolved, so a lost owner result
/// can be read back without applying HP again.
/// </summary>
internal abstract class MedusaPeriodicDamageOwnerAcknowledgementAuthority
{
    private protected MedusaPeriodicDamageOwnerAcknowledgementAuthority()
    {
    }

    internal abstract bool TryClaim(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt);
}

internal enum MedusaPeriodicDamagePreparedAbortReason : byte
{
    PreHpInvariantFault = 1,
    AttemptsExhausted = 2,
    RuntimeRetirement = 3
}

/// <summary>
/// Opaque ledger proof that one exact owner reservation may be consumed as an
/// invariant fault before HP. It is distinct from both post-HP acknowledgement
/// and registry-classified terminal-without-HP authority.
/// </summary>
internal abstract class MedusaPeriodicDamagePreparedAbortAuthority
{
    private protected MedusaPeriodicDamagePreparedAbortAuthority()
    {
    }

    internal abstract MedusaPeriodicDamagePreparedAbortReason Reason { get; }

    internal abstract bool TryClaim(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation reservation,
        MedusaPreparedPeriodicDamageOwnerReceipt receipt);
}

internal readonly record struct MedusaPeriodicDamageTargetCapture(
    MedusaMonsterPlayerTargetAuthority Authority,
    int CurrentHealth)
{
    public bool IsValid => Authority.IsValid && CurrentHealth > 0;

    public bool Matches(in MedusaPeriodicDamageIdentity identity) =>
        IsValid &&
        Authority.WorldInstanceId == identity.WorldInstanceId &&
        Authority.Ownership == identity.TargetOwnership &&
        Authority.CharacterId == identity.TargetCharacterId &&
        Authority.LifeRevision == identity.TargetLifeRevision &&
        Authority.WorldMembershipEpoch ==
            identity.TargetWorldMembershipEpoch;
}

/// <summary>
/// Allocation-free evidence captured at the first irreversible
/// GameCharacter HP/vitals mutation. A lethal ECS decision advances only its
/// private ECS life scalar here; registry life ownership advances later.
/// </summary>
internal readonly record struct MedusaPeriodicDamageHpCommitEvidence(
    ulong AttackEventId,
    int BeforeHealth,
    int AfterHealth,
    long BeforeVitalsRevision,
    long AfterVitalsRevision,
    long BeforeLifeRevision,
    long AfterLifeRevision)
{
    public bool IsValid =>
        AttackEventId != 0 &&
        BeforeHealth > 0 &&
        AfterHealth >= 0 &&
        AfterHealth < BeforeHealth &&
        BeforeVitalsRevision >= 0 &&
        BeforeVitalsRevision < long.MaxValue &&
        AfterVitalsRevision == BeforeVitalsRevision + 1 &&
        BeforeLifeRevision >= 0 &&
        (AfterHealth != 0 &&
            AfterLifeRevision == BeforeLifeRevision ||
         AfterHealth == 0 &&
            BeforeLifeRevision < long.MaxValue &&
            AfterLifeRevision == BeforeLifeRevision + 1);
}

/// <summary>
/// Preallocated callback passed to the vitals adapter in the later wiring
/// slice. Its non-virtual wrapper guarantees that no ledger fault can escape
/// after HP has changed.
/// </summary>
internal abstract class MedusaPeriodicDamageHpCommitObserver
{
    private const int NoObservation = 0;
    private const int CaptureInProgress = 1;
    private const int ValidEvidence = 2;
    private const int InvalidEvidence = 3;
    private const int ContradictoryEvidence = 4;

    private MedusaPeriodicDamageHpCommitEvidence _evidence;
    private int _observationState;

    private protected MedusaPeriodicDamageHpCommitObserver()
    {
    }

    internal void MarkHpCommitted(
        in MedusaPeriodicDamageHpCommitEvidence evidence)
    {
        if (Interlocked.CompareExchange(
                ref _observationState,
                CaptureInProgress,
                NoObservation) == NoObservation)
        {
            // Publish the evidence before invoking any fallible derived work.
            // CaptureInProgress is itself a durable post-HP quarantine marker,
            // so even an asynchronous interruption can never look pre-HP.
            _evidence = evidence;
            _ = Interlocked.CompareExchange(
                ref _observationState,
                evidence.IsValid ? ValidEvidence : InvalidEvidence,
                CaptureInProgress);
            InvokeCoreNonThrowing(evidence);
            return;
        }

        var observed = Volatile.Read(ref _observationState);
        if (observed is ValidEvidence or InvalidEvidence &&
            _evidence == evidence)
        {
            return;
        }

        // A second, non-identical observation can only mean that the exact
        // irreversible boundary was invoked inconsistently. Once published,
        // this state is sticky: the first writer's final CAS cannot erase it.
        Volatile.Write(ref _observationState, ContradictoryEvidence);
        InvokeCoreNonThrowing(evidence);
    }

    private void InvokeCoreNonThrowing(
        in MedusaPeriodicDamageHpCommitEvidence evidence)
    {
        try
        {
            MarkHpCommittedCore(evidence);
        }
        catch
        {
            // HP is already irreversible at this boundary. Recovery is
            // driven by the retained ledger entry, never by this call stack.
        }
    }

    internal bool TryReadPostHpEvidence(
        out MedusaPeriodicDamageHpCommitEvidence evidence,
        out bool hasValidShape)
    {
        var state = Volatile.Read(ref _observationState);
        if (state == NoObservation)
        {
            evidence = default;
            hasValidShape = false;
            return false;
        }

        evidence = _evidence;
        hasValidShape = state == ValidEvidence;
        return true;
    }

    private protected abstract void MarkHpCommittedCore(
        in MedusaPeriodicDamageHpCommitEvidence evidence);
}

/// <summary>
/// Opaque proof supplied by the registry only after the exact target and the
/// terminal roster have been failed closed for an owner-consumed invariant.
/// It is intentionally absent from the foundation ledger implementation;
/// the live wiring slice owns its private concrete implementation.
/// </summary>
internal abstract class MedusaPeriodicDamageInvariantSettlementAuthority
{
    private protected MedusaPeriodicDamageInvariantSettlementAuthority()
    {
    }

    internal abstract bool Matches(
        in MedusaPeriodicDamageIdentity identity);
}

/// <summary>
/// Opaque proof produced only after the one frozen persistence attempt has
/// completed (successfully or observably failed). Published entries cannot be
/// removed without this proof, so teardown cannot discard pending work.
/// </summary>
internal abstract class MedusaPeriodicDamagePersistenceSettlementAuthority
{
    private protected MedusaPeriodicDamagePersistenceSettlementAuthority()
    {
    }

    internal abstract bool Matches(
        in MedusaPeriodicDamageIdentity identity);
}
