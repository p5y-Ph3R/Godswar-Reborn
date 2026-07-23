using Godswar.Server.Game;

namespace Godswar.Server.World.Systems.Monsters;

internal readonly record struct MonsterEcsUpdateEvent(
    MonsterRuntimeUpdate Update);

internal sealed class MonsterEcsSimulationFrame
{
    private IReadOnlyDictionary<int, MonsterCombatTarget> _targets =
        new Dictionary<int, MonsterCombatTarget>();
    private IReadOnlyList<MonsterRuntimeUpdate> _pendingUpdates = [];

    public DateTimeOffset Now { get; private set; }

    public IReadOnlyDictionary<int, MonsterCombatTarget> Targets =>
        _targets;

    public IReadOnlyList<MonsterRuntimeUpdate> PendingUpdates =>
        _pendingUpdates;

    public bool PositionsChanged { get; set; }

    public void Prepare(
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget>? combatTargets,
        IReadOnlyList<MonsterRuntimeUpdate> pendingUpdates)
    {
        Now = now;
        _targets = (combatTargets ?? [])
            .GroupBy(target => target.CharacterId)
            .ToDictionary(group => group.Key, group => group.Last());
        _pendingUpdates = pendingUpdates;
        PositionsChanged = false;
    }
}
