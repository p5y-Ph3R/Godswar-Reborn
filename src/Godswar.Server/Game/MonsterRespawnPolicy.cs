using Godswar.Server.State;

namespace Godswar.Server.Game;

internal enum MonsterRespawnPolicy
{
    Timed = 0,
    Never = 1
}

internal static class MonsterRespawnPolicyRules
{
    public static void Validate(MonsterRespawnPolicy policy)
    {
        if (policy is not (MonsterRespawnPolicy.Timed or MonsterRespawnPolicy.Never))
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy,
                "Unsupported monster respawn policy.");
        }
    }

    public static TimeSpan? ResolveOrdinaryDelay(
        MonsterRespawnPolicy policy,
        TimeSpan corpseDespawnDelay,
        TimeSpan? configuredRespawnDelay)
    {
        Validate(policy);
        if (policy == MonsterRespawnPolicy.Never)
        {
            if (configuredRespawnDelay is not null)
            {
                throw new ArgumentException(
                    "A never-respawn runtime cannot also define a respawn delay.",
                    nameof(configuredRespawnDelay));
            }

            return null;
        }

        var delay = configuredRespawnDelay ?? MonsterMapRuntime.DefaultRespawnDelay;
        if (delay <= corpseDespawnDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredRespawnDelay),
                "Monster respawn delay must be later than corpse despawn.");
        }

        return delay;
    }

    public static void RejectTimedWorldBossConfiguration(
        MonsterRespawnPolicy policy,
        byte mapId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        WorldBossRespawnState? activeWorldBossRespawn,
        WorldBossCatalog worldBossCatalog)
    {
        Validate(policy);
        if (policy != MonsterRespawnPolicy.Never)
        {
            return;
        }

        if (activeWorldBossRespawn is not null)
        {
            throw new ArgumentException(
                "A never-respawn runtime cannot restore a timed world-boss respawn.",
                nameof(activeWorldBossRespawn));
        }

        if (definitions.Any(definition =>
                worldBossCatalog.IsWorldBoss(mapId, definition.TemplateKey)))
        {
            throw new ArgumentException(
                "A configured world boss requires the timed respawn policy.",
                nameof(worldBossCatalog));
        }
    }
}
