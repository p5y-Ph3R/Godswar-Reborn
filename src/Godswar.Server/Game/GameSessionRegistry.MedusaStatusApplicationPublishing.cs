using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task CompleteAbandonedMedusaStatusPublicationAsync(
        MonsterAttackEcsTransaction transaction)
    {
        var interruption = transaction.MedusaEffectInterruption;
        if (!HasMedusaProjectionObligation(transaction) &&
            interruption is not { Claimed: true })
        {
            return;
        }

        var target = transaction.TargetContext;
        try
        {
            if (target is not null)
            {
                FailClosedClaimedMedusaStatusPublication(
                    target,
                    MedusaProjectionLifeRevision(transaction));
            }
        }
        finally
        {
            if (interruption is { Claimed: true })
            {
                await interruption.CompleteAfterStatusPublicationAsync(
                    target?.DisplayName ?? "unknown",
                    mayPublishNotification: false);
            }
        }
    }

    private void ObserveDeferredMedusaVitalsPersistence(
        GameSessionContext target,
        Task persistence) =>
        _ = ObserveDeferredMedusaVitalsPersistenceAsync(
            target,
            persistence);

    private static async Task
        ObserveDeferredMedusaVitalsPersistenceAsync(
            GameSessionContext target,
            Task persistence)
    {
        try
        {
            await persistence;
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[medusa-status] deferred vitals persistence failed " +
                $"target={target.DisplayName}: {error.Message}");
        }
    }

    private async Task CompleteMedusaStatusPublicationAsync(
        GameSessionContext target,
        MonsterAttackEcsTransaction transaction,
        DateTimeOffset observedAt)
    {
        var statusPublished = false;
        try
        {
            if (transaction.MedusaMechanicsResult is { } mechanics)
            {
                statusPublished =
                    await PublishMedusaStatusApplicationAsync(
                    target,
                    mechanics,
                    observedAt,
                    CancellationToken.None);
            }
            else if (transaction.MedusaOutcome is not null &&
                transaction.Decision.Killed)
            {
                statusPublished =
                    await PublishMedusaStatusLifeCleanupAsync(
                    target,
                    observedAt,
                    transaction.Decision.AfterLifeRevision,
                    transaction.Decision.AfterVitalsRevision,
                    CancellationToken.None);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[medusa-status] application projection deferred " +
                $"target={target.DisplayName}: {error.Message}");
        }
        finally
        {
            try
            {
                if (transaction.MedusaEffectInterruption is { } interruption)
                {
                    await interruption.CompleteAfterStatusPublicationAsync(
                        target.DisplayName,
                        mayPublishNotification: true);
                }
            }
            finally
            {
                if (!statusPublished &&
                    HasMedusaProjectionObligation(transaction))
                {
                    FailClosedClaimedMedusaStatusPublication(
                        target,
                        MedusaProjectionLifeRevision(transaction));
                }
                else if (transaction.MedusaEffectInterruption is
                         {
                             RequiresNotification: true,
                             NotificationAdmitted: false
                         })
                {
                    FailClosedClaimedMedusaStatusPublication(
                        target,
                        MedusaProjectionLifeRevision(transaction));
                }
            }
        }
    }

    private static bool HasMedusaProjectionObligation(
        in MonsterAttackEcsTransaction transaction) =>
        transaction.MedusaMechanicsResult is
        {
            Outcome: MedusaMechanicHitOutcome.Applied or
                MedusaMechanicHitOutcome.Refreshed,
            Effect: { Definition.Kind: not
                MedusaEncounterEffectKind.Bleed },
            PeriodicDamage: null
        } ||
        transaction.MedusaOutcome is not null &&
        transaction.Decision.Killed;

    private static long MedusaProjectionLifeRevision(
        in MonsterAttackEcsTransaction transaction) =>
        transaction.MedusaMechanicsResult?.Effect?
            .TargetLifeRevision ??
        transaction.Decision.AfterLifeRevision;

    private void FailClosedClaimedMedusaStatusPublication(
        GameSessionContext target,
        long expectedLifeRevision)
    {
        ClientSession? claimed = null;
        Exception? diagnostic = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        expectedLifeRevision,
                        out target))
                {
                    return;
                }

                var current = CaptureMedusaClientStatusOverlay(
                    target,
                    DateTimeOffset.UtcNow);
                if (current.AuthorityOutcome ==
                        MedusaCharacterEffectAuthorityOutcome
                            .CurrentMembershipRequired)
                {
                    continue;
                }
                if (current.AuthorityOutcome ==
                        MedusaCharacterEffectAuthorityOutcome
                            .BoundAuthorityUnavailable ||
                    current.CanPublish)
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        expectedLifeRevision,
                        out claimed!);
                }
                break;
            }

            if (claimed is null)
            {
                _ = TryClaimExactMedusaMembershipDisconnect(
                    target,
                    expectedLifeRevision,
                    out claimed!);
            }
        }
        catch (Exception error)
        {
            diagnostic = error;
            try
            {
                _ = TryClaimExactMedusaMembershipDisconnect(
                    target,
                    expectedLifeRevision,
                    out claimed!);
            }
            catch
            {
            }
        }
        finally
        {
            if (claimed is not null)
            {
                try
                {
                    CompleteClaimedExactStatusDisconnect(claimed);
                }
                catch
                {
                }
            }
            if (diagnostic is not null)
            {
                try
                {
                    Console.WriteLine(
                        "[medusa-status] fail-closed status fault: " +
                        diagnostic.Message);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<bool> PublishMedusaStatusApplicationAsync(
        GameSessionContext target,
        MedusaMechanicHitResult mechanics,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (mechanics.Outcome is not (
                MedusaMechanicHitOutcome.Applied or
                MedusaMechanicHitOutcome.Refreshed) ||
            mechanics.Effect is not { } effect ||
            mechanics.PeriodicDamage is not null ||
            !MedusaClientStatusProjection.TryCreateEffectIdentity(
                effect,
                out var identity) ||
            !TryRebaseMedusaPublicationContext(
                target,
                effect.TargetLifeRevision,
                out target) ||
            !TryGetOrCreatePlayerStatusState(
                target.Session,
                out var state))
        {
            return false;
        }

        DateTimeOffset? runDeadline = null;
        var published = false;
        var scheduleExactIdentity = false;
        var failCloseAfterGate = false;
        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        effect.TargetLifeRevision,
                        out target))
                {
                    return false;
                }
                var projectionAt = CurrentMedusaProjectionTime(
                    observedAt);
                var overlay = CaptureMedusaClientStatusOverlay(
                    target,
                    projectionAt);
                InvokeProtocolCheckAfterMedusaStatusCapture();
                if (!overlay.CanPublish)
                {
                    if (overlay.AuthorityOutcome ==
                            MedusaCharacterEffectAuthorityOutcome
                                .CurrentMembershipRequired &&
                        TryRebaseMedusaPublicationContext(
                            target,
                            effect.TargetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    failCloseAfterGate =
                        RequiresFailClosedMedusaProjection(
                        overlay,
                        cancellationToken);
                    return false;
                }

                runDeadline = overlay.RunDeadline;
                scheduleExactIdentity = overlay.Presentations.Any(
                    presentation => presentation.Identity == identity);
                var publicationOutcome = overlay.AuthorityOutcome;
                published = await PublishStatusSnapshotLockedAsync(
                    target.Session,
                    state,
                    projectionAt,
                    $"medusa-status-{identity.StatusId}-" +
                    identity.ApplicationSequence,
                    force: true,
                    broadcast: true,
                    cancellationToken,
                    requiredMedusaEffect: attempt == 0 &&
                        scheduleExactIdentity
                            ? identity
                            : null,
                    medusaAuthorityObserved: outcome =>
                        publicationOutcome = outcome,
                    claimedDisconnects: admissionClaims);
                if (published)
                {
                    return true;
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
                        cancellationToken);
                    return false;
                }

                // The first exact snapshot may lose to a refresh, expiry, or
                // run terminal while packets are being admitted. Recompose
                // once from current owner state; never replay the old effect.
            }

            var current = CaptureMedusaClientStatusOverlay(
                target,
                CurrentMedusaProjectionTime(observedAt));
            failCloseAfterGate =
                current.AuthorityOutcome ==
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired ||
                RequiresFailClosedMedusaProjection(
                current,
                cancellationToken,
                disconnectIfPublishable: true);
            return false;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
            if (failCloseAfterGate)
            {
                FailClosedClaimedMedusaStatusPublication(
                    target,
                    effect.TargetLifeRevision);
            }
            if (published && scheduleExactIdentity)
            {
                ScheduleMedusaStatusPresentationExpiry(
                    target,
                    state,
                    identity,
                    runDeadline);
            }
        }
    }

    private async Task<bool> PublishMedusaStatusLifeCleanupAsync(
        GameSessionContext target,
        DateTimeOffset observedAt,
        long expectedLifeRevision,
        long expectedVitalsRevision,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrCreatePlayerStatusState(
                target.Session,
                out var state))
        {
            return false;
        }

        var failCloseAfterGate = false;
        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        expectedLifeRevision,
                        out target))
                {
                    return false;
                }
                var projectionAt = CurrentMedusaProjectionTime(
                    observedAt);
                var overlay = CaptureMedusaClientStatusOverlay(
                    target,
                    projectionAt);
                InvokeProtocolCheckAfterMedusaStatusCapture();
                if (!overlay.CanPublish)
                {
                    if (overlay.AuthorityOutcome ==
                            MedusaCharacterEffectAuthorityOutcome
                                .CurrentMembershipRequired &&
                        TryRebaseMedusaPublicationContext(
                            target,
                            expectedLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    failCloseAfterGate =
                        RequiresFailClosedMedusaProjection(
                        overlay,
                        cancellationToken);
                    return false;
                }

                var publicationOutcome = overlay.AuthorityOutcome;
                if (await PublishStatusSnapshotLockedAsync(
                        target.Session,
                        state,
                        projectionAt,
                        "medusa-status-life-cleanup",
                        force: true,
                        broadcast: true,
                        cancellationToken,
                        expectedTargetVitalsRevision:
                            expectedVitalsRevision,
                        requireTargetDead: true,
                        medusaAuthorityObserved: outcome =>
                            publicationOutcome = outcome,
                        claimedDisconnects: admissionClaims))
                {
                    return true;
                }
                if (publicationOutcome ==
                        MedusaCharacterEffectAuthorityOutcome
                            .CurrentMembershipRequired &&
                    TryRebaseMedusaPublicationContext(
                        target,
                        expectedLifeRevision,
                        out target) &&
                    HasSupersedingMedusaLifeCleanupVitals(
                        target,
                        expectedVitalsRevision))
                {
                    // A recovery/newer same-life vitals write won after the
                    // lethal commit. Suppress the obsolete lethal packets,
                    // but still reconcile the owner's current complete
                    // status view without replaying the dead-vitals fence.
                    publicationOutcome = overlay.AuthorityOutcome;
                    if (await PublishStatusSnapshotLockedAsync(
                            target.Session,
                            state,
                            CurrentMedusaProjectionTime(observedAt),
                            "medusa-status-life-cleanup-current",
                            force: true,
                            broadcast: true,
                            cancellationToken,
                            medusaAuthorityObserved: outcome =>
                                publicationOutcome = outcome,
                            claimedDisconnects: admissionClaims))
                    {
                        return true;
                    }
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
                        cancellationToken);
                    return false;
                }
            }

            var current = CaptureMedusaClientStatusOverlay(
                target,
                CurrentMedusaProjectionTime(observedAt));
            failCloseAfterGate =
                current.AuthorityOutcome ==
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired ||
                RequiresFailClosedMedusaProjection(
                current,
                cancellationToken,
                disconnectIfPublishable: true);
            return false;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
            if (failCloseAfterGate)
            {
                FailClosedClaimedMedusaStatusPublication(
                    target,
                    expectedLifeRevision);
            }
        }
    }

    private static bool HasSupersedingMedusaLifeCleanupVitals(
        GameSessionContext target,
        long expectedVitalsRevision)
    {
        lock (target.Character.VitalsSync)
        {
            return target.Character.VitalsRevision !=
                       expectedVitalsRevision ||
                   target.Character.CurrentHp != 0;
        }
    }

}
