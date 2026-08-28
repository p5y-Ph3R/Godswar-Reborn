using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Unwired durable admission orchestration. All external capabilities are
/// intentionally abstract: no current party observation, per-character map
/// transfer, or active Medusa runtime API is adapted to these interfaces.
/// </summary>
internal sealed partial class MedusaAdmissionSagaCoordinator
{
    private readonly RealmCalendar _calendar;
    private readonly TimeProvider _clock;
    private readonly IMedusaPartyAdmissionAuthority _partyAuthority;
    private readonly IMedusaDurableAdmissionStore _store;
    private readonly IMedusaPendingStartRuntimeGateway _runtimeGateway;
    private readonly IMedusaAtomicRosterTransferGateway _transferGateway;

    public MedusaAdmissionSagaCoordinator(
        RealmCalendar calendar,
        TimeProvider clock,
        IMedusaPartyAdmissionAuthority partyAuthority,
        IMedusaDurableAdmissionStore store,
        IMedusaPendingStartRuntimeGateway runtimeGateway,
        IMedusaAtomicRosterTransferGateway transferGateway)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _partyAuthority = partyAuthority ??
            throw new ArgumentNullException(nameof(partyAuthority));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtimeGateway = runtimeGateway ??
            throw new ArgumentNullException(nameof(runtimeGateway));
        _transferGateway = transferGateway ??
            throw new ArgumentNullException(nameof(transferGateway));
    }

    public async Task<MedusaAdmissionSagaResult> ExecuteAsync(
        MedusaAdmissionStartCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var loaded = await LoadOrReserveAsync(command, cancellationToken);
        if (loaded.Failure is not null)
        {
            return loaded.Failure;
        }
        var admission = loaded.Admission!;
        if (!MatchesCommand(admission, command))
        {
            return new(
                MedusaAdmissionSagaStatus.InvalidCommand,
                admission,
                null);
        }
        if (admission.State == MedusaAdmissionState.ReleasedCleaned)
        {
            return new(
                MedusaAdmissionSagaStatus.Released,
                admission,
                null);
        }
        if (admission.State == MedusaAdmissionState.Released)
        {
            return await CleanupReleasedAsync(
                admission,
                MedusaAdmissionSagaStatus.Released,
                cancellationToken);
        }
        if (admission.State is
                MedusaAdmissionState.Completed or
                MedusaAdmissionState.Abandoned or
                MedusaAdmissionState.TimedOut)
        {
            return await CleanupTerminalAsync(admission, cancellationToken);
        }
        if (IsTerminal(admission.State))
        {
            return new(
                MedusaAdmissionSagaStatus.AlreadyTerminal,
                admission,
                null);
        }

        var ensure = await _runtimeGateway.EnsurePendingStartAsync(
            new MedusaPendingStartRuntimeRequest(admission),
            cancellationToken);
        if (ensure is null)
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                null);
        }
        if (!ensure.Succeeded || ensure.Snapshot is null ||
            !MatchesRuntime(admission, ensure.Snapshot))
        {
            var canCompensateExactRuntime =
                admission.State is
                    MedusaAdmissionState.Reserved or
                    MedusaAdmissionState.RuntimeReady &&
                (ensure.Snapshot is null ||
                 MatchesRuntimeIdentity(admission, ensure.Snapshot));
            return canCompensateExactRuntime
                ? await CompensateAsync(
                    admission,
                    MedusaAdmissionSagaStatus.RuntimeRejectedCompensated,
                    ensure.Snapshot?.PreparedAtUtc,
                    cancellationToken)
                : new(
                    MedusaAdmissionSagaStatus.ReconcileRequired,
                    admission,
                    ensure.Snapshot);
        }
        var runtime = ensure.Snapshot;
        if (admission.State == MedusaAdmissionState.Reserved &&
            runtime.PreparedAtUtc > UtcNow())
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                runtime);
        }

        if (admission.State == MedusaAdmissionState.Reserved)
        {
            var ready = await _store.TransitionAsync(
                new MedusaAdmissionTransitionRequest(
                    MedusaAdmissionSagaOperationIds.RuntimeReady(
                        admission.AdmissionId),
                    admission.AdmissionId,
                    MedusaAdmissionState.Reserved,
                    MedusaAdmissionState.RuntimeReady,
                    runtime.PreparedAtUtc),
                cancellationToken);
            if (!TryAcceptState(
                    ready,
                    MedusaAdmissionState.RuntimeReady,
                    out admission))
            {
                if (ready.Snapshot?.State == MedusaAdmissionState.Released)
                {
                    return await CleanupReleasedAsync(
                        ready.Snapshot,
                        MedusaAdmissionSagaStatus.RuntimeRejectedCompensated,
                        cancellationToken);
                }
                return new(
                    MedusaAdmissionSagaStatus.ReconcileRequired,
                    ready.Snapshot ?? admission,
                    runtime);
            }
            if (!MatchesRuntime(admission, runtime))
            {
                return new(
                    MedusaAdmissionSagaStatus.ReconcileRequired,
                    admission,
                    runtime);
            }
        }

        if (admission.State == MedusaAdmissionState.RuntimeReady)
        {
            var transfer = await PrepareAndBarrierAsync(
                admission,
                runtime,
                cancellationToken);
            if (transfer.Failure is not null)
            {
                return transfer.Failure;
            }
            admission = transfer.Admission!;
        }

        if (!MedusaRosterTransferBarrierPermit.TryCreate(
                admission,
                out var barrierPermit))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                runtime);
        }
        var commit = await _transferGateway.CommitAsync(
            new MedusaRosterTransferCommitRequest(
                MedusaAdmissionSagaOperationIds.TransferCommit(
                    admission.AdmissionId),
                barrierPermit,
                runtime.TransferToken),
            cancellationToken);
        if (!MatchesCommit(admission, commit))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                runtime);
        }

        if (admission.State == MedusaAdmissionState.RosterTransferCommitted)
        {
            var consumed = await _store.TransitionAsync(
                new MedusaAdmissionTransitionRequest(
                    MedusaAdmissionSagaOperationIds.ConsumedRunning(
                        admission.AdmissionId),
                    admission.AdmissionId,
                    MedusaAdmissionState.RosterTransferCommitted,
                    MedusaAdmissionState.ConsumedRunning,
                    commit.CommittedAtUtc!.Value),
                cancellationToken);
            if (!TryAcceptState(
                    consumed,
                    MedusaAdmissionState.ConsumedRunning,
                    out admission))
            {
                if (consumed.Snapshot is { } concurrent &&
                    IsTerminal(concurrent.State))
                {
                    return new(
                        MedusaAdmissionSagaStatus.AlreadyTerminal,
                        concurrent,
                        runtime);
                }
                return new(
                    MedusaAdmissionSagaStatus.ReconcileRequired,
                    consumed.Snapshot ?? admission,
                    runtime);
            }
        }

        if (!MedusaRuntimeStartPermit.TryCreate(
                admission,
                runtime,
                out var startPermit))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                runtime);
        }
        var start = await _runtimeGateway.StartAsync(
            startPermit,
            cancellationToken);
        if (start is null || !start.Succeeded || start.Snapshot is null ||
            !MatchesRuntime(admission, start.Snapshot) ||
            start.Snapshot.State != MedusaPendingRuntimeState.Running ||
            start.Snapshot.StartedAtUtc != admission.ConsumedAtUtc)
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                start?.Snapshot ?? runtime);
        }
        return new(
            start.Status == MedusaPendingRuntimeStatus.ExactReplay
                ? MedusaAdmissionSagaStatus.AlreadyRunning
                : MedusaAdmissionSagaStatus.Running,
            admission,
            start.Snapshot);
    }

    private async Task<(
        MedusaAdmissionSnapshot? Admission,
        MedusaAdmissionSagaResult? Failure)> LoadOrReserveAsync(
        MedusaAdmissionStartCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await _store.FindAsync(
            command.Operation.AdmissionId,
            cancellationToken);
        if (existing is not null)
        {
            return (existing, null);
        }
        if (command.ReceivedAtUtc > UtcNow())
        {
            return (
                null,
                new(
                    MedusaAdmissionSagaStatus.InvalidCommand,
                    null,
                    null));
        }

        var realmDay = GetRealmDay(command.ReceivedAtUtc);
        var partyResult = await _partyAuthority.AcquireAsync(
            new MedusaPartyLeaseAcquisitionRequest(
                command.Operation,
                realmDay,
                command.Difficulty,
                command.Source,
                command.RequestingAccountId,
                command.RequestingCharacterId,
                command.RequestingOwnership,
                command.ReceivedAtUtc),
            cancellationToken);
        if (partyResult is null || !partyResult.Succeeded ||
            partyResult.Lease is null ||
            !IsTrustedLease(partyResult.Lease, command))
        {
            var status = partyResult?.Status ==
                MedusaPartyLeaseAcquisitionStatus.Unavailable
                ? MedusaAdmissionSagaStatus.PartyUnavailable
                : MedusaAdmissionSagaStatus.PartyRejected;
            return (null, new(status, null, null));
        }

        var reservation = new MedusaAdmissionReservationRequest(
            command.Operation.AdmissionId,
            command.Operation.WorldInstanceId,
            realmDay,
            command.Difficulty,
            command.Source,
            partyResult.Lease,
            command.EncounterContentFingerprint,
            command.ReceivedAtUtc);
        var receipt = await _store.ReserveAsync(reservation, cancellationToken);
        if (receipt.IsSuccess && receipt.Snapshot is { } snapshot &&
            snapshot.RequestHash == reservation.RequestHash)
        {
            return (snapshot, null);
        }
        var failure = receipt.Status switch
        {
            MedusaAdmissionReceiptStatus.MemberAttemptConflict =>
                MedusaAdmissionSagaStatus.MemberAttemptConflict,
            MedusaAdmissionReceiptStatus.MemberActiveAdmissionConflict =>
                MedusaAdmissionSagaStatus.MemberActiveAdmissionConflict,
            _ => MedusaAdmissionSagaStatus.ReservationConflict
        };
        return (receipt.Snapshot, new(failure, receipt.Snapshot, null));
    }

    private async Task<(
        MedusaAdmissionSnapshot? Admission,
        MedusaAdmissionSagaResult? Failure)> PrepareAndBarrierAsync(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        var prepare = await _transferGateway.PrepareAsync(
            new MedusaRosterTransferPrepareRequest(
                MedusaAdmissionSagaOperationIds.TransferPrepare(
                    admission.AdmissionId),
                admission,
                runtime.TransferToken),
            cancellationToken);
        var barrierNow = UtcNow();
        if (!MatchesPrepare(admission, runtime, prepare) ||
            prepare.PreparedAtUtc > barrierNow ||
            prepare.ExpiresAtUtc <= barrierNow ||
            !admission.Party.IsValidAt(barrierNow))
        {
            return (
                null,
                await CompensateAsync(
                    admission,
                    MedusaAdmissionSagaStatus.TransferRejectedCompensated,
                    barrierNow,
                    cancellationToken));
        }

        var barrier = await _store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.TransferBarrier(
                    admission.AdmissionId),
                admission.AdmissionId,
                MedusaAdmissionState.RuntimeReady,
                MedusaAdmissionState.RosterTransferCommitted,
                prepare.PreparedAtUtc!.Value,
                new MedusaRosterTransferBarrierEvidence(
                    prepare.StageToken.Value,
                    prepare.PreparationHash!)),
            cancellationToken);
        if (TryAcceptState(
                barrier,
                MedusaAdmissionState.RosterTransferCommitted,
                out var committed) &&
            committed.BarrierEvidence?.StageId == prepare.StageToken.Value &&
            committed.BarrierEvidence.PreparationHash == prepare.PreparationHash)
        {
            return (committed, null);
        }
        if (barrier.Snapshot?.State == MedusaAdmissionState.Released)
        {
            return (
                null,
                await CleanupReleasedAsync(
                    barrier.Snapshot,
                    MedusaAdmissionSagaStatus.TransferRejectedCompensated,
                    cancellationToken));
        }
        return (
            null,
            new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                barrier.Snapshot ?? admission,
                runtime));
    }

}
