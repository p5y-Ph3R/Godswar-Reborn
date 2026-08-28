using System.Collections.Concurrent;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task<int> ReconcileExperienceBoostStatusesOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return 0;
        }

        var sent = 0;
        foreach (var context in _sessions.Values.Where(static context => context.WorldReady))
        {
            try
            {
                var boosts = await GetExperienceBoostStateAsync(
                    context.Session,
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                if (await RefreshExperienceStatusesAndPublishAsync(
                        context.Session,
                        boosts,
                        now,
                        "reconcile",
                        force: false,
                        broadcast: true,
                        cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[status] EXP boost reconciliation failed character={context.DisplayName}: {ex.Message}");
            }
        }

        if (sent > 0)
        {
            Console.WriteLine($"[status] EXP boost reconciliation updated={sent}");
        }

        return sent;
    }

    private async Task<bool> PublishStatusSnapshotLockedAsync(
        ClientSession session,
        PlayerStatusState state,
        DateTimeOffset now,
        string reason,
        bool force,
        bool broadcast,
        CancellationToken cancellationToken,
        bool forceLocalGameDataSynchronization = false,
        MedusaClientStatusEffectIdentity? requiredMedusaEffect = null,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false,
        Action<MedusaCharacterEffectAuthorityOutcome>?
            medusaAuthorityObserved = null,
        ExactStatusDisconnectClaims? claimedDisconnects = null)
    {
        ArgumentNullException.ThrowIfNull(claimedDisconnects);
        if (!_sessions.TryGetValue(session, out var context))
        {
            return false;
        }

        PlayerStatusSnapshot snapshot;
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            snapshot = EvaluatePlayerStatusEcsLocked(
                    session,
                    state,
                    context,
                    now)
                .Snapshot;
        }
        else
        {
            foreach (var expiredKind in state.RuntimeStatuses
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                state.RuntimeStatuses.Remove(expiredKind);
            }
            RefreshSkillCastControlSnapshot(state);

            snapshot = PlayerStatusComposer.Compose(
                state.ExperienceBoosts,
                state.RuntimeStatuses.Values,
                now);
        }

        var baselineSnapshot = snapshot;
        snapshot = MergeTrainingDummyClientStatusOverlays(
            context,
            snapshot,
            now,
            out var elementalOverlay,
            out _,
            out var medusaOverlay,
            out _);
        medusaAuthorityObserved?.Invoke(
            medusaOverlay.AuthorityOutcome);
        var completeFence = new CompleteStatusPublicationFence(
            state,
            state.Revision,
            baselineSnapshot,
            snapshot.Fingerprint,
            now);

        // A complete replacement packet must never erase a possibly-active
        // owner effect merely because its bound authority is partial, stale,
        // or temporarily unavailable. Defer the entire 10167 publication.
        if (medusaOverlay.IsBound && !medusaOverlay.CanPublish)
        {
            return false;
        }
        if (requiredMedusaEffect is { } required &&
            (!medusaOverlay.Presentations.Any(presentation =>
                 presentation.Identity == required) ||
             !snapshot.Effects.Any(effect =>
                 effect.StatusId == required.StatusId)))
        {
            return false;
        }

        if (!force && string.Equals(
                state.LastFingerprint,
                snapshot.Fingerprint,
                StringComparison.Ordinal))
        {
            if (!TryCaptureStatusPublicationTarget(
                    context,
                    out var unchangedRuntime,
                    out var unchangedLife) ||
                !IsExactStatusPublicationTarget(
                    unchangedRuntime,
                    context,
                    unchangedLife,
                    medusaOverlay,
                    expectedTargetVitalsRevision,
                    requireTargetDead))
            {
                return false;
            }
            state.LastPublishedElementalFingerprint =
                elementalOverlay.Fingerprint;
            return false;
        }

        var synchronizeLocalGameData =
            forceLocalGameDataSynchronization ||
            HasLocalGameDataAggregateChanged(
                state.LastPublishedAggregate,
                snapshot.Aggregate);
        var statusPacket = PacketBuilder.PlayerStatusEffects(
            context.Character,
            snapshot.Effects,
            snapshot.Aggregate);
        if (!WorldInstances.TryFind(
                context.WorldInstanceId,
                out var runtime) ||
            !_playerLifeRevisions.TryGetValue(
                session,
                out var lifeRevision))
        {
            return false;
        }

        if (synchronizeLocalGameData)
        {
            // 10167 owns the native status list, while PersonalInfo reads its
            // displayed derived values from the local 10166 GameData copy.
            // Publish both whenever a runtime modifier changes, including on
            // expiry, so the panel cannot retain either base or buffed values.
            var localAggregate = ProjectElementalMovementStatus(
                session,
                context.Character,
                context.Ownership,
                snapshot.Aggregate,
                now);
            var admissionOutcome =
                await TrySendStatusPacketPairExactAsync(
                runtime,
                context,
                lifeRevision,
                context,
                lifeRevision,
                medusaOverlay,
                statusPacket,
                PacketBuilder.PlayerStatusUpdate(
                    context.Character,
                    localAggregate),
                    cancellationToken,
                    "PlayerStatusEffectsAndMovementSpeed",
                    expectedTargetVitalsRevision,
                    requireTargetDead,
                    medusaAuthorityObserved,
                    completeFence with
                    {
                        LocalAggregate = localAggregate
                    },
                    completionStatusGate: state,
                    claimedDisconnects: claimedDisconnects);
            if (!WasAdmitted(admissionOutcome))
            {
                return false;
            }
        }
        else
        {
            var admissionOutcome =
                await TrySendStatusPacketExactAsync(
                    runtime,
                    context,
                    lifeRevision,
                    context,
                    lifeRevision,
                    medusaOverlay,
                    statusPacket,
                    cancellationToken,
                    "PlayerStatusEffects",
                    expectedTargetVitalsRevision,
                    requireTargetDead,
                    medusaAuthorityObserved,
                    completeFence,
                    completionStatusGate: state,
                    claimedDisconnects: claimedDisconnects);
            if (!WasAdmitted(admissionOutcome))
            {
                return false;
            }
        }

        InvokeProtocolCheckAfterMedusaStatusSelfAdmission();

        if (broadcast && context.WorldReady)
        {
            await PublishStatusPacketWorldExactAsync(
                runtime,
                context,
                lifeRevision,
                medusaOverlay,
                PacketBuilder.PlayerStatusEffects(
                    context.Character,
                    context.ObjectId,
                    snapshot.Effects,
                    snapshot.Aggregate),
                cancellationToken,
                "PlayerStatusEffectsWorld",
                expectedTargetVitalsRevision,
                requireTargetDead,
                completeFence: completeFence,
                completionStatusGate: state,
                claimedDisconnects: claimedDisconnects);
        }

        if (!IsExactStatusPublicationTarget(
                runtime,
                context,
                lifeRevision,
                medusaOverlay,
                expectedTargetVitalsRevision,
                requireTargetDead,
                medusaAuthorityObserved,
                completeFence))
        {
            return false;
        }

        state.LastPublishedAggregate = snapshot.Aggregate;
        state.LastPublishedElementalFingerprint =
            elementalOverlay.Fingerprint;
        state.LastFingerprint = snapshot.Fingerprint;
        Console.WriteLine(
            $"[status] full sync character={context.DisplayName} reason={reason} count={snapshot.Effects.Count} control={snapshot.Aggregate.Control} hit={snapshot.Aggregate.Hit} critical={snapshot.Aggregate.CriticalAppend} pdef={snapshot.Aggregate.PhysicalDefense} mdef={snapshot.Aggregate.MagicDefense} dodge={snapshot.Aggregate.Dodge} critical-resistance={snapshot.Aggregate.CriticalResistance} exp={snapshot.Aggregate.ExperienceBonus:R} speed={snapshot.Aggregate.MovementSpeedMultiplier:R} game-data={synchronizeLocalGameData}");
        return true;
    }

    private static bool HasLocalGameDataAggregateChanged(
        ClientStatusAggregate previous,
        ClientStatusAggregate current) =>
        previous.Hit != current.Hit ||
        previous.CriticalAppend != current.CriticalAppend ||
        previous.MovementSpeedMultiplier != current.MovementSpeedMultiplier ||
        previous.PhysicalDefense != current.PhysicalDefense ||
        previous.MagicDefense != current.MagicDefense ||
        previous.Dodge != current.Dodge ||
        previous.CriticalResistance != current.CriticalResistance;

    private bool TryGetOrCreatePlayerStatusState(
        ClientSession session,
        out PlayerStatusState state)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(session))
            {
                state = null!;
                return false;
            }

            state = _playerStatusStates.GetOrAdd(
                session,
                static _ => new PlayerStatusState());
            return true;
        }
    }

    private void ScheduleRuntimeStatusExpiry(
        ClientSession session,
        PlayerStatusState state,
        ActiveRuntimeStatus expected)
    {
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            return;
        }

        _ = ExpireRuntimeStatusAsync(session, state, expected);
    }

    private async Task ExpireRuntimeStatusAsync(
        ClientSession session,
        PlayerStatusState state,
        ActiveRuntimeStatus expected)
    {
        try
        {
            var delay = expected.ExpiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, state.Lifetime.Token);
            }

            var admissionClaims = new ExactStatusDisconnectClaims();
            await state.Gate.WaitAsync(state.Lifetime.Token);
            try
            {
                if (!state.RuntimeStatuses.TryGetValue(expected.Kind, out var current) ||
                    current.Revision != expected.Revision ||
                    current.StatusId != expected.StatusId ||
                    current.ExpiresAt != expected.ExpiresAt)
                {
                    return;
                }

                state.RuntimeStatuses.Remove(expected.Kind);
                RefreshSkillCastControlSnapshot(state);
                await PublishStatusSnapshotLockedAsync(
                    session,
                    state,
                    DateTimeOffset.UtcNow,
                    $"status-{expected.StatusId}-expired",
                    force: true,
                    broadcast: true,
                    state.Lifetime.Token,
                    claimedDisconnects: admissionClaims);
            }
            finally
            {
                state.Gate.Release();
                admissionClaims.CompleteAll(this);
            }
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[status] expiry publish failed status={expected.StatusId}: {ex.Message}");
        }
    }

}
