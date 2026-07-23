using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Expires runtime statuses and composes the same complete replacement
/// snapshot used by the live registry. Remaining seconds alone do not cause a
/// change event because PlayerStatusComposer excludes them from its fingerprint.
/// </summary>
internal sealed class PlayerStatusCompositionSystem : IEcsSystem
{
    public const int SystemOrder = 300;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var entity in context.World.Query<
                     PlayerStatusSourceComponent,
                     PlayerStatusTimerComponent,
                     PlayerComposedStatusComponent>())
        {
            if (!context.World.Has<PlayerRuntimeClockComponent>(entity))
            {
                continue;
            }

            var now = context.World
                .Get<PlayerRuntimeClockComponent>(entity)
                .CurrentAt;
            ref var source = ref context.World
                .Get<PlayerStatusSourceComponent>(entity);
            var expired = source.RuntimeStatuses
                .Where(status => status.ExpiresAt <= now)
                .OrderBy(static status => status.ExpiresAt)
                .ThenBy(static status => status.StatusId)
                .ThenBy(static status => status.Kind)
                .ThenBy(static status => status.Revision)
                .ToArray();
            foreach (var status in expired)
            {
                context.Events.Publish(
                    new PlayerRuntimeStatusExpiredEvent(
                        entity,
                        status.StatusId,
                        status.Kind,
                        status.ExpiresAt,
                        status.Revision));
            }

            var activeRuntimeStatuses = source.RuntimeStatuses
                .Where(status => status.ExpiresAt > now)
                .ToImmutableArray();
            if (activeRuntimeStatuses.Length !=
                source.RuntimeStatuses.Length)
            {
                source = source.WithRuntimeStatuses(activeRuntimeStatuses);
            }

            var snapshot = PlayerStatusComposer.Compose(
                new ExperienceBoostState(source.ExperienceBoosts),
                activeRuntimeStatuses,
                now);
            ref var output = ref context.World
                .Get<PlayerComposedStatusComponent>(entity);
            var compositionChanged = !string.Equals(
                    output.Fingerprint,
                    snapshot.Fingerprint,
                    StringComparison.Ordinal);
            var effects = snapshot.Effects
                .Select(static effect =>
                    new PlayerComposedStatusEffect(
                        effect.StatusId,
                        effect.RemainingSeconds))
                .ToImmutableArray();
            output = new PlayerComposedStatusComponent(
                effects,
                snapshot.Aggregate,
                snapshot.Fingerprint);
            if (compositionChanged)
            {
                context.Events.Publish(
                    new PlayerStatusCompositionChangedEvent(
                        entity,
                        now,
                        effects,
                        snapshot.Aggregate,
                        snapshot.Fingerprint));
            }

            ref var timer = ref context.World
                .Get<PlayerStatusTimerComponent>(entity);
            timer.LastEvaluatedAt = now;
            timer.NextExpiryAt = FindNextExpiry(
                source.ExperienceBoosts,
                activeRuntimeStatuses,
                now);
            timer.Evaluations = checked(timer.Evaluations + 1);
        }
    }

    private static DateTimeOffset? FindNextExpiry(
        ImmutableArray<ActiveExperienceBoost> experienceBoosts,
        ImmutableArray<ActiveRuntimeStatus> runtimeStatuses,
        DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var boost in experienceBoosts)
        {
            if (boost.ExpiresAt is { } expiresAt && expiresAt > now)
            {
                next = next is null || expiresAt < next
                    ? expiresAt
                    : next;
            }
        }

        foreach (var status in runtimeStatuses)
        {
            if (status.ExpiresAt > now)
            {
                next = next is null || status.ExpiresAt < next
                    ? status.ExpiresAt
                    : next;
            }
        }

        return next;
    }
}
