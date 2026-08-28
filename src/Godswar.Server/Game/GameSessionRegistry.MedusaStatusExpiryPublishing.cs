using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    private Action? _protocolCheckBeforeMedusaExpirySetup = null;
#endif

    private static DateTimeOffset CurrentMedusaProjectionTime(
        DateTimeOffset observedAt)
    {
        var now = DateTimeOffset.UtcNow;
        return now < observedAt ? observedAt : now;
    }

    private static bool RequiresFailClosedMedusaProjection(
        MedusaClientStatusOverlay current,
        CancellationToken cancellationToken,
        bool disconnectIfPublishable = false) =>
        RequiresFailClosedMedusaProjection(
            current.AuthorityOutcome,
            cancellationToken,
            disconnectIfPublishable);

    private static bool RequiresFailClosedMedusaProjection(
        MedusaCharacterEffectAuthorityOutcome currentOutcome,
        CancellationToken cancellationToken,
        bool disconnectIfPublishable = false) =>
        !cancellationToken.IsCancellationRequested &&
            (currentOutcome ==
                 MedusaCharacterEffectAuthorityOutcome
                     .BoundAuthorityUnavailable ||
             disconnectIfPublishable &&
             currentOutcome is (
                 MedusaCharacterEffectAuthorityOutcome
                     .ResolvedActive or
                 MedusaCharacterEffectAuthorityOutcome
                     .RunNotActive));

    private void ScheduleMedusaStatusPresentationExpiry(
        GameSessionContext target,
        PlayerStatusState state,
        in MedusaClientStatusEffectIdentity expected,
        DateTimeOffset? runDeadline)
    {
        var captured = expected;
        _ = PublishMedusaStatusPresentationExpiryAsync(
            target,
            state,
            captured,
            runDeadline);
    }

    private async Task PublishMedusaStatusPresentationExpiryAsync(
        GameSessionContext target,
        PlayerStatusState state,
        MedusaClientStatusEffectIdentity expected,
        DateTimeOffset? runDeadline)
    {
        var failCloseAfterGate = false;
        ExactStatusDisconnectClaims? admissionClaims = null;
        MedusaClientStatusEffectIdentity? reschedule = null;
        DateTimeOffset? rescheduleRunDeadline = null;
        try
        {
#if DEBUG
            _protocolCheckBeforeMedusaExpirySetup?.Invoke();
#endif
            admissionClaims = new ExactStatusDisconnectClaims();
            var dueAt = runDeadline is { } deadline &&
                        deadline < expected.ExpiresAt
                ? deadline
                : expected.ExpiresAt;
            var delay = dueAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, state.Lifetime.Token);
            }

            await state.Gate.WaitAsync(state.Lifetime.Token);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (!TryRebaseMedusaPublicationContext(
                            target,
                            expected.TargetLifeRevision,
                            out target))
                    {
                        return;
                    }
                    var now = DateTimeOffset.UtcNow;
                    var overlay = CaptureMedusaClientStatusOverlay(
                        target,
                        now);
                    InvokeProtocolCheckAfterMedusaStatusCapture();
                    if (!overlay.CanPublish)
                    {
                        if (overlay.AuthorityOutcome ==
                                MedusaCharacterEffectAuthorityOutcome
                                    .CurrentMembershipRequired &&
                            TryRebaseMedusaPublicationContext(
                                target,
                                expected.TargetLifeRevision,
                                out target))
                        {
                            continue;
                        }
                        failCloseAfterGate =
                            RequiresFailClosedMedusaProjection(
                            overlay,
                            state.Lifetime.Token);
                        return;
                    }

                    var sameKind = overlay.Presentations
                        .FirstOrDefault(presentation =>
                            presentation.Identity.Kind == expected.Kind);
                    if (sameKind is not null &&
                        sameKind.Identity != expected)
                    {
                        // A refresh owns a new sequence/expiry. Its own timer
                        // and application publication are authoritative.
                        return;
                    }
                    if (sameKind is not null)
                    {
                        reschedule = expected;
                        rescheduleRunDeadline =
                            overlay.RunDeadline ?? runDeadline;
                        return;
                    }

                    var publicationOutcome = overlay.AuthorityOutcome;
                    if (await PublishStatusSnapshotLockedAsync(
                            target.Session,
                            state,
                            now,
                            $"medusa-status-{expected.StatusId}-expired-" +
                            expected.ApplicationSequence,
                            force: true,
                            broadcast: true,
                            state.Lifetime.Token,
                            medusaAuthorityObserved: outcome =>
                                publicationOutcome = outcome,
                            claimedDisconnects: admissionClaims))
                    {
                        return;
                    }
                    if (publicationOutcome is
                        MedusaCharacterEffectAuthorityOutcome
                            .BoundAuthorityUnavailable or
                        MedusaCharacterEffectAuthorityOutcome
                            .CurrentMembershipRequired)
                    {
                        failCloseAfterGate =
                            RequiresFailClosedMedusaProjection(
                            publicationOutcome,
                            state.Lifetime.Token);
                        return;
                    }
                }

                var current = CaptureMedusaClientStatusOverlay(
                    target,
                    DateTimeOffset.UtcNow);
                failCloseAfterGate =
                    current.AuthorityOutcome ==
                        MedusaCharacterEffectAuthorityOutcome
                            .CurrentMembershipRequired ||
                    RequiresFailClosedMedusaProjection(
                    current,
                    state.Lifetime.Token,
                    disconnectIfPublishable: true);
            }
            finally
            {
                state.Gate.Release();
                admissionClaims?.CompleteAll(this);
                if (failCloseAfterGate)
                {
                    FailClosedClaimedMedusaStatusPublication(
                        target,
                        expected.TargetLifeRevision);
                }
                if (reschedule is { } refreshed)
                {
                    ScheduleMedusaStatusPresentationExpiry(
                        target,
                        state,
                        refreshed,
                        rescheduleRunDeadline);
                }
            }
        }
        catch (OperationCanceledException) when (
            state.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            FailClosedClaimedMedusaStatusPublication(
                target,
                expected.TargetLifeRevision);
            try
            {
                Console.WriteLine(
                    "[medusa-status] expiry projection deferred " +
                    $"target={target.DisplayName} " +
                    $"status={expected.StatusId} " +
                    $"sequence={expected.ApplicationSequence}: " +
                    error.Message);
            }
            catch
            {
            }
        }
    }
}
