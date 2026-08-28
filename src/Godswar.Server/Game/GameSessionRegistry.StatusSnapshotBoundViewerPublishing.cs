using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task SendCurrentBoundStatusSnapshotToViewerAsync(
        GameSessionContext target,
        ClientSession viewer,
        CancellationToken cancellationToken)
    {
        if (!TryCaptureCurrentMedusaPublicationContext(
                target,
                out target,
                out var targetLifeRevision))
        {
            return;
        }
        if (!_playerStatusStates.TryGetValue(
                target.Session,
                out var state))
        {
            if (TryClaimExactMedusaMembershipDisconnect(
                    target,
                    targetLifeRevision,
                    out var claimed))
            {
                CompleteClaimedExactStatusDisconnect(claimed);
            }
            return;
        }

        ClientSession? claimedSession = null;
        GameSessionContext? lastViewerContext = null;
        var lastViewerLife = -1L;
        var admissionClaims = new ExactStatusDisconnectClaims();
        admissionClaims.EnsureCapacity(1);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        targetLifeRevision,
                        out target))
                {
                    return;
                }

                var current = ComposeStatusProjectionEnvelopeLocked(
                    target.Session,
                    state,
                    DateTimeOffset.UtcNow);
                InvokeProtocolCheckAfterBoundMedusaViewerStatusCapture();
                if (!ReferenceEquals(current.Context, target))
                {
                    if (TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    return;
                }
                if (!current.MedusaOverlay.CanPublish)
                {
                    if (current.MedusaOverlay.AuthorityOutcome ==
                        MedusaCharacterEffectAuthorityOutcome
                            .BoundAuthorityUnavailable)
                    {
                        _ = TryClaimExactMedusaMembershipDisconnect(
                            target,
                            targetLifeRevision,
                            out claimedSession!);
                    }
                    break;
                }
                if (!TryCaptureStatusPublicationRoute(
                        target,
                        viewer,
                        out var runtime,
                        out var targetLife,
                        out var viewerContext,
                        out var viewerLife))
                {
                    if (TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out var rebased) &&
                        !ReferenceEquals(rebased, target))
                    {
                        target = rebased;
                        continue;
                    }
                    return;
                }

                lastViewerContext = viewerContext;
                lastViewerLife = viewerLife;
                var admissionOutcome =
                    await TrySendStatusPacketExactAsync(
                        runtime,
                        viewerContext,
                        viewerLife,
                        target,
                        targetLife,
                        current.MedusaOverlay,
                        PacketBuilder.PlayerStatusEffects(
                            target.Character,
                            target.ObjectId,
                            current.Snapshot.Effects,
                            current.Snapshot.Aggregate),
                        cancellationToken,
                        "VisiblePlayerStatusEffects",
                        completeFence: current.CompleteFence,
                        completionStatusGate: state,
                        claimedDisconnects: admissionClaims);
                if (WasAdmitted(admissionOutcome))
                {
                    return;
                }
                if (RequiresAdmissionFailureDisconnect(
                        admissionOutcome))
                {
                    _ = TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        targetLifeRevision,
                        viewerContext,
                        viewerLife,
                        out claimedSession!);
                    break;
                }
                if (admissionOutcome ==
                    ExactStatusAdmissionOutcome.AuthorityUnavailable)
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        targetLifeRevision,
                        out claimedSession!);
                    break;
                }
                if (admissionOutcome is
                    ExactStatusAdmissionOutcome.Canceled or
                    ExactStatusAdmissionOutcome
                        .RecipientOrTargetStale)
                {
                    return;
                }
            }

            if (claimedSession is null)
            {
                if (lastViewerContext is not null)
                {
                    _ = TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        targetLifeRevision,
                        lastViewerContext,
                        lastViewerLife,
                        out claimedSession!);
                }
                else
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        targetLifeRevision,
                        out claimedSession!);
                }
            }
        }
        catch
        {
            if (lastViewerContext is not null)
            {
                _ = TryClaimExactMedusaPublicationPairDisconnect(
                    target,
                    targetLifeRevision,
                    lastViewerContext,
                    lastViewerLife,
                    out claimedSession!);
            }
            else
            {
                _ = TryClaimExactMedusaMembershipDisconnect(
                    target,
                    targetLifeRevision,
                    out claimedSession!);
            }
            throw;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
            if (claimedSession is not null)
            {
                CompleteClaimedExactStatusDisconnect(claimedSession);
            }
        }

    }

#if DEBUG
    private Action?
        _protocolCheckAfterBoundMedusaViewerStatusCapture = null;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void
        InvokeProtocolCheckAfterBoundMedusaViewerStatusCapture()
    {
#if DEBUG
        _protocolCheckAfterBoundMedusaViewerStatusCapture?.Invoke();
#endif
    }
}
