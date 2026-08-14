using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Monsters;

internal readonly record struct MonsterIdentityComponent(
    CapturedMonsterSpawn Definition,
    Guid RuntimeInstanceId);

internal struct MonsterTransformComponent
{
    public MonsterTransformComponent(
        float homeX,
        float currentY,
        float homeZ,
        float homeFacing)
    {
        HomeX = homeX;
        HomeZ = homeZ;
        HomeFacing = homeFacing;
        X = homeX;
        Y = currentY;
        Z = homeZ;
        Facing = homeFacing;
    }

    public float HomeX;
    public float HomeZ;
    public float HomeFacing;
    public float X;
    public float Y;
    public float Z;
    public float Facing;
}

internal struct MonsterVitalsComponent
{
    public MonsterVitalsComponent(
        uint currentHealth,
        uint maximumHealth,
        uint spawnGeneration)
    {
        CurrentHealth = currentHealth;
        MaximumHealth = maximumHealth;
        IsAlive = true;
        IsSpawned = true;
        SpawnGeneration = spawnGeneration;
    }

    public uint CurrentHealth;
    public uint MaximumHealth;
    public bool IsAlive;
    public bool IsSpawned;
    public uint SpawnGeneration;
    public ulong HealthRevision;
}

internal struct MonsterMovementComponent
{
    public bool IsMoving;
    public float VelocityX;
    public float VelocityZ;
    public float TargetX;
    public float TargetZ;
    public uint MovementTicks;
    public uint RemainingMovementTicks;
    public DateTimeOffset NextMovementAt;
    public DateTimeOffset NextMovementStepAt;
    public int MovementSpeedBasisPoints;
}

internal struct MonsterCombatComponent
{
    public int? AggroCharacterId;
    public MonsterCombatPhase Phase;
    public bool HasSentInitialChase;
    public DateTimeOffset NextAttackAt;
    public DateTimeOffset? StunnedUntil;
}

internal struct MonsterLifecycleComponent
{
    public MonsterLifecycleComponent(
        TimeSpan corpseDespawnDelay,
        TimeSpan respawnDelay)
    {
        CorpseDespawnDelay = corpseDespawnDelay;
        RespawnDelay = respawnDelay;
    }

    public TimeSpan CorpseDespawnDelay;
    public TimeSpan RespawnDelay;
    public DateTimeOffset? DespawnAt;
    public DateTimeOffset? RespawnAt;
}

internal struct MonsterRandomComponent(uint state)
{
    public uint State = state;
}
