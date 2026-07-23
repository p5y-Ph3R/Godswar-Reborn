using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Applies the modern six-second passive recovery pulse. A delayed simulation
/// performs one pulse and reschedules from the observation time, matching the
/// live registry instead of replaying an unbounded backlog.
/// </summary>
internal sealed class PlayerRecoverySimulationSystem : IEcsSystem
{
    public static readonly TimeSpan RecoveryInterval =
        TimeSpan.FromSeconds(6);

    public const int SystemOrder = 200;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var entity in context.World.Query<
                     PlayerVitalsComponent,
                     PlayerRecoverySourceComponent,
                     PlayerRecoveryTimerComponent>())
        {
            if (!context.World.Has<PlayerRuntimeClockComponent>(entity))
            {
                continue;
            }

            var now = context.World
                .Get<PlayerRuntimeClockComponent>(entity)
                .CurrentAt;
            ref var timer = ref context.World
                .Get<PlayerRecoveryTimerComponent>(entity);
            if (now < timer.NextPulseAt)
            {
                continue;
            }

            timer.NextPulseAt = now + RecoveryInterval;
            timer.PulsesObserved = checked(timer.PulsesObserved + 1);

            ref var vitals = ref context.World
                .Get<PlayerVitalsComponent>(entity);
            if (vitals.CurrentHp <= 0)
            {
                continue;
            }

            var recovery = context.World
                .Get<PlayerRecoverySourceComponent>(entity);
            var maximumHp = Math.Max(1, vitals.MaximumHp);
            var maximumMp = Math.Max(0, vitals.MaximumMp);
            var nextHp = (int)Math.Min(
                maximumHp,
                (long)vitals.CurrentHp + recovery.HpPerPulse);
            var nextMp = (int)Math.Min(
                maximumMp,
                (long)vitals.CurrentMp + recovery.MpPerPulse);
            if (nextHp == vitals.CurrentHp &&
                nextMp == vitals.CurrentMp)
            {
                continue;
            }

            var previousHp = vitals.CurrentHp;
            var previousMp = vitals.CurrentMp;
            vitals.CurrentHp = nextHp;
            vitals.CurrentMp = nextMp;
            vitals.Revision = checked(vitals.Revision + 1);
            context.Events.Publish(
                new PlayerVitalsRecoveredEvent(
                    entity,
                    now,
                    previousHp,
                    nextHp,
                    previousMp,
                    nextMp,
                    vitals.Revision));
        }
    }
}
