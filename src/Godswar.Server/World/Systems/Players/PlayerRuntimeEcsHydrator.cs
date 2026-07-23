using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

internal readonly record struct PlayerRuntimeEcsSeed(
    DateTimeOffset ObservedAt,
    ImmutableArray<ActiveExperienceBoost> ExperienceBoosts,
    ImmutableArray<ActiveRuntimeStatus> RuntimeStatuses,
    DateTimeOffset? ProgressionOnlineStartedAt,
    DateTimeOffset? ZodiacOnlineStartedAt);

/// <summary>
/// Attaches the shadow runtime policies to an already hydrated player entity.
/// This boundary reads only typed ECS snapshots; no mutable GameCharacter
/// reference enters a runtime component.
/// </summary>
internal static class PlayerRuntimeEcsHydrator
{
    public static void RegisterComponents(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RegisterComponent<PlayerRuntimeTimeSourceComponent>();
        world.RegisterComponent<PlayerRuntimeClockComponent>();
        world.RegisterComponent<PlayerRecoverySourceComponent>();
        world.RegisterComponent<PlayerRecoveryTimerComponent>();
        world.RegisterComponent<PlayerStatusSourceComponent>();
        world.RegisterComponent<PlayerStatusTimerComponent>();
        world.RegisterComponent<PlayerComposedStatusComponent>();
        world.RegisterComponent<PlayerOnlineDurationClocksComponent>();
    }

    public static void Attach(
        EcsWorld world,
        EntityId entity,
        PlayerRuntimeEcsSeed seed)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.IsAlive(entity))
        {
            throw new ArgumentException(
                "The player runtime seed requires a live ECS entity.",
                nameof(entity));
        }

        EnsureBaseComponents(world, entity);
        ValidateRuntimeStatuses(seed.RuntimeStatuses);

        var progression = world.Get<PlayerProgressionComponent>(entity);
        var playerClass = world.Get<PlayerClassComponent>(entity);
        var calculated = world
            .Get<PlayerCalculatedStatsComponent>(entity);
        var recovery = PlayerRecoverySourceComponent.Create(
            progression.Level,
            playerClass.Profession,
            calculated.HasValue ? calculated.HpRecovery : 0,
            calculated.HasValue ? calculated.MpRecovery : 0);

        RegisterComponents(world);
        world.Set(
            entity,
            new PlayerRuntimeTimeSourceComponent(seed.ObservedAt));
        world.Set(
            entity,
            new PlayerRuntimeClockComponent(seed.ObservedAt));
        world.Set(entity, recovery);
        world.Set(
            entity,
            new PlayerRecoveryTimerComponent(
                seed.ObservedAt +
                PlayerRecoverySimulationSystem.RecoveryInterval));
        world.Set(
            entity,
            new PlayerStatusSourceComponent(
                seed.ExperienceBoosts,
                seed.RuntimeStatuses));
        world.Set(
            entity,
            new PlayerStatusTimerComponent(seed.ObservedAt));
        world.Set(
            entity,
            new PlayerComposedStatusComponent(
                ImmutableArray<PlayerComposedStatusEffect>.Empty,
                ClientStatusAggregate.Empty,
                string.Empty));
        world.Set(
            entity,
            new PlayerOnlineDurationClocksComponent(
                seed.ProgressionOnlineStartedAt,
                seed.ZodiacOnlineStartedAt));
    }

    private static void EnsureBaseComponents(
        EcsWorld world,
        EntityId entity)
    {
        if (!world.Has<PlayerIdentityComponent>(entity) ||
            !world.Has<PlayerClassComponent>(entity) ||
            !world.Has<PlayerVitalsComponent>(entity) ||
            !world.Has<PlayerProgressionComponent>(entity) ||
            !world.Has<PlayerCalculatedStatsComponent>(entity))
        {
            throw new ArgumentException(
                "The player runtime seed requires a fully hydrated player entity.",
                nameof(entity));
        }
    }

    private static void ValidateRuntimeStatuses(
        ImmutableArray<ActiveRuntimeStatus> statuses)
    {
        if (statuses.IsDefaultOrEmpty)
        {
            return;
        }

        var duplicateKind = statuses
            .GroupBy(static status => status.Kind)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateKind is not null)
        {
            throw new ArgumentException(
                $"Runtime status kind {duplicateKind.Key} appears more than once.",
                nameof(statuses));
        }
    }
}
