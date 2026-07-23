using System.Threading;

namespace Godswar.Server.Ecs;

internal static class EcsTypeIds
{
    private static int _nextComponentId;
    private static int _nextEventId;

    public static int AllocateComponent() =>
        Interlocked.Increment(ref _nextComponentId) - 1;

    public static int AllocateEvent() =>
        Interlocked.Increment(ref _nextEventId) - 1;
}

internal static class EcsComponentType<T>
    where T : struct
{
    public static readonly int Id = EcsTypeIds.AllocateComponent();
}

internal static class EcsEventType<T>
    where T : struct
{
    public static readonly int Id = EcsTypeIds.AllocateEvent();
}
