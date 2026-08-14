using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct RuntimeIncomingDamageMitigation(
    decimal PhysicalDamageReduction,
    decimal MagicDamageReduction,
    int PhysicalDefenseBonus,
    int MagicDefenseBonus);

internal sealed partial class GameSessionRegistry
{
    internal bool TryGetRuntimeIncomingDamageMitigation(
        ClientSession session,
        DateTimeOffset now,
        out RuntimeIncomingDamageMitigation mitigation)
    {
        mitigation = default;
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
            {
                return true;
            }

            lock (_gate)
            {
                return _sessions.ContainsKey(session);
            }
        }

        state.Gate.Wait();
        try
        {
            IEnumerable<ActiveRuntimeStatus> statuses;
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                RuntimeStatusSessionLookupHook?.Invoke();
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return false;
                    }

                    statuses = EvaluatePlayerStatusEcsLocked(
                            session,
                            state,
                            context,
                            now)
                        .ActiveRuntimeStatuses;
                }
            }
            else
            {
                statuses = state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now)
                    .ToArray();
            }

            var active = statuses.ToArray();
            mitigation = new RuntimeIncomingDamageMitigation(
                Math.Clamp(
                    active.Sum(static status =>
                        status.PhysicalDamageReduction),
                    0m,
                    1m),
                Math.Clamp(
                    active.Sum(static status =>
                        status.MagicDamageReduction),
                    0m,
                    1m),
                SumDefense(
                    active,
                    static status => status.Modifiers.PhysicalDefense),
                SumDefense(
                    active,
                    static status => status.Modifiers.MagicDefense));
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static int SumDefense(
        IEnumerable<ActiveRuntimeStatus> statuses,
        Func<ActiveRuntimeStatus, int> selector) =>
        (int)Math.Clamp(
            statuses.Aggregate(
                0L,
                (sum, status) => sum + selector(status)),
            int.MinValue,
            int.MaxValue);
}
