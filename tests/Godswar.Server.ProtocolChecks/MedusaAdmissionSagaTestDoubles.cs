using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal sealed class MedusaSagaMemoryStore(List<string> events) :
    IMedusaDurableAdmissionStore
{
    private readonly Dictionary<MedusaAdmissionId, MedusaAdmissionSnapshot>
        _admissions = [];
    private readonly Dictionary<(MedusaAdmissionId, Guid),
        (string Hash, MedusaAdmissionState State, long Revision)> _receipts = [];

    public int ReserveCalls { get; private set; }
    public bool ReleaseWinsOnNextBarrier { get; set; }
    public MedusaAdmissionState? ConflictNextTarget { get; set; }
    public MedusaAdmissionState? ThrowBeforeNextTarget { get; set; }
    public MedusaAdmissionState? ThrowAfterNextTarget { get; set; }
    public Action<MedusaAdmissionSnapshot>? AfterTransition { get; set; }

    public Task<MedusaAdmissionReceipt> ReserveAsync(
        MedusaAdmissionReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReserveCalls++;
        events.Add("reserve");
        if (_admissions.TryGetValue(request.AdmissionId, out var existing))
        {
            return Task.FromResult(new MedusaAdmissionReceipt(
                existing.RequestHash == request.RequestHash
                    ? MedusaAdmissionReceiptStatus.Duplicate
                    : MedusaAdmissionReceiptStatus.RequestConflict,
                request.AdmissionId,
                existing.State,
                existing.Revision,
                existing));
        }
        var snapshot = new MedusaAdmissionSnapshot(
            request.AdmissionId,
            request.WorldInstanceId,
            request.RealmDay,
            request.Difficulty,
            request.ContentMapId,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RosterHash,
            request.RequestHash,
            MedusaAdmissionState.Reserved,
            1,
            null,
            request.RequestedAtUtc,
            null,
            null,
            null,
            null,
            null);
        _admissions.Add(request.AdmissionId, snapshot);
        return Task.FromResult(Applied(snapshot));
    }

    public Task<MedusaAdmissionReceipt> TransitionAsync(
        MedusaAdmissionTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_admissions.TryGetValue(request.AdmissionId, out var current))
        {
            return Task.FromResult(new MedusaAdmissionReceipt(
                MedusaAdmissionReceiptStatus.NotFound,
                request.AdmissionId,
                null,
                null,
                null));
        }
        if (_receipts.TryGetValue(
                (request.AdmissionId, request.TransitionId),
                out var receipt))
        {
            return Task.FromResult(new MedusaAdmissionReceipt(
                receipt.Hash == request.RequestHash
                    ? MedusaAdmissionReceiptStatus.Duplicate
                    : MedusaAdmissionReceiptStatus.RequestConflict,
                request.AdmissionId,
                receipt.State,
                receipt.Revision,
                current));
        }

        if (ReleaseWinsOnNextBarrier && request.TargetState ==
                MedusaAdmissionState.RosterTransferCommitted)
        {
            ReleaseWinsOnNextBarrier = false;
            var release = new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.DurableRelease(
                    current.AdmissionId),
                current.AdmissionId,
                current.State,
                MedusaAdmissionState.Released,
                current.LastChangedAtUtc);
            current = Apply(current, release);
            _admissions[current.AdmissionId] = current;
            events.Add("transition:Released");
            return Task.FromResult(new MedusaAdmissionReceipt(
                MedusaAdmissionReceiptStatus.InvalidTransition,
                request.AdmissionId,
                null,
                null,
                current));
        }

        if (current.State != request.ExpectedState ||
            request.OccurredAtUtc < current.LastChangedAtUtc)
        {
            return Task.FromResult(new MedusaAdmissionReceipt(
                MedusaAdmissionReceiptStatus.InvalidTransition,
                request.AdmissionId,
                null,
                null,
                current));
        }
        if (ThrowBeforeNextTarget == request.TargetState)
        {
            ThrowBeforeNextTarget = null;
            throw new InvalidOperationException(
                $"Injected failure before {request.TargetState} commit.");
        }
        var updated = Apply(current, request);
        _admissions[request.AdmissionId] = updated;
        if (ConflictNextTarget == request.TargetState)
        {
            ConflictNextTarget = null;
            events.Add($"conflict:{updated.State}");
            return Task.FromResult(new MedusaAdmissionReceipt(
                MedusaAdmissionReceiptStatus.RequestConflict,
                request.AdmissionId,
                null,
                null,
                updated));
        }
        _receipts.Add(
            (request.AdmissionId, request.TransitionId),
            (request.RequestHash, updated.State, updated.Revision));
        events.Add($"transition:{updated.State}");
        AfterTransition?.Invoke(updated);
        if (ThrowAfterNextTarget == request.TargetState)
        {
            ThrowAfterNextTarget = null;
            throw new InvalidOperationException(
                $"Injected lost {updated.State} receipt.");
        }
        return Task.FromResult(Applied(updated));
    }

    public Task<MedusaAdmissionSnapshot?> FindAsync(
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _admissions.TryGetValue(admissionId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public MedusaAdmissionSnapshot Snapshot(MedusaAdmissionId admissionId) =>
        _admissions[admissionId];

    private static MedusaAdmissionReceipt Applied(
        MedusaAdmissionSnapshot snapshot) =>
        new(
            MedusaAdmissionReceiptStatus.Applied,
            snapshot.AdmissionId,
            snapshot.State,
            snapshot.Revision,
            snapshot);

    private static MedusaAdmissionSnapshot Apply(
        MedusaAdmissionSnapshot current,
        MedusaAdmissionTransitionRequest request)
    {
        var ready = current.RuntimeReadyAtUtc;
        var barrierAt = current.RosterTransferCommittedAtUtc;
        var consumed = current.ConsumedAtUtc;
        var terminal = current.TerminalAtUtc;
        var released = current.ReleasedAtUtc;
        var evidence = current.BarrierEvidence;
        var cleanupEvidence = current.CleanupEvidence;
        var cleanupCompleted = current.CleanupCompletedAtUtc;
        switch (request.TargetState)
        {
            case MedusaAdmissionState.RuntimeReady:
                ready = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.RosterTransferCommitted:
                barrierAt = request.OccurredAtUtc;
                evidence = request.BarrierEvidence;
                break;
            case MedusaAdmissionState.ConsumedRunning:
                consumed = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.Completed:
            case MedusaAdmissionState.Abandoned:
            case MedusaAdmissionState.TimedOut:
                terminal = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.Released:
                released = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.CompletedCleaned:
            case MedusaAdmissionState.AbandonedCleaned:
            case MedusaAdmissionState.TimedOutCleaned:
            case MedusaAdmissionState.ReleasedCleaned:
                cleanupEvidence = request.CleanupEvidence;
                cleanupCompleted = request.OccurredAtUtc;
                break;
            default:
                throw new InvalidOperationException();
        }
        return new MedusaAdmissionSnapshot(
            current.AdmissionId,
            current.WorldInstanceId,
            current.RealmDay,
            current.Difficulty,
            current.ContentMapId,
            current.Source,
            current.Party,
            current.EncounterContentFingerprint,
            current.RosterHash,
            current.RequestHash,
            request.TargetState,
            current.Revision + 1,
            evidence,
            current.ReservedAtUtc,
            ready,
            barrierAt,
            consumed,
            terminal,
            released,
            cleanupEvidence,
            cleanupCompleted);
    }
}

internal sealed partial class MedusaSagaTransferGateway(List<string> events) :
    IMedusaAtomicRosterTransferGateway
{
    private readonly Dictionary<MedusaAdmissionId,
        (Guid StageId, string Hash, DateTimeOffset ExpiresAt)> _hidden = [];
    private readonly Dictionary<MedusaAdmissionId, DateTimeOffset> _committed = [];

    public DateTimeOffset PreparedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public bool RejectPrepare { get; set; }
    public int CommitCalls { get; private set; }
    public int PrepareCalls { get; private set; }
    public int AbortCalls { get; private set; }
    public int ReconstructedCommitCount { get; private set; }
    public int HiddenCount => _hidden.Count;
    public bool ThrowAfterNextCommit { get; set; }
    public MedusaRosterTransferPrepareRequest? LastPrepareRequest { get; private set; }

    public void LoseProcess() => _hidden.Clear();

    public Task<MedusaRosterTransferPrepareResult> PrepareAsync(
        MedusaRosterTransferPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPrepareRequest = request;
        PrepareCalls++;
        events.Add("prepare");
        if (RejectPrepare || _aborted.Contains(request.AdmissionId) ||
            _egressed.Contains(request.AdmissionId))
        {
            return Task.FromResult(new MedusaRosterTransferPrepareResult(
                MedusaRosterTransferPrepareStatus.RejectedNoChange,
                request.AdmissionId,
                request.WorldInstanceId,
                request.RosterHash,
                ImmutableArray<int>.Empty,
                default,
                null,
                null,
                null));
        }

        var stageId = request.OperationId;
        const string preparationHash =
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var replay = _hidden.ContainsKey(request.AdmissionId);
        _hidden[request.AdmissionId] =
            (stageId, preparationHash, ExpiresAtUtc);
        return Task.FromResult(new MedusaRosterTransferPrepareResult(
            replay
                ? MedusaRosterTransferPrepareStatus.ExactReplay
                : MedusaRosterTransferPrepareStatus.PreparedHidden,
            request.AdmissionId,
            request.WorldInstanceId,
            request.RosterHash,
            request.Party.Members
                .Select(static member => member.CharacterId)
                .ToImmutableArray(),
            new MedusaRosterTransferStageToken(stageId),
            preparationHash,
            PreparedAtUtc,
            ExpiresAtUtc));
    }

    public Task<MedusaRosterTransferCommitResult> CommitAsync(
        MedusaRosterTransferCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitCalls++;
        events.Add("commit");
        if (_egressed.Contains(request.AdmissionId))
        {
            return Task.FromResult(new MedusaRosterTransferCommitResult(
                MedusaRosterTransferCommitStatus.SupersededByEgress,
                request.AdmissionId,
                request.WorldInstanceId,
                request.RosterHash,
                ImmutableArray<int>.Empty,
                null));
        }
        if (!_hidden.TryGetValue(request.AdmissionId, out var stage))
        {
            // The durable barrier is sufficient to reconstruct the exact
            // hidden stage after pre-barrier lease expiry/process loss.
            ReconstructedCommitCount++;
            stage = (
                request.BarrierPermit.StageId,
                request.BarrierPermit.PreparationHash,
                request.BarrierPermit.CommittedAtUtc);
        }
        if (stage.StageId != request.BarrierPermit.StageId ||
            stage.Hash != request.BarrierPermit.PreparationHash)
        {
            return Task.FromResult(new MedusaRosterTransferCommitResult(
                MedusaRosterTransferCommitStatus.IdentityConflict,
                request.AdmissionId,
                request.WorldInstanceId,
                request.RosterHash,
                ImmutableArray<int>.Empty,
                null));
        }
        _hidden.Remove(request.AdmissionId);
        var replay = _committed.TryGetValue(
            request.AdmissionId,
            out var committedAt);
        if (!replay)
        {
            committedAt = CommittedAtUtc;
            _committed.Add(request.AdmissionId, committedAt);
        }
        var result = new MedusaRosterTransferCommitResult(
            replay
                ? MedusaRosterTransferCommitStatus.ExactReplay
                : MedusaRosterTransferCommitStatus.AtomicCommitted,
            request.AdmissionId,
            request.WorldInstanceId,
            request.RosterHash,
            request.BarrierPermit.Party.Members
                .Select(static member => member.CharacterId)
                .ToImmutableArray(),
            committedAt);
        if (ThrowAfterNextCommit)
        {
            ThrowAfterNextCommit = false;
            throw new InvalidOperationException(
                "Injected lost atomic-commit response.");
        }
        return Task.FromResult(result);
    }

    public Task<MedusaRosterTransferAbortResult> AbortPreparedAsync(
        MedusaRosterTransferAbortPermit permit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AbortCalls++;
        events.Add("abort");
        if (ForcedAbortResult is { } forced)
        {
            return Task.FromResult(forced);
        }
        var first = _aborted.Add(permit.AdmissionId);
        _hidden.Remove(permit.AdmissionId);
        LastAbortResult = new MedusaRosterTransferAbortResult(
            first
                ? MedusaRosterTransferAbortStatus.Aborted
                : MedusaRosterTransferAbortStatus.ExactReplay,
            permit.OperationId,
            permit.AdmissionId,
            permit.WorldInstanceId,
            permit.AdmissionRequestHash,
            permit.RosterHash,
            permit.ReleasedRevision,
            permit.ReleasedAtUtc);
        return Task.FromResult(LastAbortResult);
    }
}
