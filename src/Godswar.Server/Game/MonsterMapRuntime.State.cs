using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
{
    private sealed class MonsterRuntimeState
    {
        public MonsterRuntimeState(
            CapturedMonsterSpawn definition,
            float homeX,
            float currentY,
            float homeZ,
            float homeFacing,
            uint currentHealth,
            uint maximumHealth,
            uint randomState,
            uint spawnGeneration,
            Guid runtimeInstanceId)
        {
            Definition = definition;
            HomeX = homeX;
            CurrentX = homeX;
            CurrentY = currentY;
            HomeZ = homeZ;
            CurrentZ = homeZ;
            HomeFacing = homeFacing;
            Facing = homeFacing;
            CurrentHealth = currentHealth;
            MaximumHealth = maximumHealth;
            RandomState = randomState;
            SpawnGeneration = spawnGeneration;
            RuntimeInstanceId = runtimeInstanceId;
        }

        public CapturedMonsterSpawn Definition { get; }
        public float HomeX { get; }
        public float CurrentY { get; }
        public float HomeZ { get; }
        public float HomeFacing { get; }
        public float CurrentX { get; set; }
        public float CurrentZ { get; set; }
        public float Facing { get; set; }
        public uint CurrentHealth { get; set; }
        public uint MaximumHealth { get; }
        public bool IsAlive { get; set; } = true;
        public bool IsSpawned { get; set; } = true;
        public bool IsMoving { get; set; }
        public float VelocityX { get; set; }
        public float VelocityZ { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }
        public uint MovementTicks { get; set; }
        public uint RemainingMovementTicks { get; set; }
        public DateTimeOffset NextMovementAt { get; set; }
        public DateTimeOffset NextMovementStepAt { get; set; }
        public DateTimeOffset? DespawnAt { get; set; }
        public DateTimeOffset? RespawnAt { get; set; }
        public uint RandomState { get; set; }
        public uint SpawnGeneration { get; }
        public Guid RuntimeInstanceId { get; }
        public ulong HealthRevision { get; set; }
        public int? AggroCharacterId { get; set; }
        public MonsterCombatPhase CombatPhase { get; set; }
        public bool HasSentInitialChase { get; set; }
        public DateTimeOffset NextAttackAt { get; set; }
        public DateTimeOffset? StunnedUntil { get; set; }
        public int MovementSpeedBasisPoints { get; set; } = 10_000;
    }
}
