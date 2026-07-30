using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
{
    private static MonsterRuntimeState CreateState(
        byte mapId,
        CapturedMonsterSpawn definition,
        DateTimeOffset initializedAt,
        WorldBossRespawnState? activeWorldBossRespawn,
        Guid runtimeInstanceId)
    {
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

        var monster = new MonsterRuntimeState(
            definition,
            definition.AppearanceX,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(32, 4)),
            definition.AppearanceZ,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(40, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4)),
            CreateSeed(mapId, definition.ObjectId),
            spawnGeneration: 1,
            runtimeInstanceId);
        if (activeWorldBossRespawn is not null &&
            activeWorldBossRespawn.MapId == mapId &&
            activeWorldBossRespawn.RespawnAt > initializedAt &&
            string.Equals(
                activeWorldBossRespawn.BossTemplateKey,
                definition.TemplateKey,
                StringComparison.Ordinal))
        {
            monster.CurrentHealth = 0;
            monster.IsAlive = false;
            monster.IsSpawned = false;
            monster.RespawnAt = activeWorldBossRespawn.RespawnAt;
        }

        monster.NextMovementAt = initializedAt + NextIdleDelay(monster);
        return monster;
    }

    private static void StartMovement(MonsterRuntimeState monster, DateTimeOffset now)
    {
        var selected = false;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var ticks = MinimumMovementTicks +
                        (int)(NextRandom(monster) %
                              (MaximumMovementTicks - MinimumMovementTicks + 1));
            var angle = NextUnit(monster) * Math.Tau;
            var velocityX = (float)(Math.Sin(angle) * MovementStep);
            var velocityZ = (float)(Math.Cos(angle) * MovementStep);
            var targetX = monster.CurrentX + (velocityX * ticks);
            var targetZ = monster.CurrentZ + (velocityZ * ticks);
            if (DistanceSquared(monster.HomeX, monster.HomeZ, targetX, targetZ) >
                (MaximumRoamRadius * MaximumRoamRadius))
            {
                continue;
            }

            SetMovement(monster, now, ticks, velocityX, velocityZ, targetX, targetZ);
            selected = true;
            break;
        }

        if (selected)
        {
            return;
        }

        // A valid inward one-step move always exists, including from the radius
        // boundary. This fallback also makes the bound independent of RNG quality.
        var towardHomeX = monster.HomeX - monster.CurrentX;
        var towardHomeZ = monster.HomeZ - monster.CurrentZ;
        var distance = Math.Sqrt((towardHomeX * towardHomeX) + (towardHomeZ * towardHomeZ));
        var velocityXFallback = distance > double.Epsilon
            ? (float)((towardHomeX / distance) * MovementStep)
            : MovementStep;
        var velocityZFallback = distance > double.Epsilon
            ? (float)((towardHomeZ / distance) * MovementStep)
            : 0f;
        SetMovement(
            monster,
            now,
            MinimumMovementTicks,
            velocityXFallback,
            velocityZFallback,
            monster.CurrentX + velocityXFallback,
            monster.CurrentZ + velocityZFallback);
    }

    private static void SetMovement(
        MonsterRuntimeState monster,
        DateTimeOffset now,
        int ticks,
        float velocityX,
        float velocityZ,
        float targetX,
        float targetZ)
    {
        monster.IsMoving = true;
        monster.MovementTicks = checked((uint)ticks);
        monster.RemainingMovementTicks = checked((uint)ticks);
        monster.VelocityX = velocityX;
        monster.VelocityZ = velocityZ;
        monster.TargetX = targetX;
        monster.TargetZ = targetZ;
        monster.Facing = MathF.Atan2(velocityX, velocityZ);
        monster.NextMovementStepAt = now + TickInterval;
    }

    private static MonsterRuntimeState CreateRespawnedState(
        MonsterRuntimeState retired,
        DateTimeOffset now)
    {
        var respawned = new MonsterRuntimeState(
            retired.Definition,
            retired.HomeX,
            retired.CurrentY,
            retired.HomeZ,
            retired.HomeFacing,
            retired.MaximumHealth,
            retired.MaximumHealth,
            retired.RandomState,
            checked(retired.SpawnGeneration + 1),
            retired.RuntimeInstanceId);
        respawned.NextMovementAt = now + NextIdleDelay(respawned);
        return respawned;
    }

    private static MonsterRuntimeSnapshot CreateSnapshot(MonsterRuntimeState monster)
    {
        return new MonsterRuntimeSnapshot(
            monster.Definition,
            monster.HomeX,
            monster.HomeZ,
            monster.CurrentX,
            monster.CurrentY,
            monster.CurrentZ,
            monster.Facing,
            monster.CurrentHealth,
            monster.MaximumHealth,
            monster.IsAlive,
            monster.IsSpawned,
            monster.IsMoving,
            monster.VelocityX,
            0f,
            monster.VelocityZ,
            monster.MovementTicks,
            monster.RemainingMovementTicks,
            monster.NextMovementAt,
            monster.DespawnAt,
            monster.RespawnAt,
            monster.CombatPhase,
            monster.StunnedUntil,
            monster.SpawnGeneration,
            monster.HealthRevision,
            monster.RuntimeInstanceId);
    }

    private static TimeSpan NextIdleDelay(MonsterRuntimeState monster)
    {
        var idleTicks = MinimumIdleTicks +
                        (int)(NextRandom(monster) %
                              (MaximumIdleTicks - MinimumIdleTicks + 1));
        return TimeSpan.FromSeconds(idleTicks / (double)TicksPerSecond);
    }

    private static uint CreateSeed(byte mapId, uint objectId)
    {
        var seed = unchecked((objectId * 0x9E3779B9u) ^ ((uint)mapId << 24) ^ 0xA341316Cu);
        return seed == 0 ? 0x6D2B79F5u : seed;
    }

    private static uint NextRandom(MonsterRuntimeState monster)
    {
        var value = monster.RandomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        monster.RandomState = value;
        return value;
    }

    private static double NextUnit(MonsterRuntimeState monster)
    {
        return NextRandom(monster) / (uint.MaxValue + 1d);
    }

    private static double DistanceSquared(float x1, float z1, float x2, float z2)
    {
        var deltaX = (double)x2 - x1;
        var deltaZ = (double)z2 - z1;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

}
