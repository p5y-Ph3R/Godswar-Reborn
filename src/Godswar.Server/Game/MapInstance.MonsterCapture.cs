namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal bool TryCaptureMonster(
        MonsterRuntimeSnapshot expected,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null)
            {
                result = default!;
                return false;
            }

            lock (_monsterRuntimeGate)
            {
                if (_monsterRuntime is null ||
                    !_monsterRuntime.TryGetSnapshot(
                        expected.ObjectId,
                        out var current) ||
                    current.RuntimeInstanceId !=
                        expected.RuntimeInstanceId ||
                    current.SpawnGeneration !=
                        expected.SpawnGeneration ||
                    current.HealthRevision != expected.HealthRevision ||
                    !current.IsAlive ||
                    !current.IsSpawned ||
                    current.CurrentHealth == 0 ||
                    !_monsterRuntime.TryApplyDamage(
                        current.ObjectId,
                        current.CurrentHealth,
                        attackerCharacterId: null,
                        current.SpawnGeneration,
                        now,
                        out result) ||
                    !result.Killed)
                {
                    result = default!;
                    return false;
                }

                return true;
            }
        }
    }
}
