namespace Godswar.Server.Game;

internal readonly record struct SimulationLoopRuntimeSnapshot(
    SimulationLoopKind Kind,
    long ActiveLoops,
    TimeSpan HeartbeatAge,
    TimeSpan ExpectedPeriod);
