using System.Buffers.Binary;
using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Monsters;

/// <summary>
/// Converts the capture-backed persistence shape into typed simulation data.
/// Packet bytes stop at this boundary and are never read by monster systems.
/// </summary>
internal static class CapturedMonsterSpawnHydrator
{
    public static void RegisterComponents(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RegisterComponent<MonsterIdentityComponent>();
        world.RegisterComponent<MonsterTransformComponent>();
        world.RegisterComponent<MonsterVitalsComponent>();
        world.RegisterComponent<MonsterMovementComponent>();
        world.RegisterComponent<MonsterCombatComponent>();
        world.RegisterComponent<MonsterLifecycleComponent>();
        world.RegisterComponent<MonsterRandomComponent>();
    }

    public static EntityId Hydrate(
        EcsWorld world,
        byte mapId,
        CapturedMonsterSpawn definition,
        DateTimeOffset initializedAt,
        TimeSpan corpseDespawnDelay,
        TimeSpan ordinaryRespawnDelay,
        WorldBossRespawnState? activeWorldBossRespawn,
        Guid runtimeInstanceId,
        WorldBossCatalog worldBossCatalog)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(worldBossCatalog);
        if (runtimeInstanceId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeInstanceId));
        }

        if (definition.MapId != mapId)
        {
            throw new ArgumentException(
                $"Monster {definition.ObjectId} belongs to map {definition.MapId}, not runtime map {mapId}.",
                nameof(definition));
        }

        var packet = definition.Packet;
        if (packet.Length < 44)
        {
            throw new ArgumentException(
                $"Monster {definition.ObjectId} appearance packet is too short.",
                nameof(definition));
        }

        var transform = new MonsterTransformComponent(
            definition.AppearanceX,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(32, 4)),
            definition.AppearanceZ,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(40, 4)));
        var vitals = new MonsterVitalsComponent(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4)),
            spawnGeneration: 1);
        var lifecycle = new MonsterLifecycleComponent(
            corpseDespawnDelay,
            worldBossCatalog.ResolveRespawnInterval(
                mapId,
                definition.TemplateKey,
                ordinaryRespawnDelay));
        var random = new MonsterRandomComponent(
            MonsterEcsRandom.CreateSeed(mapId, definition.ObjectId));

        if (activeWorldBossRespawn is not null &&
            activeWorldBossRespawn.MapId == mapId &&
            activeWorldBossRespawn.RespawnAt > initializedAt &&
            string.Equals(
                activeWorldBossRespawn.BossTemplateKey,
                definition.TemplateKey,
                StringComparison.Ordinal))
        {
            vitals.CurrentHealth = 0;
            vitals.IsAlive = false;
            vitals.IsSpawned = false;
            lifecycle.RespawnAt = activeWorldBossRespawn.RespawnAt;
        }

        var movement = new MonsterMovementComponent
        {
            NextMovementAt = initializedAt +
                MonsterEcsRandom.NextIdleDelay(ref random),
            MovementSpeedBasisPoints = 10_000
        };

        var entity = world.CreateEntity();
        world.Add(
            entity,
            new MonsterIdentityComponent(
                definition,
                runtimeInstanceId));
        world.Add(entity, transform);
        world.Add(entity, vitals);
        world.Add(entity, movement);
        world.Add(entity, new MonsterCombatComponent());
        world.Add(entity, lifecycle);
        world.Add(entity, random);
        return entity;
    }
}
