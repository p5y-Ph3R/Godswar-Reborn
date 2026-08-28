using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaAdmissionSagaCoordinatorChecks
{
    private static async Task AssertLeaseExpiryBeforeBarrierReleasesAsync()
    {
        var fixture = new SagaFixture();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var result = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            result.Status ==
                MedusaAdmissionSagaStatus.TransferRejectedCompensated &&
            result.Admission?.State ==
                MedusaAdmissionState.ReleasedCleaned,
            "expired frozen lease releases before the durable barrier");
        Check.Equal(0, fixture.Transfer.CommitCalls,
            "expired party revision never reaches public transfer");
    }

    private static async Task AssertAmbiguousReceiptsReplayExactlyAsync()
    {
        var readyLoss = new SagaFixture();
        readyLoss.Store.ThrowAfterNextTarget =
            MedusaAdmissionState.RuntimeReady;
        await ThrowsAfterSideEffectAsync(
            () => readyLoss.Coordinator.ExecuteAsync(readyLoss.Command),
            "lost RuntimeReady receipt");
        var durableReadyAt = readyLoss.Store.Snapshot(
            readyLoss.AdmissionId).RuntimeReadyAtUtc;
        var durableReadyToken = readyLoss.Runtime.Current!.TransferToken;
        readyLoss.Runtime.LoseProcess();
        readyLoss.Runtime.InitialPreparedAtUtc =
            readyLoss.ReceivedAt.AddMinutes(3);
        var readyReplay = await readyLoss.Coordinator.ExecuteAsync(
            readyLoss.Command);
        Check.True(
            readyReplay.Status == MedusaAdmissionSagaStatus.Running &&
            readyReplay.Runtime?.PreparedAtUtc == durableReadyAt &&
            readyReplay.Runtime?.TransferToken == durableReadyToken &&
            readyLoss.Transfer.AbortCalls == 0 &&
            readyLoss.Runtime.ReleaseCalls == 0,
            "lost RuntimeReady receipt resumes exact IDs without compensation");

        var commitLoss = new SagaFixture();
        commitLoss.Transfer.ThrowAfterNextCommit = true;
        await ThrowsAfterSideEffectAsync(
            () => commitLoss.Coordinator.ExecuteAsync(commitLoss.Command),
            "lost atomic commit response");
        var firstCommittedAt = commitLoss.Transfer.CommittedAtUtc;
        commitLoss.Transfer.LoseProcess();
        commitLoss.Transfer.CommittedAtUtc = firstCommittedAt.AddMinutes(17);
        var commitReplay = await commitLoss.Coordinator.ExecuteAsync(
            commitLoss.Command);
        Check.True(
            commitReplay.Status == MedusaAdmissionSagaStatus.Running &&
            commitReplay.Admission?.ConsumedAtUtc == firstCommittedAt &&
            commitLoss.Transfer.CommitCalls == 2 &&
            commitLoss.Transfer.AbortCalls == 0 &&
            commitLoss.Runtime.ReleaseCalls == 0,
            "lost public commit response replays commit and never releases");

        var consumeLoss = new SagaFixture();
        consumeLoss.Store.ThrowAfterNextTarget =
            MedusaAdmissionState.ConsumedRunning;
        await ThrowsAfterSideEffectAsync(
            () => consumeLoss.Coordinator.ExecuteAsync(consumeLoss.Command),
            "lost consumed receipt");
        var durableConsumed = consumeLoss.Store.Snapshot(
            consumeLoss.AdmissionId).ConsumedAtUtc;
        consumeLoss.Runtime.LoseProcess();
        consumeLoss.Runtime.InitialPreparedAtUtc =
            consumeLoss.ReceivedAt.AddMinutes(20);
        var consumeReplay = await consumeLoss.Coordinator.ExecuteAsync(
            consumeLoss.Command);
        Check.True(
            consumeReplay.Status == MedusaAdmissionSagaStatus.Running &&
            consumeReplay.Runtime?.StartedAtUtc == durableConsumed &&
            consumeLoss.Transfer.AbortCalls == 0 &&
            consumeLoss.Runtime.ReleaseCalls == 0,
            "consume-before-start crash resumes with durable original StartAt");
    }

    private static async Task AssertConflictingReceiptsNeverAdvanceAsync()
    {
        var ready = new SagaFixture();
        ready.Store.ConflictNextTarget = MedusaAdmissionState.RuntimeReady;
        var readyResult = await ready.Coordinator.ExecuteAsync(ready.Command);
        Check.True(
            readyResult.Status == MedusaAdmissionSagaStatus.ReconcileRequired &&
            ready.Transfer.PrepareCalls == 0,
            "RequestConflict RuntimeReady snapshot is never upgraded to authority");

        var barrier = new SagaFixture();
        barrier.Store.ConflictNextTarget =
            MedusaAdmissionState.RosterTransferCommitted;
        var barrierResult = await barrier.Coordinator.ExecuteAsync(
            barrier.Command);
        Check.True(
            barrierResult.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            barrier.Transfer.CommitCalls == 0,
            "RequestConflict barrier snapshot cannot authorize public commit");

        var consumed = new SagaFixture();
        consumed.Store.ConflictNextTarget =
            MedusaAdmissionState.ConsumedRunning;
        var consumedResult = await consumed.Coordinator.ExecuteAsync(
            consumed.Command);
        Check.True(
            consumedResult.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            consumed.Runtime.StartCalls == 0,
            "RequestConflict consumed snapshot cannot authorize Start");

        var released = new SagaFixture();
        released.Runtime.EnsureFailure =
            MedusaPendingRuntimeStatus.RejectedNoPublication;
        released.Store.ConflictNextTarget = MedusaAdmissionState.Released;
        var releasedResult = await released.Coordinator.ExecuteAsync(
            released.Command);
        Check.True(
            releasedResult.Status ==
                MedusaAdmissionSagaStatus.ReconcileRequired &&
            released.Transfer.AbortCalls == 0 &&
            released.Runtime.ReleaseCalls == 0,
            "RequestConflict release snapshot cannot authorize cleanup");
    }

    private static async Task AssertTerminalAndOperationPermitsFailClosedAsync()
    {
        var fixture = new SagaFixture();
        var running = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            MedusaRosterTransferBarrierPermit.TryCreate(
                running.Admission!,
                out var staleBarrierPermit),
            "active run mints exact transfer replay authority");
        Check.True(
            MedusaRuntimeStartPermit.TryCreate(
                running.Admission!,
                running.Runtime!,
                out var staleStartPermit),
            "active run mints exact runtime replay authority");
        var staleCommit = new MedusaRosterTransferCommitRequest(
            MedusaAdmissionSagaOperationIds.TransferCommit(
                fixture.AdmissionId),
            staleBarrierPermit,
            running.Runtime!.TransferToken);
        var stalePrepare = fixture.Transfer.LastPrepareRequest!;
        var terminalReceipt = await fixture.Store.TransitionAsync(
            new MedusaAdmissionTransitionRequest(
                MedusaAdmissionSagaOperationIds.Completed(
                    fixture.AdmissionId),
                fixture.AdmissionId,
                MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Completed,
                running.Admission!.ConsumedAtUtc!.Value.AddMinutes(1)));
        var terminal = terminalReceipt.Snapshot!;
        Check.True(
            !MedusaRosterTransferBarrierPermit.TryCreate(terminal, out _) &&
            !MedusaRuntimeStartPermit.TryCreate(
                terminal,
                running.Runtime!,
                out _) &&
            !MedusaRuntimeReleasePermit.TryCreate(terminal, out _),
            "terminal state cannot mint transfer, start, or release authority");
        Check.Throws<ArgumentException>(
            () => new MedusaPendingStartRuntimeRequest(terminal),
            "terminal state cannot recreate a runtime");
        Check.True(
            MedusaRosterEgressPermit.TryCreate(
                terminal,
                out var egressPermit),
            "pending terminal cleanup mints exact egress authority");
        Check.True(
            MedusaRuntimeRetirePermit.TryCreate(
                terminal,
                out var retirePermit),
            "terminal durability alone mints exact retire authority");
        Check.True(
            retirePermit.CreatedAtUtc == terminal.ReservedAtUtc &&
            retirePermit.PreparedAtUtc == terminal.RuntimeReadyAtUtc &&
            retirePermit.StartedAtUtc == terminal.ConsumedAtUtc,
            "retire authority fixes the exact durable lifecycle timestamps");
        fixture.Runtime.LoseProcess();
        var cleanup = await fixture.Coordinator.ExecuteAsync(fixture.Command);
        Check.True(
            cleanup.Status == MedusaAdmissionSagaStatus.AlreadyTerminal &&
            cleanup.Admission?.State ==
                MedusaAdmissionState.CompletedCleaned &&
            fixture.Transfer.LastEgressResult?.Matches(egressPermit) == true &&
            retirePermit.Matches(fixture.Runtime.LastRetireResult),
            "coordinator persists cleanup only after exact egress and lost-runtime retirement");
        fixture.Runtime.LoseProcess();
        fixture.Transfer.LoseProcess();
        var staleEnsure = await fixture.Runtime.EnsurePendingStartAsync(
            new MedusaPendingStartRuntimeRequest(running.Admission!));
        var stalePrepareResult = await fixture.Transfer.PrepareAsync(
            stalePrepare);
        var staleCommitResult = await fixture.Transfer.CommitAsync(staleCommit);
        var staleStartResult = await fixture.Runtime.StartAsync(staleStartPermit);
        Check.True(
            staleEnsure.Status ==
                MedusaPendingRuntimeStatus.RejectedNoPublication &&
            stalePrepareResult.Status ==
                MedusaRosterTransferPrepareStatus.RejectedNoChange &&
            staleCommitResult.Status ==
                MedusaRosterTransferCommitStatus.SupersededByEgress &&
            staleStartResult.Status ==
                MedusaPendingRuntimeStatus.RejectedNoChange &&
            fixture.Runtime.DurableRetired?.State ==
                MedusaPendingRuntimeState.Retired,
            "durable egress/retire tombstones survive process loss and defeat stale authority");
        Check.True(
            cleanup.Admission?.CleanupEvidence?.RosterOperationId ==
                egressPermit.OperationId &&
            cleanup.Admission?.CleanupEvidence?.RuntimeOperationId ==
                retirePermit.OperationId,
            "cleanup completion is durable only after exact side-effect receipts");

        var request = MedusaDurableAdmissionFoundationChecks.Request(
            fixture.Lease,
            fixture.ReceivedAt,
            MedusaEncounterDifficulty.Normal,
            MedusaAdmissionId.New(),
            Godswar.Server.Domain.World.Instances.WorldInstanceId.New());
        var reserved = Reserved(request);
        var ready = SnapshotAt(
            reserved,
            MedusaAdmissionState.RuntimeReady,
            readyAt: reserved.ReservedAtUtc.AddMinutes(1));
        var token = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                ready.AdmissionId,
                ready.RequestHash));
        Check.Throws<ArgumentException>(
            () => new MedusaRosterTransferPrepareRequest(
                Guid.NewGuid(),
                ready,
                token),
            "random nonempty prepare operation ID is rejected");

        var barrier = SnapshotAt(
            ready,
            MedusaAdmissionState.RosterTransferCommitted,
            readyAt: ready.RuntimeReadyAtUtc,
            barrierAt: ready.RuntimeReadyAtUtc!.Value.AddMinutes(1),
            evidence: new MedusaRosterTransferBarrierEvidence(
                MedusaAdmissionSagaOperationIds.TransferPrepare(
                    ready.AdmissionId),
                new string('B', 64)));
        Check.True(
            MedusaRosterTransferBarrierPermit.TryCreate(
                barrier,
                out var permit),
            "durable barrier mints exact active commit permit");
        Check.Throws<ArgumentException>(
            () => new MedusaRosterTransferCommitRequest(
                Guid.NewGuid(),
                permit,
                token),
            "random nonempty commit operation ID is rejected");
    }

    private static MedusaAdmissionSnapshot SnapshotAt(
        MedusaAdmissionSnapshot source,
        MedusaAdmissionState state,
        DateTimeOffset? readyAt,
        DateTimeOffset? barrierAt = null,
        MedusaRosterTransferBarrierEvidence? evidence = null) =>
        new(
            source.AdmissionId,
            source.WorldInstanceId,
            source.RealmDay,
            source.Difficulty,
            source.ContentMapId,
            source.Source,
            source.Party,
            source.EncounterContentFingerprint,
            source.RosterHash,
            source.RequestHash,
            state,
            state == MedusaAdmissionState.RuntimeReady ? 2 : 3,
            evidence,
            source.ReservedAtUtc,
            readyAt,
            barrierAt,
            null,
            null,
            null);

    private static async Task ThrowsAfterSideEffectAsync(
        Func<Task<MedusaAdmissionSagaResult>> action,
        string description)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Assertion failed: {description} was not injected.");
    }
}
