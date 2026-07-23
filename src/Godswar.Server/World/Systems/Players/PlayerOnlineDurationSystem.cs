using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Produces persistence-neutral online intervals. Store adapters may consume
/// the progression stream to decrement EXP/Talent boosts and the Zodiac stream
/// to apply its own daily cadence and compensation policy.
/// </summary>
internal sealed class PlayerOnlineDurationSystem : IEcsSystem
{
    public const int SystemOrder = 400;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var entity in context.World.Query<
                     PlayerIdentityComponent,
                     PlayerRuntimeClockComponent,
                     PlayerOnlineDurationClocksComponent>())
        {
            var identity = context.World
                .Get<PlayerIdentityComponent>(entity);
            var now = context.World
                .Get<PlayerRuntimeClockComponent>(entity)
                .CurrentAt;
            ref var clocks = ref context.World
                .Get<PlayerOnlineDurationClocksComponent>(entity);

            Account(
                context,
                entity,
                identity,
                PlayerOnlineDurationTarget.ProgressionBoosts,
                now,
                ref clocks.ProgressionLastAccountedAt,
                ref clocks.ProgressionElapsedTicks);
            Account(
                context,
                entity,
                identity,
                PlayerOnlineDurationTarget.Zodiac,
                now,
                ref clocks.ZodiacLastAccountedAt,
                ref clocks.ZodiacElapsedTicks);
        }
    }

    private static void Account(
        EcsSystemContext context,
        EntityId entity,
        PlayerIdentityComponent identity,
        PlayerOnlineDurationTarget target,
        DateTimeOffset now,
        ref DateTimeOffset? lastAccountedAt,
        ref long totalElapsedTicks)
    {
        if (lastAccountedAt is not { } previousAt ||
            now <= previousAt)
        {
            return;
        }

        var elapsedTicks = (now - previousAt).Ticks;
        if (elapsedTicks <= 0)
        {
            return;
        }

        totalElapsedTicks = checked(totalElapsedTicks + elapsedTicks);
        lastAccountedAt = now;
        context.Events.Publish(
            new PlayerOnlineDurationAccountedEvent(
                entity,
                identity.AccountId,
                identity.CharacterId,
                target,
                previousAt,
                now,
                elapsedTicks,
                totalElapsedTicks));
    }
}
