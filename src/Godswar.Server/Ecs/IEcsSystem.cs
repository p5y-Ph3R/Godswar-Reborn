namespace Godswar.Server.Ecs;

/// <summary>
/// A deterministic simulation stage. Equal order values retain registration
/// order.
/// </summary>
internal interface IEcsSystem
{
    int Order { get; }

    void Update(EcsSystemContext context);
}

internal readonly struct EcsSystemContext
{
    internal EcsSystemContext(
        EcsWorld world,
        ulong tick,
        TimeSpan deltaTime,
        EcsCommandBuffer commands,
        EcsEventBuffer events)
    {
        World = world;
        Tick = tick;
        DeltaTime = deltaTime;
        Commands = commands;
        Events = events;
    }

    public EcsWorld World { get; }

    public ulong Tick { get; }

    public TimeSpan DeltaTime { get; }

    public EcsCommandBuffer Commands { get; }

    public EcsEventBuffer Events { get; }
}
