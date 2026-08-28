using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Replaces map-only spawn refreshes for a bound Medusa member. The raw
    /// spawn deliberately carries no Medusa overlay; an exact complete 10167
    /// follows it for the same captured observer and target epochs.
    /// </summary>
    internal async Task<int?>
        TryBroadcastMedusaWorldSpawnRefreshAsync(
            ClientSession targetSession,
            CancellationToken cancellationToken,
            string label)
    {
        var envelope = await GetStatusProjectionEnvelopeAsync(
            targetSession,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!envelope.MedusaOverlay.IsBound)
        {
            return null;
        }
        if (envelope.Context is not { } capturedTarget ||
            !TryCaptureCurrentMedusaPublicationTarget(
                capturedTarget,
                out var target,
                out var runtime,
                out var targetLifeRevision))
        {
            return 0;
        }
        if (envelope.MedusaOverlay.AuthorityOutcome ==
            MedusaCharacterEffectAuthorityOutcome
                .BoundAuthorityUnavailable)
        {
            if (TryClaimExactMedusaMembershipDisconnect(
                    target,
                    targetLifeRevision,
                    out var claimedTarget))
            {
                CompleteClaimedExactStatusDisconnect(claimedTarget);
            }
            return 0;
        }

        // A routine same-membership character revision can replace the
        // registry context after the baseline was composed but before the
        // Medusa owner was queried. CurrentMembershipRequired is therefore
        // recomposed under the bounded helper instead of being mistaken for
        // a completed/stale publication.

        var recipients = CaptureMonsterAttackPublicationRecipients(
            runtime,
            GetWorldInstanceSessions(
                target.WorldInstanceId,
                target.Session));
        var spawned = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                if (!await TrySendCurrentMedusaWorldSpawnPairAsync(
                        runtime,
                        recipient.Context,
                        recipient.LifeRevision,
                        target,
                        targetLifeRevision,
                        label))
                {
                    continue;
                }

                spawned++;
            }
            catch (Exception)
            {
                if (TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        targetLifeRevision,
                        recipient.Context,
                        recipient.LifeRevision,
                        out var claimedRecipient))
                {
                    CompleteClaimedExactStatusDisconnect(
                        claimedRecipient);
                }
            }
        }

        return spawned;
    }

    private async Task<bool> TrySendCurrentMedusaWorldSpawnPairAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long recipientLifeRevision,
        GameSessionContext target,
        long targetLifeRevision,
        string label)
    {
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
            return false;
        }

        ClientSession? claimedDisconnect = null;
        var admissionClaims = new ExactStatusDisconnectClaims();
        admissionClaims.EnsureCapacity(1);
        await state.Gate.WaitAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        targetLifeRevision,
                        out target) ||
                    !TryRebaseMedusaPublicationContext(
                        recipient,
                        recipientLifeRevision,
                        out recipient))
                {
                    return false;
                }
                var current = ComposeStatusProjectionEnvelopeLocked(
                    target.Session,
                    state,
                    DateTimeOffset.UtcNow);
                InvokeProtocolCheckAfterMedusaWorldSpawnCapture();
                if (!ReferenceEquals(current.Context, target))
                {
                    if (TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    return false;
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
                            out claimedDisconnect!);
                        break;
                    }
                    return false;
                }

                var admissionOutcome =
                    await TrySendStatusPacketPairExactAsync(
                    runtime,
                    recipient,
                    recipientLifeRevision,
                    target,
                    targetLifeRevision,
                    current.MedusaOverlay,
                    PacketBuilder.PlayerWorldSpawn(
                        target.Character,
                        target.ObjectId,
                        current.SnapshotWithoutMedusa.Effects,
                        pkMode: TrainingDummySpawnPkMode(
                            target.Character)),
                    PacketBuilder.PlayerStatusEffects(
                        target.Character,
                        target.ObjectId,
                        current.Snapshot.Effects,
                        current.Snapshot.Aggregate),
                    CancellationToken.None,
                    label,
                    completeFence: current.CompleteFence,
                    completionStatusGate: state,
                    claimedDisconnects: admissionClaims);
                if (WasAdmitted(admissionOutcome))
                {
                    return true;
                }
                if (RequiresAdmissionFailureDisconnect(
                        admissionOutcome))
                {
                    _ = TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        targetLifeRevision,
                        recipient,
                        recipientLifeRevision,
                        out claimedDisconnect!);
                    break;
                }
                if (admissionOutcome ==
                    ExactStatusAdmissionOutcome.AuthorityUnavailable)
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        targetLifeRevision,
                        out claimedDisconnect!);
                    break;
                }
                if (admissionOutcome is
                    ExactStatusAdmissionOutcome.Canceled or
                    ExactStatusAdmissionOutcome
                        .RecipientOrTargetStale)
                {
                    return false;
                }
            }

            if (claimedDisconnect is null)
            {
                _ = TryClaimExactMedusaPublicationPairDisconnect(
                    target,
                    targetLifeRevision,
                    recipient,
                    recipientLifeRevision,
                    out claimedDisconnect!);
            }

        }
        catch
        {
            _ = TryClaimExactMedusaPublicationPairDisconnect(
                target,
                targetLifeRevision,
                recipient,
                recipientLifeRevision,
                out claimedDisconnect!);
            throw;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
            if (claimedDisconnect is not null)
            {
                CompleteClaimedExactStatusDisconnect(
                    claimedDisconnect);
            }
        }
        return false;
    }

#if DEBUG
    private Action? _protocolCheckAfterMedusaWorldSpawnCapture = null;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckAfterMedusaWorldSpawnCapture()
    {
#if DEBUG
        _protocolCheckAfterMedusaWorldSpawnCapture?.Invoke();
#endif
    }
}
