using System.Collections.Concurrent;
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
        bool synchronizeLocalMovementSpeed = false)
    {
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

        if (!force && string.Equals(
                state.LastFingerprint,
                snapshot.Fingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        await session.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                context.Character,
                snapshot.Effects,
                snapshot.Aggregate),
            cancellationToken,
            "PlayerStatusEffects");

        if (synchronizeLocalMovementSpeed)
        {
            // The working server follows Ride's 10167 status snapshot with
            // 10166. Its float at offset 56 is what actually changes local
            // movement from the normal 1.0 multiplier to the mount value.
            await session.SendAsync(
                PacketBuilder.PlayerStatusUpdate(
                    context.Character,
                    snapshot.Aggregate.MovementSpeedMultiplier),
                cancellationToken,
                "PlayerMovementSpeed");
        }

        if (broadcast && context.WorldReady)
        {
            await BroadcastToMapAsync(
                context.MapId,
                PacketBuilder.PlayerStatusEffects(
                    context.Character,
                    context.ObjectId,
                    snapshot.Effects,
                    snapshot.Aggregate),
                cancellationToken,
                session,
                "PlayerStatusEffectsWorld");
        }

        state.LastFingerprint = snapshot.Fingerprint;
        Console.WriteLine(
            $"[status] full sync character={context.DisplayName} reason={reason} count={snapshot.Effects.Count} hit={snapshot.Aggregate.Hit} critical={snapshot.Aggregate.CriticalAppend} exp={snapshot.Aggregate.ExperienceBonus:R} speed={snapshot.Aggregate.MovementSpeedMultiplier:R}");
        return true;
    }

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
                    state.Lifetime.Token);
            }
            finally
            {
                state.Gate.Release();
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
