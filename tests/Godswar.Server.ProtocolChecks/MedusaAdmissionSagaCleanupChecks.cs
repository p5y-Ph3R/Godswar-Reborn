using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaAdmissionSagaCoordinatorChecks
{
    private static async Task AssertCleanupResultsMustBeExactAsync()
    {
        var abort = new SagaFixture();
        abort.Runtime.EnsureFailure =
            MedusaPendingRuntimeStatus.RejectedNoPublication;
        abort.Store.AfterTransition = snapshot =>
        {
            if (snapshot.State == MedusaAdmissionState.Released)
            {
                abort.Transfer.ForcedAbortResult = AbortResult(
                    snapshot,
                    worldInstanceId:
                        Godswar.Server.Domain.World.Instances.WorldInstanceId.New());
            }
        };
        var abortResult = await abort.Coordinator.ExecuteAsync(abort.Command);
        Check.True(
            abortResult.Status == MedusaAdmissionSagaStatus.ReconcileRequired &&
            abortResult.Admission?.State == MedusaAdmissionState.Released &&
            abort.Runtime.ReleaseCalls == 0,
            "wrong-world abort success cannot authorize released cleanup");

        var staleAbort = new SagaFixture();
        staleAbort.Runtime.EnsureFailure =
            MedusaPendingRuntimeStatus.RejectedNoPublication;
        staleAbort.Store.AfterTransition = snapshot =>
        {
            if (snapshot.State == MedusaAdmissionState.Released)
            {
                staleAbort.Transfer.ForcedAbortResult = AbortResult(
                    snapshot,
                    releasedAtUtc: snapshot.ReleasedAtUtc!.Value.AddSeconds(1));
            }
        };
        var staleAbortResult = await staleAbort.Coordinator.ExecuteAsync(
            staleAbort.Command);
        Check.True(
            staleAbortResult.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            staleAbortResult.Admission?.State == MedusaAdmissionState.Released &&
            staleAbort.Runtime.ReleaseCalls == 0,
            "stale abort timestamp cannot authorize released cleanup");

        var releasedRuntime = new SagaFixture();
        releasedRuntime.Transfer.RejectPrepare = true;
        releasedRuntime.Store.AfterTransition = snapshot =>
        {
            if (snapshot.State != MedusaAdmissionState.RuntimeReady)
            {
                return;
            }
            var current = releasedRuntime.Runtime.Current!;
            releasedRuntime.Runtime.ForcedReleaseResult =
                new MedusaPendingRuntimeResult(
                    MedusaPendingRuntimeStatus.Applied,
                    new MedusaPendingRuntimeSnapshot(
                        current.AdmissionId,
                        current.WorldInstanceId,
                        current.Difficulty,
                        current.ContentMapId,
                        current.RosterHash,
                        current.AdmissionRequestHash,
                        current.EncounterContentFingerprint,
                        current.TransferToken,
                        MedusaPendingRuntimeState.Released,
                        current.CreatedAtUtc,
                        current.PreparedAtUtc.AddSeconds(1),
                        null,
                        releasedRuntime.Clock.GetUtcNow()));
        };
        var wrongPrepared = await releasedRuntime.Coordinator.ExecuteAsync(
            releasedRuntime.Command);
        Check.True(
            wrongPrepared.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            wrongPrepared.Admission?.State == MedusaAdmissionState.Released,
            "mismatched logical PreparedAt cannot authorize released cleanup");

        var wrongEgress = new SagaFixture();
        var wrongEgressRunning = await wrongEgress.Coordinator.ExecuteAsync(
            wrongEgress.Command);
        var wrongEgressTerminal = (await wrongEgress.Store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.Completed(
                    wrongEgress.AdmissionId),
                wrongEgress.AdmissionId,
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Completed,
                wrongEgressRunning.Admission!.ConsumedAtUtc!.Value
                    .AddMinutes(1)))).Snapshot!;
        wrongEgress.Transfer.ForcedEgressResult =
            new MedusaRosterEgressResult(
                MedusaRosterEgressStatus.Egressed,
                wrongEgressTerminal.AdmissionId,
                Godswar.Server.Domain.World.Instances.WorldInstanceId.New(),
                wrongEgressTerminal.RequestHash,
                wrongEgressTerminal.RosterHash,
                wrongEgressTerminal.TerminalAtUtc);
        var wrongEgressResult = await wrongEgress.Coordinator.ExecuteAsync(
            wrongEgress.Command);
        Check.True(
            wrongEgressResult.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            wrongEgressResult.Admission?.State ==
                MedusaAdmissionState.Completed &&
            wrongEgress.Runtime.RetireCalls == 0,
            "wrong-world egress success cannot authorize terminal cleanup");

        var wrongRetire = new SagaFixture();
        var wrongRetireRunning = await wrongRetire.Coordinator.ExecuteAsync(
            wrongRetire.Command);
        await wrongRetire.Store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.Completed(
                    wrongRetire.AdmissionId),
                wrongRetire.AdmissionId,
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Completed,
                wrongRetireRunning.Admission!.ConsumedAtUtc!.Value
                    .AddMinutes(1)));
        wrongRetire.Runtime.ForcedRetireResult =
            new MedusaPendingRuntimeResult(
                (MedusaPendingRuntimeStatus)255,
                null);
        var wrongRetireResult = await wrongRetire.Coordinator.ExecuteAsync(
            wrongRetire.Command);
        Check.True(
            wrongRetireResult.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            wrongRetireResult.Admission?.State ==
                MedusaAdmissionState.Completed,
            "undefined retire status cannot authorize terminal cleanup");

        Check.Throws<ArgumentException>(
            () => new MedusaRosterEgressResult(
                default,
                wrongEgressTerminal.AdmissionId,
                wrongEgressTerminal.WorldInstanceId,
                wrongEgressTerminal.RequestHash,
                wrongEgressTerminal.RosterHash,
                null),
            "default egress result status is rejected at construction");
        Check.Throws<ArgumentException>(
            () => new MedusaRosterTransferAbortResult(
                default,
                Guid.NewGuid(),
                wrongEgressTerminal.AdmissionId,
                wrongEgressTerminal.WorldInstanceId,
                wrongEgressTerminal.RequestHash,
                wrongEgressTerminal.RosterHash,
                wrongEgressTerminal.Revision,
                wrongEgressTerminal.TerminalAtUtc!.Value),
            "default abort receipt status is rejected at construction");
    }

    private static async Task AssertNeverStartedTimeoutStillCleansAsync()
    {
        var fixture = new SagaFixture();
        fixture.Store.ThrowAfterNextTarget =
            MedusaAdmissionState.ConsumedRunning;
        await ThrowsAfterSideEffectAsync(
            () => fixture.Coordinator.ExecuteAsync(fixture.Command),
            "consume receipt lost before Start");
        var consumed = fixture.Store.Snapshot(fixture.AdmissionId);
        Check.Equal(0, fixture.Runtime.StartCalls,
            "injected consume loss occurs before runtime publication");
        fixture.Runtime.LoseProcess();
        var timedOut = (await fixture.Store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.TimedOut(
                    fixture.AdmissionId),
                fixture.AdmissionId,
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.TimedOut,
                consumed.ConsumedAtUtc!.Value.AddMinutes(40)))).Snapshot!;

        var cleanup = await fixture.Coordinator.ResumeAsync(timedOut);
        Check.True(
            timedOut.State == MedusaAdmissionState.TimedOut &&
            cleanup.Status == MedusaAdmissionSagaStatus.AlreadyTerminal &&
            cleanup.Admission?.State == MedusaAdmissionState.TimedOutCleaned &&
            cleanup.Runtime?.State == MedusaPendingRuntimeState.Retired &&
            cleanup.Runtime.StartedAtUtc == consumed.ConsumedAtUtc &&
            fixture.Runtime.StartCalls == 0,
            "never-published run is tombstoned from durable consume time after process loss");
    }

    private static async Task AssertCleanupTombstonesDefeatStaleCreationAsync()
    {
        var released = new SagaFixture();
        released.Store.ReleaseWinsOnNextBarrier = true;
        var cleanup = await released.Coordinator.ExecuteAsync(released.Command);
        var staleEnsure = released.Runtime.LastEnsureRequest!;
        var stalePrepare = released.Transfer.LastPrepareRequest!;
        released.Runtime.LoseProcess();
        released.Transfer.LoseProcess();
        var ensure = await released.Runtime.EnsurePendingStartAsync(staleEnsure);
        var prepare = await released.Transfer.PrepareAsync(stalePrepare);
        Check.True(
            cleanup.Admission?.State == MedusaAdmissionState.ReleasedCleaned &&
            ensure.Status ==
                MedusaPendingRuntimeStatus.RejectedNoPublication &&
            prepare.Status ==
                MedusaRosterTransferPrepareStatus.RejectedNoChange &&
            released.Runtime.DurableReleased?.State ==
                MedusaPendingRuntimeState.Released &&
            released.Transfer.HiddenCount == 0,
            "release/abort tombstones survive process loss and defeat stale creation");
    }

    private static async Task AssertReleasedCleanupCrashBoundariesReplayAsync()
    {
        var beforeCleanup = new SagaFixture();
        beforeCleanup.Runtime.EnsureFailure =
            MedusaPendingRuntimeStatus.RejectedNoPublication;
        beforeCleanup.Store.ThrowAfterNextTarget =
            MedusaAdmissionState.Released;
        await ThrowsAfterSideEffectAsync(
            () => beforeCleanup.Coordinator.ExecuteAsync(beforeCleanup.Command),
            "durable Released receipt loss");
        beforeCleanup.Runtime.LoseProcess();
        beforeCleanup.Transfer.LoseProcess();
        var recovered = await beforeCleanup.Coordinator.ExecuteAsync(
            beforeCleanup.Command);
        Check.True(
            recovered.Admission?.State == MedusaAdmissionState.ReleasedCleaned &&
            beforeCleanup.Transfer.AbortCalls == 1 &&
            beforeCleanup.Runtime.ReleaseCalls == 1,
            "durable Released resumes exact cleanup after process loss");

        var beforeReceipt = new SagaFixture();
        beforeReceipt.Runtime.EnsureFailure =
            MedusaPendingRuntimeStatus.RejectedNoPublication;
        beforeReceipt.Store.ThrowBeforeNextTarget =
            MedusaAdmissionState.ReleasedCleaned;
        await ThrowsAfterSideEffectAsync(
            () => beforeReceipt.Coordinator.ExecuteAsync(beforeReceipt.Command),
            "cleanup side effects before durable completion");
        Check.True(
            beforeReceipt.Store.Snapshot(beforeReceipt.AdmissionId).State ==
                MedusaAdmissionState.Released,
            "failed cleanup receipt leaves durable cleanup pending");
        beforeReceipt.Runtime.LoseProcess();
        beforeReceipt.Transfer.LoseProcess();
        var replay = await beforeReceipt.Coordinator.ExecuteAsync(
            beforeReceipt.Command);
        Check.True(
            replay.Admission?.State == MedusaAdmissionState.ReleasedCleaned &&
            beforeReceipt.Transfer.AbortCalls == 2 &&
            beforeReceipt.Runtime.ReleaseCalls == 2,
            "durable cleanup tombstones replay exactly after side-effect response loss");
    }

    private static MedusaRosterTransferAbortResult AbortResult(
        MedusaAdmissionSnapshot released,
        Godswar.Server.Domain.World.Instances.WorldInstanceId? worldInstanceId = null,
        DateTimeOffset? releasedAtUtc = null) =>
        new(
            MedusaRosterTransferAbortStatus.Aborted,
            MedusaAdmissionSagaOperationIds.TransferAbort(released.AdmissionId),
            released.AdmissionId,
            worldInstanceId ?? released.WorldInstanceId,
            released.RequestHash,
            released.RosterHash,
            released.Revision,
            releasedAtUtc ?? released.ReleasedAtUtc!.Value);
}
