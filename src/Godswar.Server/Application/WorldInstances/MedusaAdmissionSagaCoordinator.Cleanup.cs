namespace Godswar.Server.Application.WorldInstances;

internal sealed partial class MedusaAdmissionSagaCoordinator
{
    private async Task<MedusaAdmissionSagaResult> CompensateAsync(
        MedusaAdmissionSnapshot admission,
        MedusaAdmissionSagaStatus successStatus,
        DateTimeOffset? knownRuntimePreparedAtUtc,
        CancellationToken cancellationToken)
    {
        if (admission.State is not (
                MedusaAdmissionState.Reserved or
                MedusaAdmissionState.RuntimeReady))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                admission,
                null);
        }
        var release = await _store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.DurableRelease(
                    admission.AdmissionId),
                admission.AdmissionId,
                admission.State,
                MedusaAdmissionState.Released,
                knownRuntimePreparedAtUtc is { } preparedAt &&
                    preparedAt > admission.LastChangedAtUtc
                    ? preparedAt
                    : admission.LastChangedAtUtc),
            cancellationToken);
        if (!TryAcceptState(
                release,
                MedusaAdmissionState.Released,
                out var released))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                release.Snapshot ?? admission,
                null);
        }
        return await CleanupReleasedAsync(
            released,
            successStatus,
            cancellationToken);
    }

    private async Task<MedusaAdmissionSagaResult> CleanupReleasedAsync(
        MedusaAdmissionSnapshot released,
        MedusaAdmissionSagaStatus successStatus,
        CancellationToken cancellationToken)
    {
        if (!MedusaRosterTransferAbortPermit.TryCreate(released, out var abort) ||
            !MedusaRuntimeReleasePermit.TryCreate(released, out var retire))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                released,
                null);
        }
        var abortResult = await _transferGateway.AbortPreparedAsync(
            abort,
            cancellationToken);
        if (abortResult is null || !abortResult.Matches(abort))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                released,
                null);
        }
        var runtime = await _runtimeGateway.ReleaseEmptyAsync(
            retire,
            cancellationToken);
        if (!retire.Matches(runtime))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                released,
                runtime.Snapshot);
        }
        var cleanedReceipt = await _store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.CleanupCompleted(
                    released.AdmissionId),
                released.AdmissionId,
                MedusaAdmissionState.Released,
                MedusaAdmissionState.ReleasedCleaned,
                released.ReleasedAtUtc!.Value,
                cleanupEvidence: new MedusaAdmissionCleanupEvidence(
                    released.AdmissionId,
                    MedusaAdmissionCleanupKind.PreBarrierRelease,
                    abort.OperationId,
                    retire.OperationId)),
            cancellationToken);
        if (!TryAcceptState(
                cleanedReceipt,
                MedusaAdmissionState.ReleasedCleaned,
                out var cleaned))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                cleanedReceipt.Snapshot ?? released,
                runtime.Snapshot);
        }
        return new(successStatus, cleaned, runtime.Snapshot);
    }

    private async Task<MedusaAdmissionSagaResult> CleanupTerminalAsync(
        MedusaAdmissionSnapshot terminal,
        CancellationToken cancellationToken)
    {
        if (!MedusaRosterEgressPermit.TryCreate(terminal, out var egressPermit) ||
            !MedusaRuntimeRetirePermit.TryCreate(terminal, out var retirePermit))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                terminal,
                null);
        }

        var egress = await _transferGateway.EgressAsync(
            egressPermit,
            cancellationToken);
        if (egress is null || !egress.Matches(egressPermit))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                terminal,
                null);
        }

        var runtime = await _runtimeGateway.RetireTerminalAsync(
            retirePermit,
            cancellationToken);
        if (!retirePermit.Matches(runtime))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                terminal,
                runtime?.Snapshot);
        }

        var cleanedState = terminal.State switch
        {
            MedusaAdmissionState.Completed =>
                MedusaAdmissionState.CompletedCleaned,
            MedusaAdmissionState.Abandoned =>
                MedusaAdmissionState.AbandonedCleaned,
            MedusaAdmissionState.TimedOut =>
                MedusaAdmissionState.TimedOutCleaned,
            _ => throw new InvalidOperationException(
                "Only pending terminal states can complete cleanup.")
        };
        var cleanedReceipt = await _store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.CleanupCompleted(
                    terminal.AdmissionId),
                terminal.AdmissionId,
                terminal.State,
                cleanedState,
                terminal.TerminalAtUtc!.Value,
                cleanupEvidence: new MedusaAdmissionCleanupEvidence(
                    terminal.AdmissionId,
                    MedusaAdmissionCleanupKind.TerminalEgress,
                    egressPermit.OperationId,
                    retirePermit.OperationId)),
            cancellationToken);
        if (!TryAcceptState(cleanedReceipt, cleanedState, out var cleaned))
        {
            return new(
                MedusaAdmissionSagaStatus.ReconcileRequired,
                cleanedReceipt.Snapshot ?? terminal,
                runtime!.Snapshot);
        }
        return new(
            MedusaAdmissionSagaStatus.AlreadyTerminal,
            cleaned,
            runtime!.Snapshot);
    }

    private static bool TryAcceptState(
        MedusaAdmissionReceipt receipt,
        MedusaAdmissionState state,
        out MedusaAdmissionSnapshot snapshot)
    {
        if (receipt.IsSuccess && receipt.Snapshot?.State == state)
        {
            snapshot = receipt.Snapshot;
            return true;
        }
        snapshot = null!;
        return false;
    }
}
