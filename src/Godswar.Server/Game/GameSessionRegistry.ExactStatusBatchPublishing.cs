using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private ValueTask<ExactStatusAdmissionOutcome>
        TrySendStatusPacketPairExactAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long expectedRecipientLifeRevision,
        GameSessionContext target,
        long expectedTargetLifeRevision,
        MedusaClientStatusOverlay medusaOverlay,
        ReadOnlyMemory<byte> firstPacket,
        ReadOnlyMemory<byte> secondPacket,
        CancellationToken cancellationToken,
        string label,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false,
        Action<MedusaCharacterEffectAuthorityOutcome>?
            targetAuthorityObserved = null,
        CompleteStatusPublicationFence? completeFence = null,
        PlayerStatusState? completionStatusGate = null,
        ExactStatusDisconnectClaims? claimedDisconnects = null) =>
        TrySendStatusPacketBatchExactAsync(
            runtime,
            recipient,
            expectedRecipientLifeRevision,
            target,
            expectedTargetLifeRevision,
            medusaOverlay,
            [firstPacket, secondPacket],
            cancellationToken,
            label,
            expectedTargetVitalsRevision,
            requireTargetDead,
            targetAuthorityObserved,
            completeFence,
            completionStatusGate,
            claimedDisconnects);

    private ValueTask<ExactStatusAdmissionOutcome>
        TrySendStatusPacketBatchExactAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long expectedRecipientLifeRevision,
        GameSessionContext target,
        long expectedTargetLifeRevision,
        MedusaClientStatusOverlay medusaOverlay,
        IReadOnlyList<ReadOnlyMemory<byte>> batch,
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
        Task completion = Task.CompletedTask;
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
                .TryAdmitExactBatchOutcome(batch, out completion);
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
                completion,
                label);
        }
        else
        {
            ObserveExactAdmissionCompletionAfterStatusGate(
                completionStatusGate,
                recipient.Session,
                completion,
                label);
        }
        return ValueTask.FromResult(admittedTerminal
            ? claimedAdmissionFailure is null
                ? ExactStatusAdmissionOutcome.AdmittedTerminal
                : ExactStatusAdmissionOutcome.AdmittedTerminalClaimed
            : ExactStatusAdmissionOutcome.Admitted);
    }
}
