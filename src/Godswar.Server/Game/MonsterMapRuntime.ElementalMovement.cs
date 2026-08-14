namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
{
    public bool TrySetMovementSpeedBasisPoints(
        uint objectId,
        uint expectedSpawnGeneration,
        int speedBasisPoints)
    {
        if (speedBasisPoints is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedBasisPoints));
        }

        lock (_gate)
        {
            if (!_monsters.TryGetValue(objectId, out var monster) ||
                monster.SpawnGeneration != expectedSpawnGeneration)
            {
                return false;
            }

            monster.MovementSpeedBasisPoints = speedBasisPoints;
            return true;
        }
    }

    private static TimeSpan ElementalMovementInterval(
        MonsterRuntimeState monster)
    {
        var scale = Math.Clamp(
            monster.MovementSpeedBasisPoints,
            1,
            10_000);
        return TimeSpan.FromTicks(checked(
            (TickInterval.Ticks * 10_000L) / scale));
    }
}
