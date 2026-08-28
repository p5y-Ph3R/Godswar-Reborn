using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private sealed record PlayerStatusProjectionEnvelope(
        GameSessionContext? Context,
        PlayerStatusSnapshot SnapshotWithoutMedusa,
        PlayerStatusSnapshot Snapshot,
        MedusaClientStatusOverlay MedusaOverlay,
        CompleteStatusPublicationFence CompleteFence);

    private sealed record CompleteStatusPublicationFence(
        PlayerStatusState? State,
        long BaselineRevision,
        PlayerStatusSnapshot Baseline,
        string CompleteFingerprint,
        DateTimeOffset ObservedAt,
        ClientStatusAggregate? LocalAggregate = null);

    public async Task SendStatusSnapshotToViewerAsync(
        GameSessionContext player,
        ClientSession viewer,
        CancellationToken cancellationToken) =>
        await SendStatusSnapshotToViewerCoreAsync(
            player,
            viewer,
            requireBoundMedusa: false,
            cancellationToken);

    internal async Task SendBoundMedusaStatusSnapshotToViewerAsync(
        GameSessionContext player,
        ClientSession viewer,
        CancellationToken cancellationToken) =>
        await SendStatusSnapshotToViewerCoreAsync(
            player,
            viewer,
            requireBoundMedusa: true,
            cancellationToken);

    private async Task SendStatusSnapshotToViewerCoreAsync(
        GameSessionContext player,
        ClientSession viewer,
        bool requireBoundMedusa,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(viewer);
        var envelope = await GetStatusProjectionEnvelopeAsync(
            player.Session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (requireBoundMedusa && !envelope.MedusaOverlay.IsBound ||
            envelope.Context is not { } target)
        {
            return;
        }
        if (envelope.MedusaOverlay.IsBound)
        {
            await SendCurrentBoundStatusSnapshotToViewerAsync(
                target,
                viewer,
                cancellationToken);
            return;
        }

        if (!TryCaptureStatusPublicationRoute(
                target,
                viewer,
                out var runtime,
                out var targetLife,
                out var viewerContext,
                out var viewerLife))
        {
            await viewer.SendAsync(
                PacketBuilder.PlayerStatusEffects(
                    player.Character,
                    player.ObjectId,
                    envelope.Snapshot.Effects,
                    envelope.Snapshot.Aggregate),
                cancellationToken,
                "VisiblePlayerStatusEffects");
            return;
        }

        var admissionOutcome = await TrySendStatusPacketExactAsync(
            runtime,
            viewerContext,
            viewerLife,
            target,
            targetLife,
            envelope.MedusaOverlay,
            PacketBuilder.PlayerStatusEffects(
                target.Character,
                target.ObjectId,
                envelope.Snapshot.Effects,
                envelope.Snapshot.Aggregate),
            cancellationToken,
            "VisiblePlayerStatusEffects");
        if (RequiresAdmissionFailureDisconnect(admissionOutcome))
        {
            DisconnectExactStatusRecipient(viewer);
        }
    }

    internal async Task<PlayerStatusSnapshot?>
        SendStatusSnapshotToSelfAsync(
            ClientSession session,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string label)
    {
        var envelope = await GetStatusProjectionEnvelopeAsync(
            session,
            now,
            cancellationToken);
        if (envelope.Context is not { } context)
        {
            return null;
        }
        if (envelope.MedusaOverlay.IsBound)
        {
            return await SendCurrentBoundStatusSnapshotToSelfAsync(
                context,
                now,
                cancellationToken,
                label);
        }
        if (
            !TryCaptureStatusPublicationTarget(
                    context,
                    out var runtime,
                    out var lifeRevision))
        {
            return null;
        }

        var localAggregate = ProjectElementalMovementStatus(
            session,
            context.Character,
            context.Ownership,
            envelope.Snapshot.Aggregate,
            now);
        var admissionOutcome =
            await TrySendStatusPacketPairExactAsync(
                runtime,
                context,
                lifeRevision,
                context,
                lifeRevision,
                envelope.MedusaOverlay,
                PacketBuilder.PlayerStatusEffects(
                    context.Character,
                    envelope.Snapshot.Effects,
                    envelope.Snapshot.Aggregate),
                PacketBuilder.PlayerStatusUpdate(
                    context.Character,
                    localAggregate),
                cancellationToken,
                label,
                completeFence: envelope.CompleteFence with
                {
                    LocalAggregate = localAggregate
                });
        if (!WasAdmitted(admissionOutcome))
        {
            if (RequiresAdmissionFailureDisconnect(
                    admissionOutcome))
            {
                DisconnectExactStatusRecipient(session);
            }
            return null;
        }

        return envelope.Snapshot;
    }

    internal async Task<PlayerStatusSnapshot> GetStatusSnapshotAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        (await GetStatusProjectionEnvelopeAsync(
            session,
            now,
            cancellationToken)).SnapshotWithoutMedusa;

    private async Task<PlayerStatusProjectionEnvelope>
        GetStatusProjectionEnvelopeAsync(
            ClientSession session,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            var baseline = PlayerStatusComposer.Compose(
                ExperienceBoostState.Empty,
                [],
                now);
            if (!_sessions.TryGetValue(session, out var context))
            {
                return new(
                    Context: null,
                    baseline,
                    baseline,
                    MedusaClientStatusOverlay.Unbound,
                    new(
                        null,
                        -1,
                        baseline,
                        baseline.Fingerprint,
                        now));
            }

            var snapshot = MergeTrainingDummyClientStatusOverlays(
                context,
                baseline,
                now,
                out _,
                out _,
                out var medusa,
                out var withoutMedusa);
            return new(
                context,
                withoutMedusa,
                snapshot,
                medusa,
                new(
                    null,
                    -1,
                    baseline,
                    snapshot.Fingerprint,
                    now));
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            return ComposeStatusProjectionEnvelopeLocked(
                session,
                state,
                now);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private PlayerStatusProjectionEnvelope
        ComposeStatusProjectionEnvelopeLocked(
            ClientSession session,
            PlayerStatusState state,
            DateTimeOffset now)
    {
        PlayerStatusSnapshot? snapshot = null;
        GameSessionContext? context = null;
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(
                        session,
                        out var currentContext))
                {
                    snapshot = EvaluatePlayerStatusEcsLocked(
                            session,
                            state,
                            currentContext,
                            now)
                        .Snapshot;
                    context = currentContext;
                }
            }
        }

        snapshot ??= PlayerStatusComposer.Compose(
            state.ExperienceBoosts,
            state.RuntimeStatuses.Values,
            now);
        context ??= _sessions.TryGetValue(session, out var current)
            ? current
            : null;
        if (context is null)
        {
            return new(
                Context: null,
                snapshot,
                snapshot,
                MedusaClientStatusOverlay.Unbound,
                new(
                    state,
                    state.Revision,
                    snapshot,
                    snapshot.Fingerprint,
                    now));
        }

        InvokeProtocolCheckAfterStatusProjectionBaselineCapture();
        var merged = MergeTrainingDummyClientStatusOverlays(
            context,
            snapshot,
            now,
            out _,
            out _,
            out var medusa,
            out var withoutMedusa);
        return new(
            context,
            withoutMedusa,
            merged,
            medusa,
            new(
                state,
                state.Revision,
                snapshot,
                merged.Fingerprint,
                now));
    }

#if DEBUG
    private Action?
        _protocolCheckAfterStatusProjectionBaselineCapture = null;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckAfterStatusProjectionBaselineCapture()
    {
#if DEBUG
        _protocolCheckAfterStatusProjectionBaselineCapture?.Invoke();
#endif
    }

    private bool TryCaptureStatusPublicationTarget(
        GameSessionContext expected,
        out WorldInstanceRuntime runtime,
        out long lifeRevision)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(
                expected.Session,
                out var current) &&
                ReferenceEquals(current, expected) &&
                TryGetWorldInstance(current, out runtime!) &&
                _playerLifeRevisions.TryGetValue(
                    current.Session,
                    out lifeRevision))
            {
                return true;
            }
        }

        runtime = null!;
        lifeRevision = -1;
        return false;
    }

    private bool TryCaptureStatusPublicationRoute(
        GameSessionContext expectedTarget,
        ClientSession viewer,
        out WorldInstanceRuntime runtime,
        out long targetLifeRevision,
        out GameSessionContext viewerContext,
        out long viewerLifeRevision)
    {
        lock (_gate)
        {
            if (TryCaptureStatusPublicationTarget(
                    expectedTarget,
                    out runtime!,
                    out targetLifeRevision) &&
                _sessions.TryGetValue(viewer, out viewerContext!) &&
                viewerContext.WorldReady &&
                viewerContext.WorldInstanceId ==
                    expectedTarget.WorldInstanceId &&
                _playerLifeRevisions.TryGetValue(
                    viewer,
                    out viewerLifeRevision))
            {
                return true;
            }
        }

        runtime = null!;
        targetLifeRevision = -1;
        viewerContext = null!;
        viewerLifeRevision = -1;
        return false;
    }
}
