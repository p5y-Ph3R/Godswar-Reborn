namespace Godswar.Server.World.Components.Monsters;

internal static class MonsterEcsRandom
{
    public static uint CreateSeed(byte mapId, uint objectId)
    {
        var seed = unchecked(
            (objectId * 0x9E3779B9u) ^
            ((uint)mapId << 24) ^
            0xA341316Cu);
        return seed == 0 ? 0x6D2B79F5u : seed;
    }

    public static uint Next(ref MonsterRandomComponent random)
    {
        var value = random.State;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        random.State = value;
        return value;
    }

    public static double NextUnit(ref MonsterRandomComponent random) =>
        Next(ref random) / (uint.MaxValue + 1d);

    public static TimeSpan NextIdleDelay(ref MonsterRandomComponent random)
    {
        var idleTicks = MonsterEcsRules.MinimumIdleTicks +
            (int)(Next(ref random) %
                (MonsterEcsRules.MaximumIdleTicks -
                 MonsterEcsRules.MinimumIdleTicks + 1));
        return TimeSpan.FromSeconds(
            idleTicks / (double)MonsterEcsRules.TicksPerSecond);
    }
}

internal static class MonsterEcsRules
{
    public const int TicksPerSecond = 12;
    public const float MovementStep = 0.38f;
    public const float MaximumRoamRadius = 8f;
    public const float CombatLeashRadius = 32f;
    public const int MinimumMovementTicks = 1;
    public const int MaximumMovementTicks = 21;
    public const int MinimumIdleTicks = 15 * TicksPerSecond;
    public const int MaximumIdleTicks = 20 * TicksPerSecond;
    public const float CombatRange = 3f;
    public const int AttackCooldownTicks = 21;

    public static readonly TimeSpan TickInterval =
        TimeSpan.FromSeconds(1d / TicksPerSecond);

    public static readonly TimeSpan AttackCooldown =
        TimeSpan.FromTicks(TickInterval.Ticks * AttackCooldownTicks);
}
