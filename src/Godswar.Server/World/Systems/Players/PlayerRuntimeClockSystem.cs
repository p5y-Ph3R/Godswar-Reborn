using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Converts potentially stale wall-clock observations into a monotonic
/// simulation clock. It deliberately does not consult DateTimeOffset.UtcNow.
/// </summary>
internal sealed class PlayerRuntimeClockSystem : IEcsSystem
{
    public const int SystemOrder = 100;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var entity in context.World.Query<
                     PlayerRuntimeTimeSourceComponent,
                     PlayerRuntimeClockComponent>())
        {
            var observedAt = context.World
                .Get<PlayerRuntimeTimeSourceComponent>(entity)
                .ObservedAt;
            ref var clock = ref context.World
                .Get<PlayerRuntimeClockComponent>(entity);
            if (observedAt <= clock.CurrentAt)
            {
                continue;
            }

            var previousAt = clock.CurrentAt;
            clock.CurrentAt = observedAt;
            context.Events.Publish(
                new PlayerRuntimeClockAdvancedEvent(
                    entity,
                    previousAt,
                    observedAt));
        }
    }
}
