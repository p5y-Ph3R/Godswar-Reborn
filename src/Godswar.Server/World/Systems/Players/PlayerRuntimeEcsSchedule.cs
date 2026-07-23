using Godswar.Server.Ecs;

namespace Godswar.Server.World.Systems.Players;

internal static class PlayerRuntimeEcsSchedule
{
    public static EcsSystemScheduler Create(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new PlayerRuntimeClockSystem());
        scheduler.AddSystem(new PlayerRecoverySimulationSystem());
        scheduler.AddSystem(new PlayerStatusCompositionSystem());
        scheduler.AddSystem(new PlayerOnlineDurationSystem());
        return scheduler;
    }
}
