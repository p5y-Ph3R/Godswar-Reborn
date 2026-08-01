using Godswar.Server.State;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.Game;

internal static class MonsterMapRuntimeFactory
{
    public static IMonsterMapRuntime Create(
        MonsterRuntimeMode mode,
        byte mapId,
        IEnumerable<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        TimeSpan? corpseDespawnDelay = null,
        TimeSpan? respawnDelay = null,
        WorldBossRespawnState? activeWorldBossRespawn = null,
        WorldBossCatalog? worldBossCatalog = null)
    {
        var runtimeInstanceId = Guid.NewGuid();
        return mode switch
        {
            MonsterRuntimeMode.Legacy => new MonsterMapRuntime(
                mapId,
                definitions,
                initializedAt,
                corpseDespawnDelay,
                respawnDelay,
                activeWorldBossRespawn,
                runtimeInstanceId,
                worldBossCatalog),
            MonsterRuntimeMode.Ecs => new EcsMonsterMapRuntime(
                mapId,
                definitions,
                initializedAt,
                corpseDespawnDelay,
                respawnDelay,
                activeWorldBossRespawn,
                runtimeInstanceId,
                worldBossCatalog),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Unsupported monster runtime mode.")
        };
    }
}
