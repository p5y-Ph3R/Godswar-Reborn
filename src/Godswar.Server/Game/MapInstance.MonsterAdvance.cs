using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    public MonsterRuntimeTick AdvanceMonsters(
        DateTimeOffset now,
        Func<ClientSession, long?>? lifeRevisionResolver = null)
    {
        var combatTargets = Snapshot()
            .Where(static context => context.WorldReady)
            .Select(context => (
                Context: context,
                LifeRevision: lifeRevisionResolver is null
                    ? (long?)0
                    : lifeRevisionResolver(context.Session)))
            .Where(static candidate =>
                candidate.LifeRevision is not null)
            .Select(static candidate => ProjectCombatTarget(
                candidate.Context,
                candidate.LifeRevision!.Value))
            .ToArray();

        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime?.Advance(now, combatTargets) ??
                new MonsterRuntimeTick(false, []);
        }
    }

    private static MonsterCombatTarget ProjectCombatTarget(
        GameSessionContext context,
        long lifeRevision)
    {
        lock (context.Character.VitalsSync)
        {
            return new MonsterCombatTarget(
                context.CharacterId,
                context.Character.PositionX,
                context.Character.PositionZ,
                context.Character.CurrentHp > 0,
                context.ObjectId,
                lifeRevision,
                context.Ownership,
                context.WorldInstanceId,
                context.WorldRevision,
                context.WorldMembershipEpoch);
        }
    }
}
