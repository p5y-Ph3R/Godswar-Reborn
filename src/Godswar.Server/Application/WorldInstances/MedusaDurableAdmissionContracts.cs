using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal enum MedusaAdmissionState : short
{
    Reserved = 1,
    RuntimeReady = 2,
    RosterTransferCommitted = 3,
    ConsumedRunning = 4,
    Completed = 5,
    Abandoned = 6,
    TimedOut = 7,
    Released = 8,
    CompletedCleaned = 9,
    AbandonedCleaned = 10,
    TimedOutCleaned = 11,
    ReleasedCleaned = 12
}

internal enum MedusaAdmissionReceiptStatus : byte
{
    Applied = 1,
    Duplicate = 2,
    NotFound = 3,
    RequestConflict = 4,
    MemberAttemptConflict = 5,
    InvalidTransition = 6,
    MemberActiveAdmissionConflict = 7
}

internal sealed class MedusaRosterTransferBarrierEvidence
{
    public MedusaRosterTransferBarrierEvidence(
        Guid stageId,
        string preparationHash)
    {
        if (stageId == Guid.Empty)
        {
            throw new ArgumentException(
                "Roster-transfer stage IDs cannot be empty.",
                nameof(stageId));
        }
        MedusaDurableAdmissionPolicy.ValidateHash(
            preparationHash,
            nameof(preparationHash));
        StageId = stageId;
        PreparationHash = preparationHash;
    }

    public Guid StageId { get; }

    public string PreparationHash { get; }
}

internal enum MedusaAdmissionCleanupKind : byte
{
    PreBarrierRelease = 1,
    TerminalEgress = 2
}

internal sealed class MedusaAdmissionCleanupEvidence
{
    public MedusaAdmissionCleanupEvidence(
        MedusaAdmissionId admissionId,
        MedusaAdmissionCleanupKind kind,
        Guid rosterOperationId,
        Guid runtimeOperationId)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        var isExact = kind switch
        {
            MedusaAdmissionCleanupKind.PreBarrierRelease =>
                rosterOperationId ==
                    MedusaAdmissionSagaOperationIds.TransferAbort(admissionId) &&
                runtimeOperationId ==
                    MedusaAdmissionSagaOperationIds.RuntimeRelease(admissionId),
            MedusaAdmissionCleanupKind.TerminalEgress =>
                rosterOperationId ==
                    MedusaAdmissionSagaOperationIds.RosterEgress(admissionId) &&
                runtimeOperationId ==
                    MedusaAdmissionSagaOperationIds.RuntimeRetire(admissionId),
            _ => false
        };
        if (!isExact)
        {
            throw new ArgumentException(
                "Cleanup evidence does not match its deterministic path receipts.");
        }
        Kind = kind;
        RosterOperationId = rosterOperationId;
        RuntimeOperationId = runtimeOperationId;
    }

    public MedusaAdmissionCleanupKind Kind { get; }

    public Guid RosterOperationId { get; }

    public Guid RuntimeOperationId { get; }
}

/// <summary>
/// Complete immutable reservation command. AdmissionId is its idempotency
/// identity. Reusing it is successful only when RequestHash is exact.
/// </summary>
internal sealed class MedusaAdmissionReservationRequest
{
    public MedusaAdmissionReservationRequest(
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        MedusaRealmDay realmDay,
        MedusaEncounterDifficulty difficulty,
        MedusaAdmissionSource source,
        PartyAdmissionLease party,
        string encounterContentFingerprint,
        DateTimeOffset requestedAtUtc)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(worldInstanceId));
        }
        if (!realmDay.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmDay));
        }
        if (!source.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        if (worldInstanceId == source.WorldInstanceId)
        {
            throw new ArgumentException(
                "The admission target must differ from its open-world source.",
                nameof(worldInstanceId));
        }
        ArgumentNullException.ThrowIfNull(party);
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var definition))
        {
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        }
        MedusaDurableAdmissionPolicy.ValidateHash(
            encounterContentFingerprint,
            nameof(encounterContentFingerprint));

        requestedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            requestedAtUtc,
            nameof(requestedAtUtc));
        if (!party.IsValidAt(requestedAtUtc))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedAtUtc),
                "The trusted party lease is not valid at reservation time.");
        }
        if (party.Members.Any(member =>
                member.RealmId != realmDay.RealmId ||
                member.Level < MedusaIslandPolicy.MinimumLevel ||
                member.SourceWorldInstanceId != source.WorldInstanceId ||
                member.SourceMapId != source.MapId))
        {
            throw new ArgumentException(
                "The trusted roster is not eligible at the exact admission source.",
                nameof(party));
        }

        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        RealmDay = realmDay;
        Difficulty = difficulty;
        ContentMapId = definition.ContentMapId;
        Source = source;
        Party = party;
        EncounterContentFingerprint = encounterContentFingerprint;
        RequestedAtUtc = requestedAtUtc;
        RosterHash = MedusaDurableAdmissionPolicy.ComputeRosterHash(party);
        RequestHash = MedusaDurableAdmissionPolicy.ComputeRequestHash(this);
    }

    public MedusaAdmissionId AdmissionId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public MedusaRealmDay RealmDay { get; }

    public MedusaEncounterDifficulty Difficulty { get; }

    public MapId ContentMapId { get; }

    public MedusaAdmissionSource Source { get; }

    public PartyAdmissionLease Party { get; }

    /// <summary>
    /// Immutable pre-start encounter content identity. It must exclude runtime
    /// object IDs, CreatedAt, StartedAt, deadlines, and other clock data.
    /// </summary>
    public string EncounterContentFingerprint { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public string RosterHash { get; }

    public string RequestHash { get; }
}

/// <summary>
/// Exact idempotent state mutation. ExpectedState makes stale coordinators
/// fail closed; TransitionId permits safe replay after an ambiguous commit.
/// </summary>
internal sealed class MedusaAdmissionTransitionRequest
{
    public MedusaAdmissionTransitionRequest(
        Guid transitionId,
        MedusaAdmissionId admissionId,
        MedusaAdmissionState expectedState,
        MedusaAdmissionState targetState,
        DateTimeOffset occurredAtUtc,
        MedusaRosterTransferBarrierEvidence? barrierEvidence = null,
        MedusaAdmissionCleanupEvidence? cleanupEvidence = null)
    {
        if (transitionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Medusa admission transition IDs cannot be empty.",
                nameof(transitionId));
        }
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        if (!MedusaDurableAdmissionPolicy.IsAllowedTransition(
                expectedState,
                targetState))
        {
            throw new ArgumentException(
                $"Transition {expectedState} -> {targetState} is not allowed.",
                nameof(targetState));
        }
        if ((targetState == MedusaAdmissionState.RosterTransferCommitted) !=
            (barrierEvidence is not null))
        {
            throw new ArgumentException(
                "Only the roster-transfer barrier requires stage evidence.",
                nameof(barrierEvidence));
        }
        if (MedusaDurableAdmissionPolicy.IsCleanupCompletedState(targetState) !=
            (cleanupEvidence is not null))
        {
            throw new ArgumentException(
                "Only cleanup-completed transitions require cleanup evidence.",
                nameof(cleanupEvidence));
        }

        TransitionId = transitionId;
        AdmissionId = admissionId;
        ExpectedState = expectedState;
        TargetState = targetState;
        BarrierEvidence = barrierEvidence;
        CleanupEvidence = cleanupEvidence;
        OccurredAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            occurredAtUtc,
            nameof(occurredAtUtc));
        RequestHash = MedusaDurableAdmissionPolicy.ComputeTransitionHash(this);
    }

    public Guid TransitionId { get; }

    public MedusaAdmissionId AdmissionId { get; }

    public MedusaAdmissionState ExpectedState { get; }

    public MedusaAdmissionState TargetState { get; }

    public MedusaRosterTransferBarrierEvidence? BarrierEvidence { get; }

    public MedusaAdmissionCleanupEvidence? CleanupEvidence { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string RequestHash { get; }
}

internal sealed class MedusaAdmissionSnapshot
{
    public MedusaAdmissionSnapshot(
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        MedusaRealmDay realmDay,
        MedusaEncounterDifficulty difficulty,
        MapId contentMapId,
        MedusaAdmissionSource source,
        PartyAdmissionLease party,
        string encounterContentFingerprint,
        string rosterHash,
        string requestHash,
        MedusaAdmissionState state,
        long revision,
        MedusaRosterTransferBarrierEvidence? barrierEvidence,
        DateTimeOffset reservedAtUtc,
        DateTimeOffset? runtimeReadyAtUtc,
        DateTimeOffset? rosterTransferCommittedAtUtc,
        DateTimeOffset? consumedAtUtc,
        DateTimeOffset? terminalAtUtc,
        DateTimeOffset? releasedAtUtc,
        MedusaAdmissionCleanupEvidence? cleanupEvidence = null,
        DateTimeOffset? cleanupCompletedAtUtc = null)
    {
        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        RealmDay = realmDay;
        Difficulty = difficulty;
        ContentMapId = contentMapId;
        Source = source;
        Party = party ?? throw new ArgumentNullException(nameof(party));
        EncounterContentFingerprint = encounterContentFingerprint;
        RosterHash = rosterHash;
        RequestHash = requestHash;
        State = state;
        Revision = revision;
        BarrierEvidence = barrierEvidence;
        ReservedAtUtc = reservedAtUtc;
        RuntimeReadyAtUtc = runtimeReadyAtUtc;
        RosterTransferCommittedAtUtc = rosterTransferCommittedAtUtc;
        ConsumedAtUtc = consumedAtUtc;
        TerminalAtUtc = terminalAtUtc;
        ReleasedAtUtc = releasedAtUtc;
        CleanupEvidence = cleanupEvidence;
        CleanupCompletedAtUtc = cleanupCompletedAtUtc;

        MedusaDurableAdmissionPolicy.ValidateSnapshot(this);
    }

    public MedusaAdmissionId AdmissionId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public MedusaRealmDay RealmDay { get; }

    public MedusaEncounterDifficulty Difficulty { get; }

    public MapId ContentMapId { get; }

    public MedusaAdmissionSource Source { get; }

    public PartyAdmissionLease Party { get; }

    public string EncounterContentFingerprint { get; }

    public string RosterHash { get; }

    public string RequestHash { get; }

    public MedusaAdmissionState State { get; }

    public long Revision { get; }

    public MedusaRosterTransferBarrierEvidence? BarrierEvidence { get; }

    public DateTimeOffset ReservedAtUtc { get; }

    public DateTimeOffset? RuntimeReadyAtUtc { get; }

    /// <summary>
    /// Irreversible durable barrier written after a hidden all-or-none roster
    /// stage and before its public commit. Release is forbidden at and after
    /// this point; recovery must replay commit and consumption.
    /// </summary>
    public DateTimeOffset? RosterTransferCommittedAtUtc { get; }

    public DateTimeOffset? ConsumedAtUtc { get; }

    /// <summary>
    /// Immutable completion, abandonment, or timeout evidence. Consumed
    /// attempt claims remain durable for every post-consumption outcome.
    /// </summary>
    public DateTimeOffset? TerminalAtUtc { get; }

    public DateTimeOffset? ReleasedAtUtc { get; }

    public MedusaAdmissionCleanupEvidence? CleanupEvidence { get; }

    public DateTimeOffset? CleanupCompletedAtUtc { get; }

    public DateTimeOffset LastChangedAtUtc =>
        CleanupCompletedAtUtc ??
        ReleasedAtUtc ??
        TerminalAtUtc ??
        ConsumedAtUtc ??
        RosterTransferCommittedAtUtc ??
        RuntimeReadyAtUtc ??
        ReservedAtUtc;
}

internal sealed record MedusaAdmissionReceipt(
    MedusaAdmissionReceiptStatus Status,
    MedusaAdmissionId AdmissionId,
    MedusaAdmissionState? CommittedState,
    long? CommittedRevision,
    MedusaAdmissionSnapshot? Snapshot)
{
    public bool IsSuccess =>
        Status is MedusaAdmissionReceiptStatus.Applied or
            MedusaAdmissionReceiptStatus.Duplicate;
}

/// <summary>
/// Narrow persistence seam for a future admission saga. It deliberately has
/// no party lookup, NPC handler, runtime creation, transfer, or reward API.
/// </summary>
internal interface IMedusaDurableAdmissionStore
{
    Task<MedusaAdmissionReceipt> ReserveAsync(
        MedusaAdmissionReservationRequest request,
        CancellationToken cancellationToken = default);

    Task<MedusaAdmissionReceipt> TransitionAsync(
        MedusaAdmissionTransitionRequest request,
        CancellationToken cancellationToken = default);

    Task<MedusaAdmissionSnapshot?> FindAsync(
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken = default);
}
