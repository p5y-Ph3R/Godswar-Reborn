namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal bool TrySetMonsterCorpseDespawnAt(
        uint objectId,
        uint expectedSpawnGeneration,
        DateTimeOffset? despawnAt)
    {
        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime is not null &&
                _monsterRuntime.TrySetCorpseDespawnAt(
                    objectId,
                    expectedSpawnGeneration,
                    despawnAt);
        }
    }
}
