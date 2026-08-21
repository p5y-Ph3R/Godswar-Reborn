using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct RuntimeIncomingDamageMitigation(
    decimal PhysicalDamageReduction,
    decimal MagicDamageReduction,
    int PhysicalDefenseBonus,
    int MagicDefenseBonus,
    ClientStatusAggregate StatusAggregate = default,
    int PhysicalDamageTakenIncreaseBasisPoints = 0,
    int MagicDamageTakenIncreaseBasisPoints = 0);

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
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var context))
                {
                    return false;
                }

                mitigation = ComposeIncomingMitigation(
                    [],
                    ClientStatusAggregate.Empty,
                    CaptureTrainingDummyHostileIncomingModifiersLocked(
                        context,
                        now));
                return true;
            }
        }

        state.Gate.Wait();
        try
        {
            IEnumerable<ActiveRuntimeStatus> statuses;
            ClientStatusAggregate statusAggregate;
            var hostile = default(TrainingDummyHostileIncomingModifiers);
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

                    var decision = EvaluatePlayerStatusEcsLocked(
                        session,
                        state,
                        context,
                        now);
                    statuses = decision.ActiveRuntimeStatuses;
                    statusAggregate = decision.Snapshot.Aggregate;
                    hostile =
                        CaptureTrainingDummyHostileIncomingModifiersLocked(
                            context,
                            now);
                }
            }
            else
            {
                var activeStatuses = state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now)
                    .ToArray();
                statuses = activeStatuses;
                statusAggregate = PlayerStatusComposer.Compose(
                        ExperienceBoostState.Empty,
                        activeStatuses,
                        now)
                    .Aggregate;
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(session, out var context))
                    {
                        return false;
                    }

                    hostile =
                        CaptureTrainingDummyHostileIncomingModifiersLocked(
                            context,
                            now);
                }
            }

            var active = statuses.ToArray();
            mitigation = ComposeIncomingMitigation(
                active,
                statusAggregate,
                hostile);
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

    private static RuntimeIncomingDamageMitigation
        ComposeIncomingMitigation(
            IReadOnlyCollection<ActiveRuntimeStatus> statuses,
            in ClientStatusAggregate statusAggregate,
            in TrainingDummyHostileIncomingModifiers hostile) =>
        new(
            Math.Clamp(
                statuses.Sum(static status =>
                    status.PhysicalDamageReduction) +
                ToFraction(
                    hostile.PhysicalDamageReductionBasisPoints),
                0m,
                1m),
            Math.Clamp(
                statuses.Sum(static status =>
                    status.MagicDamageReduction) +
                ToFraction(
                    hostile.MagicDamageReductionBasisPoints),
                0m,
                1m),
            SaturatingAdd(
                SumDefense(
                    statuses,
                    static status => status.Modifiers.PhysicalDefense),
                hostile.PhysicalDefense),
            SaturatingAdd(
                SumDefense(
                    statuses,
                    static status => status.Modifiers.MagicDefense),
                hostile.MagicDefense),
            statusAggregate,
            hostile.PhysicalDamageTakenIncreaseBasisPoints,
            hostile.MagicDamageTakenIncreaseBasisPoints);

    private static decimal ToFraction(int basisPoints) =>
        Math.Clamp(basisPoints, 0, 10_000) / 10_000m;

    private static int SaturatingAdd(int left, int right) =>
        (int)Math.Clamp((long)left + right, int.MinValue, int.MaxValue);
}
