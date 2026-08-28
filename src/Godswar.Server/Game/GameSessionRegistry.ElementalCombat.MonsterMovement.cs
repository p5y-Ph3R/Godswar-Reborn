using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private void SyncPveMonsterElementalMovement(
        WorldInstanceRuntime runtime,
        DateTimeOffset authoritativeAt)
    {
        var nowMilliseconds =
            authoritativeAt.ToUnixTimeMilliseconds();
        foreach (var pair in _pveMonsterElementalStates)
        {
            if (pair.Key.WorldInstanceId != runtime.InstanceId ||
                pair.Value.Identity.MapId != runtime.MapId)
            {
                continue;
            }

            var baseMovementSpeed = InvokeWorldOwner(
                runtime,
                map => map.ResolveMonsterBaseMovementSpeedBasisPoints(
                    pair.Key.ObjectId,
                    pair.Value.Identity.SpawnGeneration));
            int movementSpeedBasisPoints;
            lock (pair.Value.Gate)
            {
                movementSpeedBasisPoints = checked((int)Math.Clamp(
                    pair.Value.Statuses.ApplyAdjustments(
                        nowMilliseconds,
                        movementSpeed: baseMovementSpeed,
                        physicalDefense: 0,
                        magicDefense: 0,
                        hitRating: 0,
                        healingReceived: 0).MovementSpeed,
                    1,
                    10_000));
            }

            InvokeWorldOwner(
                runtime,
                map => map.TrySetMonsterMovementSpeedBasisPoints(
                    pair.Key.ObjectId,
                    pair.Value.Identity.SpawnGeneration,
                    movementSpeedBasisPoints));
        }
    }
}
