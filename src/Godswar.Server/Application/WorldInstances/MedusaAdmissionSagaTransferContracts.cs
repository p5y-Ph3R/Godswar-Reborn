using System.Collections.Immutable;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaRosterTransferStageToken
{
    public MedusaRosterTransferStageToken(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Roster-transfer stage tokens cannot be empty.",
                nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;
}

internal sealed class MedusaRosterTransferPrepareRequest
{
    public MedusaRosterTransferPrepareRequest(
        Guid operationId,
        MedusaAdmissionSnapshot admission,
        MedusaPendingStartToken transferToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (operationId !=
            MedusaAdmissionSagaOperationIds.TransferPrepare(
                admission.AdmissionId))
        {
            throw new ArgumentException(
                "Roster-transfer preparation requires its deterministic operation ID.",
                nameof(operationId));
        }
        if (admission.State != MedusaAdmissionState.RuntimeReady ||
            admission.BarrierEvidence is not null)
        {
            throw new ArgumentException(
                "Only a pre-barrier RuntimeReady admission may be staged.",
                nameof(admission));
        }
        if (!transferToken.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(transferToken));
        }
        var expectedTransferToken = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                admission.AdmissionId,
                admission.RequestHash));
        if (transferToken != expectedTransferToken)
        {
            throw new ArgumentException(
                "Roster preparation requires the deterministic runtime token.",
                nameof(transferToken));
        }
        OperationId = operationId;
        AdmissionId = admission.AdmissionId;
        WorldInstanceId = admission.WorldInstanceId;
        RosterHash = admission.RosterHash;
        Party = admission.Party;
        Source = admission.Source;
        TransferToken = transferToken;
    }

    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string RosterHash { get; }
    public PartyAdmissionLease Party { get; }
    public MedusaAdmissionSource Source { get; }
    public MedusaPendingStartToken TransferToken { get; }
}

internal enum MedusaRosterTransferPrepareStatus : byte
{
    PreparedHidden = 1,
    ExactReplay = 2,
    RejectedNoChange = 3,
    IdentityConflict = 4
}

internal sealed class MedusaRosterTransferPrepareResult
{
    public MedusaRosterTransferPrepareResult(
        MedusaRosterTransferPrepareStatus status,
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        string rosterHash,
        ImmutableArray<int> orderedCharacterIds,
        MedusaRosterTransferStageToken stageToken,
        string? preparationHash,
        DateTimeOffset? preparedAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        if (!Enum.IsDefined(status) || !admissionId.IsValid ||
            !worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A transfer result requires exact admission identity.");
        }
        MedusaDurableAdmissionPolicy.ValidateHash(rosterHash, nameof(rosterHash));
        var succeeded = status is
            MedusaRosterTransferPrepareStatus.PreparedHidden or
            MedusaRosterTransferPrepareStatus.ExactReplay;
        if (succeeded)
        {
            MedusaDurableAdmissionPolicy.ValidateHash(
                preparationHash!,
                nameof(preparationHash));
            preparedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
                preparedAtUtc ?? throw new ArgumentNullException(nameof(preparedAtUtc)),
                nameof(preparedAtUtc));
            expiresAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
                expiresAtUtc ?? throw new ArgumentNullException(nameof(expiresAtUtc)),
                nameof(expiresAtUtc));
            if (!stageToken.IsValid || expiresAtUtc <= preparedAtUtc ||
                !HasValidRoster(orderedCharacterIds))
            {
                throw new ArgumentException(
                    "A successful hidden stage requires bounded exact evidence.");
            }
        }
        else if (stageToken.IsValid || preparationHash is not null ||
                 preparedAtUtc is not null || expiresAtUtc is not null ||
                 !orderedCharacterIds.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A rejected hidden stage cannot expose staged authority.");
        }

        Status = status;
        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        RosterHash = rosterHash;
        OrderedCharacterIds = orderedCharacterIds;
        StageToken = stageToken;
        PreparationHash = preparationHash;
        PreparedAtUtc = preparedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public MedusaRosterTransferPrepareStatus Status { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string RosterHash { get; }
    public ImmutableArray<int> OrderedCharacterIds { get; }
    public MedusaRosterTransferStageToken StageToken { get; }
    public string? PreparationHash { get; }
    public DateTimeOffset? PreparedAtUtc { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool Succeeded => Status is
        MedusaRosterTransferPrepareStatus.PreparedHidden or
        MedusaRosterTransferPrepareStatus.ExactReplay;

    private static bool HasValidRoster(ImmutableArray<int> characterIds) =>
        !characterIds.IsDefaultOrEmpty &&
        characterIds.Length <= MedusaIslandPolicy.MaximumPartySize &&
        characterIds.All(static id => id > 0) &&
        characterIds.Distinct().Count() == characterIds.Length;
}

/// <summary>
/// Store-derived proof that a pre-barrier hidden stage must be removed. Exact
/// cleanup is reconstructible from stable admission identity after restart;
/// it never depends on an in-memory stage token or receipt.
/// </summary>
internal sealed class MedusaRosterTransferAbortPermit
{
    private MedusaRosterTransferAbortPermit(MedusaAdmissionSnapshot released)
    {
        OperationId = MedusaAdmissionSagaOperationIds.TransferAbort(
            released.AdmissionId);
        AdmissionId = released.AdmissionId;
        WorldInstanceId = released.WorldInstanceId;
        AdmissionRequestHash = released.RequestHash;
        RosterHash = released.RosterHash;
        ReleasedRevision = released.Revision;
        ReleasedAtUtc = released.ReleasedAtUtc!.Value;
    }

    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string AdmissionRequestHash { get; }
    public string RosterHash { get; }
    public long ReleasedRevision { get; }
    public DateTimeOffset ReleasedAtUtc { get; }

    internal static bool TryCreate(
        MedusaAdmissionSnapshot released,
        out MedusaRosterTransferAbortPermit permit)
    {
        ArgumentNullException.ThrowIfNull(released);
        if (released.State != MedusaAdmissionState.Released ||
            released.ReleasedAtUtc is null ||
            released.BarrierEvidence is not null)
        {
            permit = null!;
            return false;
        }
        permit = new(released);
        return true;
    }
}

internal enum MedusaRosterTransferAbortStatus : byte
{
    Aborted = 1,
    ExactReplay = 2,
    IdentityConflict = 3
}

/// <summary>
/// Store-derived proof that the irreversible transfer barrier is durable.
/// It remains reproducible while the barrier is being completed or the run is
/// active so crash recovery/reconnect can replay the exact public commit.
/// Terminal admissions deliberately cannot mint re-entry authority.
/// </summary>
internal sealed class MedusaRosterTransferBarrierPermit
{
    private MedusaRosterTransferBarrierPermit(MedusaAdmissionSnapshot snapshot)
    {
        AdmissionId = snapshot.AdmissionId;
        WorldInstanceId = snapshot.WorldInstanceId;
        AdmissionRequestHash = snapshot.RequestHash;
        RosterHash = snapshot.RosterHash;
        Party = snapshot.Party;
        Source = snapshot.Source;
        ContentMapId = snapshot.ContentMapId;
        CommittedAtUtc = snapshot.RosterTransferCommittedAtUtc!.Value;
        StageId = snapshot.BarrierEvidence!.StageId;
        PreparationHash = snapshot.BarrierEvidence.PreparationHash;
    }

    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string AdmissionRequestHash { get; }
    public string RosterHash { get; }
    public PartyAdmissionLease Party { get; }
    public MedusaAdmissionSource Source { get; }
    public MapId ContentMapId { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public Guid StageId { get; }
    public string PreparationHash { get; }

    internal static bool TryCreate(
        MedusaAdmissionSnapshot snapshot,
        out MedusaRosterTransferBarrierPermit permit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var isPostBarrier = snapshot.State is
            MedusaAdmissionState.RosterTransferCommitted or
            MedusaAdmissionState.ConsumedRunning;
        if (!isPostBarrier || snapshot.BarrierEvidence is null ||
            snapshot.RosterTransferCommittedAtUtc is null)
        {
            permit = null!;
            return false;
        }
        permit = new(snapshot);
        return true;
    }
}

/// <summary>
/// Exact public commit of one durably authorized stage. Commit must recreate
/// or promote the same hidden stage from the barrier permit even if its
/// pre-barrier lease expired or the gateway process restarted.
/// </summary>
internal sealed class MedusaRosterTransferCommitRequest
{
    public MedusaRosterTransferCommitRequest(
        Guid operationId,
        MedusaRosterTransferBarrierPermit barrierPermit,
        MedusaPendingStartToken runtimeTransferToken)
    {
        ArgumentNullException.ThrowIfNull(barrierPermit);
        if (operationId !=
                MedusaAdmissionSagaOperationIds.TransferCommit(
                    barrierPermit.AdmissionId) ||
            !runtimeTransferToken.IsValid)
        {
            throw new ArgumentException(
                "Roster commit requires deterministic complete identity.");
        }
        var expectedTransferToken = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                barrierPermit.AdmissionId,
                barrierPermit.AdmissionRequestHash));
        if (runtimeTransferToken != expectedTransferToken)
        {
            throw new ArgumentException(
                "Roster commit requires the deterministic runtime token.",
                nameof(runtimeTransferToken));
        }
        OperationId = operationId;
        BarrierPermit = barrierPermit;
        RuntimeTransferToken = runtimeTransferToken;
        StageToken = new MedusaRosterTransferStageToken(barrierPermit.StageId);
    }

    public Guid OperationId { get; }
    public MedusaRosterTransferBarrierPermit BarrierPermit { get; }
    public MedusaAdmissionId AdmissionId => BarrierPermit.AdmissionId;
    public WorldInstanceId WorldInstanceId => BarrierPermit.WorldInstanceId;
    public string RosterHash => BarrierPermit.RosterHash;
    public MedusaPendingStartToken RuntimeTransferToken { get; }
    public MedusaRosterTransferStageToken StageToken { get; }
}

internal enum MedusaRosterTransferCommitStatus : byte
{
    AtomicCommitted = 1,
    ExactReplay = 2,
    IdentityConflict = 3,
    OwnershipUnavailable = 4,
    SupersededByEgress = 5
}

internal sealed class MedusaRosterTransferCommitResult
{
    public MedusaRosterTransferCommitResult(
        MedusaRosterTransferCommitStatus status,
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        string rosterHash,
        ImmutableArray<int> orderedCharacterIds,
        DateTimeOffset? committedAtUtc)
    {
        if (!Enum.IsDefined(status) || !admissionId.IsValid ||
            !worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A commit result requires valid exact identity.");
        }
        MedusaDurableAdmissionPolicy.ValidateHash(rosterHash, nameof(rosterHash));
        var succeeded = status is
            MedusaRosterTransferCommitStatus.AtomicCommitted or
            MedusaRosterTransferCommitStatus.ExactReplay;
        if (succeeded)
        {
            committedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
                committedAtUtc ??
                    throw new ArgumentNullException(nameof(committedAtUtc)),
                nameof(committedAtUtc));
            if (orderedCharacterIds.IsDefaultOrEmpty ||
                orderedCharacterIds.Length >
                    MedusaIslandPolicy.MaximumPartySize ||
                orderedCharacterIds.Any(static id => id <= 0) ||
                orderedCharacterIds.Distinct().Count() !=
                    orderedCharacterIds.Length)
            {
                throw new ArgumentException(
                    "A successful commit requires an exact bounded roster.",
                    nameof(orderedCharacterIds));
            }
        }
        else if (!orderedCharacterIds.IsDefaultOrEmpty ||
                 committedAtUtc is not null)
        {
            throw new ArgumentException(
                "A rejected commit cannot expose public transfer evidence.");
        }

        Status = status;
        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        RosterHash = rosterHash;
        OrderedCharacterIds = orderedCharacterIds;
        CommittedAtUtc = committedAtUtc;
    }

    public MedusaRosterTransferCommitStatus Status { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string RosterHash { get; }
    public ImmutableArray<int> OrderedCharacterIds { get; }
    public DateTimeOffset? CommittedAtUtc { get; }

    public bool Succeeded => Status is
        MedusaRosterTransferCommitStatus.AtomicCommitted or
        MedusaRosterTransferCommitStatus.ExactReplay;
}

/// <summary>
/// Prepare is all-or-none and hidden: it may reserve target ECS/placement
/// capacity but may not change a session route, scene, source checkpoint, or
/// client-visible state. Before the barrier it has a bounded lease and aborts
/// by stable AdmissionId. After the barrier Commit must reconstruct/promote
/// the exact stage despite lease expiry or process loss. No adapter to current
/// per-character transfer primitives is provided.
/// The implementation must keep a causal per-admission assignment ledger:
/// Abort must durably tombstone pre-barrier preparation, and exact egress must
/// durably tombstone both Prepare and Commit. After either cleanup path,
/// process loss and stale capabilities are permanent no-ops/rejections and
/// can never recreate hidden capacity or resurrect a route.
/// </summary>
internal interface IMedusaAtomicRosterTransferGateway
{
    Task<MedusaRosterTransferPrepareResult> PrepareAsync(
        MedusaRosterTransferPrepareRequest request,
        CancellationToken cancellationToken = default);

    Task<MedusaRosterTransferCommitResult> CommitAsync(
        MedusaRosterTransferCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<MedusaRosterTransferAbortResult> AbortPreparedAsync(
        MedusaRosterTransferAbortPermit permit,
        CancellationToken cancellationToken = default);

    Task<MedusaRosterEgressResult> EgressAsync(
        MedusaRosterEgressPermit permit,
        CancellationToken cancellationToken = default);
}
