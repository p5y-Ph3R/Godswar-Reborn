using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private ValueTask<ExactStatusAdmissionOutcome>
        TrySendStatusPacketExactAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long expectedRecipientLifeRevision,
        GameSessionContext target,
        long expectedTargetLifeRevision,
        MedusaClientStatusOverlay medusaOverlay,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        string label,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false,
        Action<MedusaCharacterEffectAuthorityOutcome>?
            targetAuthorityObserved = null,
        CompleteStatusPublicationFence? completeFence = null,
        PlayerStatusState? completionStatusGate = null,
        ExactStatusDisconnectClaims? claimedDisconnects = null)
    {
        if (completionStatusGate is not null &&
            claimedDisconnects is null)
        {
            throw new ArgumentNullException(nameof(claimedDisconnects));
        }
        claimedDisconnects?.EnsureCapacity(1);
        Task send = Task.CompletedTask;
        var admissionFailed = false;
        var admittedTerminal = false;
        ClientSession? claimedAdmissionFailure = null;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(
                    ExactStatusAdmissionOutcome.Canceled);
            }
            if (!TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    recipient,
                    expectedRecipientLifeRevision,
                    out recipient) ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    target,
                    expectedTargetLifeRevision,
                    out target) ||
                !ReferenceEquals(
                    target.Character,
                    _sessions[target.Session].Character) ||
                !MatchesMonsterAttackTargetVitalsFence(
                    target,
                    expectedTargetVitalsRevision,
                    requireTargetDead))
            {
                targetAuthorityObserved?.Invoke(
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired);
                return ValueTask.FromResult(
                    ExactStatusAdmissionOutcome
                        .RecipientOrTargetStale);
            }

            var matchesOverlay = MatchesMedusaClientStatusOverlay(
                target,
                medusaOverlay,
                ExactStatusObservedAt(),
                out var currentOutcome);
            targetAuthorityObserved?.Invoke(currentOutcome);
            if (!matchesOverlay)
            {
                return ValueTask.FromResult(
                    StatusProjectionMismatchOutcome(currentOutcome));
            }
            if (completeFence is not null &&
                !MatchesCompleteStatusPublicationFence(
                    target,
                    completeFence,
                    ExactStatusObservedAt()))
            {
                return ValueTask.FromResult(
                    ExactStatusAdmissionOutcome.ProjectionChanged);
            }

            var egressOutcome = recipient.Session
                .TryAdmitExactOutcome(packet, out send);
            if (egressOutcome ==
                ExactEgressAdmissionOutcome.AdmittedTerminal)
            {
                admittedTerminal = true;
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedAdmissionFailure = recipient.Session;
                }
            }
            else if (egressOutcome !=
                     ExactEgressAdmissionOutcome.Admitted)
            {
                admissionFailed = true;
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedAdmissionFailure = recipient.Session;
                }
            }
        }

        if (admissionFailed || admittedTerminal)
        {
            if (claimedAdmissionFailure is not null)
            {
                if (claimedDisconnects is null)
                {
                    CompleteClaimedExactStatusDisconnect(
                        claimedAdmissionFailure);
                }
                else
                {
                    claimedDisconnects.CaptureClaimed(
                        claimedAdmissionFailure);
                }
            }
            if (admissionFailed)
            {
                return ValueTask.FromResult(
                    claimedAdmissionFailure is null
                        ? ExactStatusAdmissionOutcome.AdmissionFailed
                        : ExactStatusAdmissionOutcome
                            .AdmissionFailedClaimed);
            }
        }

        if (completionStatusGate is null)
        {
            ObserveExactAdmissionCompletion(
                recipient.Session,
                send,
                label);
        }
        else
        {
            ObserveExactAdmissionCompletionAfterStatusGate(
                completionStatusGate,
                recipient.Session,
                send,
                label);
        }
        return ValueTask.FromResult(admittedTerminal
            ? claimedAdmissionFailure is null
                ? ExactStatusAdmissionOutcome.AdmittedTerminal
                : ExactStatusAdmissionOutcome.AdmittedTerminalClaimed
            : ExactStatusAdmissionOutcome.Admitted);
    }

    private async Task<int> PublishStatusPacketWorldExactAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext target,
        long expectedTargetLifeRevision,
        MedusaClientStatusOverlay medusaOverlay,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        string label,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false,
        Action<MedusaCharacterEffectAuthorityOutcome>?
            targetAuthorityObserved = null,
        CompleteStatusPublicationFence? completeFence = null,
        PlayerStatusState? completionStatusGate = null,
        ExactStatusDisconnectClaims? claimedDisconnects = null)
    {
        ArgumentNullException.ThrowIfNull(completionStatusGate);
        ArgumentNullException.ThrowIfNull(claimedDisconnects);
        var recipients = CaptureMonsterAttackPublicationRecipients(
            runtime,
            GetWorldInstanceSessions(
                target.WorldInstanceId,
                target.Session));
        claimedDisconnects?.EnsureCapacity(recipients.Count);
        var sent = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                var outcome = await TrySendStatusPacketExactAsync(
                        runtime,
                        recipient.Context,
                        recipient.LifeRevision,
                        target,
                        expectedTargetLifeRevision,
                        medusaOverlay,
                        packet,
                        cancellationToken,
                        label,
                        expectedTargetVitalsRevision,
                        requireTargetDead,
                        targetAuthorityObserved,
                        completeFence: completeFence,
                        completionStatusGate: completionStatusGate,
                        claimedDisconnects: claimedDisconnects);
                if (WasAdmitted(outcome))
                {
                    sent++;
                }
                else if (RequiresAdmissionFailureDisconnect(outcome))
                {
                    // TryAdmitExact already consumed the transport claim
                    // under the exact registry fence. The Gate owner drains
                    // claimedDisconnects after releasing its status lock.
                }
            }
            catch (Exception)
            {
                if (claimedDisconnects is not null &&
                    TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        expectedTargetLifeRevision,
                        recipient.Context,
                        recipient.LifeRevision,
                        out var claimedRecipient))
                {
                    claimedDisconnects.CaptureClaimed(
                        claimedRecipient);
                }
            }
        }

        return sent;
    }

    private static ExactStatusAdmissionOutcome
        StatusProjectionMismatchOutcome(
            MedusaCharacterEffectAuthorityOutcome outcome) =>
        outcome switch
        {
            MedusaCharacterEffectAuthorityOutcome
                .BoundAuthorityUnavailable =>
                ExactStatusAdmissionOutcome.AuthorityUnavailable,
            MedusaCharacterEffectAuthorityOutcome
                .CurrentMembershipRequired =>
                ExactStatusAdmissionOutcome
                    .RecipientOrTargetStale,
            _ => ExactStatusAdmissionOutcome.ProjectionChanged
        };

    private bool IsExactStatusPublicationTarget(
        WorldInstanceRuntime runtime,
        GameSessionContext target,
        long expectedLifeRevision,
        MedusaClientStatusOverlay medusaOverlay,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false,
        Action<MedusaCharacterEffectAuthorityOutcome>?
            targetAuthorityObserved = null,
        CompleteStatusPublicationFence? completeFence = null)
    {
        lock (_gate)
        {
            if (!TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    target,
                    expectedLifeRevision,
                    out target))
            {
                targetAuthorityObserved?.Invoke(
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired);
                return false;
            }
            if (!MatchesMonsterAttackTargetVitalsFence(
                    target,
                    expectedTargetVitalsRevision,
                    requireTargetDead))
            {
                targetAuthorityObserved?.Invoke(
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired);
                return false;
            }

            var matches = MatchesMedusaClientStatusOverlay(
                target,
                medusaOverlay,
                ExactStatusObservedAt(),
                out var currentOutcome);
            targetAuthorityObserved?.Invoke(currentOutcome);
            return matches &&
                (completeFence is null ||
                 MatchesCompleteStatusPublicationFence(
                     target,
                     completeFence,
                     ExactStatusObservedAt()));
        }
    }

    private static DateTimeOffset ExactStatusObservedAt() =>
        DateTimeOffset.UtcNow;

    private bool MatchesCompleteStatusPublicationFence(
        GameSessionContext target,
        CompleteStatusPublicationFence expected,
        DateTimeOffset now)
    {
        if (expected.State is { } state &&
            state.Revision != expected.BaselineRevision)
        {
            return false;
        }

        var current = MergeTrainingDummyClientStatusOverlays(
            target,
            expected.Baseline,
            now,
            out _,
            out _,
            out _,
            out _);
        if (!string.Equals(
                current.Fingerprint,
                expected.CompleteFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }
        if (expected.LocalAggregate is not { } expectedLocal)
        {
            return true;
        }

        // The self-only 10166 aggregate is part of the same complete
        // replacement transaction as 10167. Re-evaluate its independently
        // owned elemental layer at the exact capture instant and at admission
        // time. The former detects mutations between packet construction and
        // queue admission; the latter prevents an already-expired adjustment
        // from being reintroduced by a delayed packet.
        return ProjectElementalMovementStatus(
                   target.Session,
                   target.Character,
                   target.Ownership,
                   current.Aggregate,
                   expected.ObservedAt) == expectedLocal &&
               ProjectElementalMovementStatus(
                   target.Session,
                   target.Character,
                   target.Ownership,
                   current.Aggregate,
                   now) == expectedLocal;
    }
}
