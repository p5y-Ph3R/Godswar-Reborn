using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task<PlayerStatusSnapshot?>
        SendCurrentBoundStatusSnapshotToSelfAsync(
            GameSessionContext target,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken,
            string label)
    {
        if (!TryCaptureCurrentMedusaPublicationContext(
                target,
                out target,
                out var targetLifeRevision))
        {
            return null;
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
            return null;
        }

        Godswar.Server.Networking.ClientSession? claimedSession = null;
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
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                if (now < observedAt)
                {
                    now = observedAt;
                }
                var current = ComposeStatusProjectionEnvelopeLocked(
                    target.Session,
                    state,
                    now);
                if (!ReferenceEquals(current.Context, target))
                {
                    if (TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    return null;
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
                if (!TryCaptureStatusPublicationTarget(
                        target,
                        out var runtime,
                        out var lifeRevision))
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
                    return null;
                }

                var localAggregate = ProjectElementalMovementStatus(
                    target.Session,
                    target.Character,
                    target.Ownership,
                    current.Snapshot.Aggregate,
                    now);
                var admissionOutcome =
                    await TrySendStatusPacketPairExactAsync(
                        runtime,
                        target,
                        lifeRevision,
                        target,
                        lifeRevision,
                        current.MedusaOverlay,
                        PacketBuilder.PlayerStatusEffects(
                            target.Character,
                            current.Snapshot.Effects,
                            current.Snapshot.Aggregate),
                        PacketBuilder.PlayerStatusUpdate(
                            target.Character,
                            localAggregate),
                        cancellationToken,
                        label,
                        completeFence: current.CompleteFence with
                        {
                            LocalAggregate = localAggregate
                        },
                        completionStatusGate: state,
                        claimedDisconnects: admissionClaims);
                if (WasAdmitted(admissionOutcome))
                {
                    return current.Snapshot;
                }
                if (admissionOutcome is
                    ExactStatusAdmissionOutcome.Canceled or
                    ExactStatusAdmissionOutcome
                        .RecipientOrTargetStale)
                {
                    return null;
                }
                if (admissionOutcome ==
                        ExactStatusAdmissionOutcome.AuthorityUnavailable ||
                    RequiresAdmissionFailureDisconnect(
                        admissionOutcome))
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        targetLifeRevision,
                        out claimedSession!);
                    break;
                }
            }

            if (claimedSession is null)
            {
                _ = TryClaimExactMedusaMembershipDisconnect(
                    target,
                    targetLifeRevision,
                    out claimedSession!);
            }
        }
        catch
        {
            _ = TryClaimExactMedusaMembershipDisconnect(
                target,
                targetLifeRevision,
                out claimedSession!);
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
        return null;
    }
}
