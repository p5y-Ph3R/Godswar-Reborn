using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    public IMonsterMapRuntime InitializeMonsters(
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        lock (_monsterRuntimeGate)
        {
            if (_monsterRuntime is not null)
            {
                return _monsterRuntime;
            }

            EnsureMonsterObjectIdsDoNotCollideWithNpcs(definitions);
            var runtime = MonsterMapRuntimeFactory.Create(
                _monsterRuntimeMode,
                MapId,
                definitions,
                initializedAt,
                activeWorldBossRespawn: activeWorldBossRespawn);
            _monsterRuntime = runtime;
            return runtime;
        }
    }
}
