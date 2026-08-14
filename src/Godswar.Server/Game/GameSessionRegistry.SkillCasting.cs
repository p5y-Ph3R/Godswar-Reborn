using System.Collections.Concurrent;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<
        ClientSession,
        Func<
            SkillCastInterruptionReason,
            CancellationToken,
            Task?,
            Task>> _skillCastInterruptionSinks = [];

    public void RegisterSkillCastInterruptionSink(
        ClientSession session,
        Func<
            SkillCastInterruptionReason,
            CancellationToken,
            Task?,
            Task> sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_skillCastInterruptionSinks.TryAdd(session, sink))
        {
            throw new InvalidOperationException(
                "A skill-cast interruption sink is already registered " +
                "for this session.");
        }
    }

    public void UnregisterSkillCastInterruptionSink(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _skillCastInterruptionSinks.TryRemove(session, out _);
    }

    public Task RequestSkillCastInterruptionAsync(
        ClientSession session,
        SkillCastInterruptionReason reason,
        CancellationToken cancellationToken,
        Task? notificationBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _skillCastInterruptionSinks.TryGetValue(
            session,
            out var sink)
            ? sink(
                reason,
                cancellationToken,
                notificationBarrier)
            : Task.CompletedTask;
    }

    public PlayerSkillCastControl GetPlayerSkillCastControl(
        ClientSession session,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (HasActiveElementalShock(session, now))
        {
            return PlayerSkillCastControl.Stunned;
        }

        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return PlayerSkillCastControl.None;
        }

        var resolved = PlayerSkillCastControl.None;
        foreach (var status in Volatile.Read(
                     ref state.SkillCastControlStatuses))
        {
            if (status.ExpiresAt <= now)
            {
                continue;
            }

            var candidate =
                PlayerSkillCastControlCatalog.ResolveActiveBlock(
                    status.StatusId);
            if (candidate == PlayerSkillCastControl.Stunned)
            {
                return candidate;
            }

            if (candidate == PlayerSkillCastControl.Silenced)
            {
                resolved = candidate;
            }
        }

        return resolved;
    }

    private bool HasActiveElementalShock(
        ClientSession session,
        DateTimeOffset now)
    {
        ElementalCombatSessionFence fence;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !context.WorldReady ||
                !context.Ownership.IsValid)
            {
                return false;
            }

            fence = new(
                context.CharacterId,
                context.MapId,
                context.Ownership);
        }

        return TryGetElementalStatusAdjustment(
                session,
                fence,
                now.ToUnixTimeMilliseconds(),
                movementSpeed: 0,
                physicalDefense: 0,
                magicDefense: 0,
                hitRating: 0,
                healingReceived: 0,
                out var adjustment) &&
            !adjustment.MovementAllowed;
    }

    private static void RefreshSkillCastControlSnapshot(
        PlayerStatusState state)
    {
        Volatile.Write(
            ref state.SkillCastControlStatuses,
            state.RuntimeStatuses.Values
                .Where(status =>
                    PlayerSkillCastControlCatalog.ResolveActiveBlock(
                        status.StatusId) !=
                    PlayerSkillCastControl.None)
                .ToArray());
    }
}
