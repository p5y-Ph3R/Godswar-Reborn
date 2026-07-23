using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Boundaries.Combat;

internal readonly record struct PlayerCombatResourceSnapshot(
    int CurrentHp,
    int MaximumHp,
    int CurrentMp,
    int MaximumMp,
    long VitalsRevision,
    DateTimeOffset NextBasicAttackAt,
    ulong CombatRevision,
    ulong EventSequence);

internal readonly record struct PlayerCommittedProgressionSnapshot(
    int Level,
    int Experience,
    int TalentExperience,
    int TalentPoints,
    long Revision,
    ulong LastProjectionId);

internal readonly record struct PlayerCombatHydrationSnapshot(
    PlayerCombatIdentityComponent Identity,
    PlayerCombatTransformComponent Transform,
    PlayerCombatOffenseComponent Offense,
    PlayerCombatResourceSnapshot Resources,
    PlayerCommittedProgressionSnapshot Progression);

/// <summary>
/// Copies adapter-owned mutable state into scalar ECS values. This boundary is
/// deliberately not wired to a session, packet transport, or persistence
/// provider.
/// </summary>
internal static class PlayerCombatEcsBoundary
{
    public static void RegisterComponents(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RegisterComponent<PlayerCombatIdentityComponent>();
        world.RegisterComponent<PlayerCombatTransformComponent>();
        world.RegisterComponent<PlayerCombatOffenseComponent>();
        world.RegisterComponent<PlayerCombatResourceComponent>();
        world.RegisterComponent<PlayerCombatTargetComponent>();
        world.RegisterComponent<PlayerCombatIntentComponent>();
        world.RegisterComponent<PlayerCombatReservationComponent>();
        world.RegisterComponent<PlayerCombatMutationOutcomeComponent>();
        world.RegisterComponent<PlayerCombatKillLedgerComponent>();
        world.RegisterComponent<PlayerCommittedProgressionComponent>();
        world.RegisterComponent<MonsterKillProgressionProjectionComponent>();
    }

    public static EntityId HydratePlayer(
        EcsWorld world,
        in PlayerCombatHydrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidatePlayer(snapshot);
        RegisterComponents(world);

        var entity = world.CreateEntity();
        world.Add(entity, snapshot.Identity);
        world.Add(entity, snapshot.Transform);
        world.Add(entity, snapshot.Offense);
        world.Add(
            entity,
            new PlayerCombatResourceComponent(
                snapshot.Resources.CurrentHp,
                snapshot.Resources.MaximumHp,
                snapshot.Resources.CurrentMp,
                snapshot.Resources.MaximumMp,
                snapshot.Resources.VitalsRevision,
                snapshot.Resources.NextBasicAttackAt,
                snapshot.Resources.CombatRevision,
                snapshot.Resources.EventSequence));
        world.Add(
            entity,
            new PlayerCombatKillLedgerComponent(
                ImmutableArray<PlayerCombatKillGuard>.Empty));
        world.Add(
            entity,
            new PlayerCommittedProgressionComponent(
                snapshot.Progression.Level,
                snapshot.Progression.Experience,
                snapshot.Progression.TalentExperience,
                snapshot.Progression.TalentPoints,
                snapshot.Progression.Revision,
                snapshot.Progression.LastProjectionId));
        return entity;
    }

    public static EntityId HydrateTarget(
        EcsWorld world,
        in PlayerCombatTargetComponent snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateTarget(snapshot);
        RegisterComponents(world);

        var entity = world.CreateEntity();
        world.Add(entity, snapshot);
        return entity;
    }

    public static void QueueIntent(
        EcsWorld world,
        EntityId player,
        in PlayerCombatIntentComponent intent)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsurePlayer(world, player);
        if (intent.IntentId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intent),
                "A combat intent requires a non-zero ID.");
        }

        if (world.Has<PlayerCombatIntentComponent>(player))
        {
            throw new InvalidOperationException(
                "The player already has a queued combat intent.");
        }

        world.Add(player, intent);
    }

    public static void QueueMutationOutcome(
        EcsWorld world,
        EntityId player,
        in PlayerCombatMutationOutcomeComponent outcome)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsurePlayer(world, player);
        if (world.Has<PlayerCombatMutationOutcomeComponent>(player))
        {
            throw new InvalidOperationException(
                "The player already has a queued mutation outcome.");
        }

        world.Add(player, outcome);
    }

    /// <summary>
    /// Copies an already committed persistence result. No progression formula
    /// is re-run in ECS.
    /// </summary>
    public static void QueueCommittedProgression(
        EcsWorld world,
        EntityId player,
        ulong projectionId,
        in PlayerCombatKillGuard kill,
        long expectedProgressionRevision,
        CharacterProgressionResult committed)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(committed.LevelUps);
        EnsurePlayer(world, player);

        if (projectionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projectionId),
                projectionId,
                "A progression projection requires a non-zero ID.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(
            expectedProgressionRevision);
        if (world.Has<MonsterKillProgressionProjectionComponent>(player))
        {
            throw new InvalidOperationException(
                "The player already has a queued progression projection.");
        }

        var levelUps = ImmutableArray.CreateBuilder<CommittedLevelUpSnapshot>(
            committed.LevelUps.Count);
        foreach (var levelUp in committed.LevelUps)
        {
            levelUps.Add(new CommittedLevelUpSnapshot(
                levelUp.Level,
                levelUp.CurrentExperience,
                levelUp.NextLevelExperience));
        }

        var snapshot = new CommittedCharacterProgressionSnapshot(
            committed.ExperienceGained,
            committed.PreviousLevel,
            committed.CurrentLevel,
            committed.CurrentExperience,
            committed.NextLevelExperience,
            levelUps.MoveToImmutable(),
            committed.TalentExperienceGained,
            committed.CurrentTalentExperience,
            committed.TalentPointsGained,
            committed.CurrentTalentPoints);
        world.Add(
            player,
            new MonsterKillProgressionProjectionComponent(
                projectionId,
                kill.CombatIntentId,
                kill.MonsterObjectId,
                kill.MonsterSpawnGeneration,
                kill.MonsterHealthRevision,
                expectedProgressionRevision,
                snapshot));
    }

    private static void ValidatePlayer(
        in PlayerCombatHydrationSnapshot snapshot)
    {
        if (snapshot.Identity.CharacterId <= 0 ||
            snapshot.Identity.ObjectId == 0)
        {
            throw new ArgumentException(
                "Combat identity values must be positive.",
                nameof(snapshot));
        }

        if (!float.IsFinite(snapshot.Transform.X) ||
            !float.IsFinite(snapshot.Transform.Z))
        {
            throw new ArgumentException(
                "Combat coordinates must be finite.",
                nameof(snapshot));
        }

        if (snapshot.Resources.MaximumHp <= 0 ||
            snapshot.Resources.CurrentHp < 0 ||
            snapshot.Resources.CurrentHp > snapshot.Resources.MaximumHp ||
            snapshot.Resources.MaximumMp < 0 ||
            snapshot.Resources.CurrentMp < 0 ||
            snapshot.Resources.CurrentMp > snapshot.Resources.MaximumMp)
        {
            throw new ArgumentException(
                "Combat resources are outside their scalar bounds.",
                nameof(snapshot));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(
            snapshot.Resources.VitalsRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(
            snapshot.Progression.Revision);
        if (snapshot.Progression.Level <= 0 ||
            snapshot.Progression.Experience < 0 ||
            snapshot.Progression.TalentExperience < 0 ||
            snapshot.Progression.TalentPoints < 0)
        {
            throw new ArgumentException(
                "Committed progression values must be non-negative.",
                nameof(snapshot));
        }
    }

    private static void ValidateTarget(
        in PlayerCombatTargetComponent snapshot)
    {
        if (snapshot.ObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "A combat target requires a non-zero object ID.");
        }

        if (!float.IsFinite(snapshot.X) ||
            !float.IsFinite(snapshot.Z) ||
            !float.IsFinite(snapshot.BasicAttackRange) ||
            snapshot.BasicAttackRange < 0f)
        {
            throw new ArgumentException(
                "Target coordinates and range must be finite.",
                nameof(snapshot));
        }
    }

    private static void EnsurePlayer(EcsWorld world, EntityId player)
    {
        RegisterComponents(world);
        if (!world.IsAlive(player) ||
            !world.Has<PlayerCombatIdentityComponent>(player) ||
            !world.Has<PlayerCombatResourceComponent>(player))
        {
            throw new ArgumentException(
                "The entity is not a hydrated combat player.",
                nameof(player));
        }
    }
}
