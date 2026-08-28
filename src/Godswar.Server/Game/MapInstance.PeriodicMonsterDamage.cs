namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal bool TrySetMonsterMovementSpeedBasisPoints(
        uint objectId,
        uint expectedSpawnGeneration,
        int speedBasisPoints)
    {
        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime is not null &&
                _monsterRuntime.TrySetMovementSpeedBasisPoints(
                    objectId,
                    expectedSpawnGeneration,
                    speedBasisPoints);
        }
    }

    internal bool TryApplyMonsterPeriodicDamageGuarded(
        uint objectId,
        uint damage,
        int sourceCharacterId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        lock (_medusaOwnershipGate)
        {
            // Periodic elemental damage currently carries no authoritative
            // physical/magical channel. It cannot bypass a bound Medusa boss
            // reduction or produce an unclaimed roster defeat.
            if (_medusaInstanceOwner is not null)
            {
                result = default!;
                return false;
            }

            lock (_monsterRuntimeGate)
            {
                if (_monsterRuntime is not null &&
                    _monsterRuntime.TryGetSnapshot(
                        objectId,
                        out var snapshot) &&
                    snapshot.SpawnGeneration == expectedSpawnGeneration &&
                    snapshot.HealthRevision == expectedHealthRevision &&
                    _monsterRuntime.TryApplyPeriodicDamage(
                        objectId,
                        damage,
                        sourceCharacterId,
                        expectedSpawnGeneration,
                        now,
                        out result))
                {
                    return true;
                }

                result = default!;
                return false;
            }
        }
    }
}
