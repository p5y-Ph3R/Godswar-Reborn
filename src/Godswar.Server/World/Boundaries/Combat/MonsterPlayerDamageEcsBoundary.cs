using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Boundaries.Combat;

/// <summary>
/// Scalar boundary for monster-to-player damage. No session, packet, store, or
/// live character reference crosses into the ECS world.
/// </summary>
internal static class MonsterPlayerDamageEcsBoundary
{
    public static MonsterPlayerDamageEntity HydratePlayer(
        EcsWorld world,
        in MonsterPlayerDamageHydrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateHydration(snapshot);
        RegisterComponents(world);

        var entity = world.CreateEntity();
        world.Add(
            entity,
            new PlayerIdentityComponent(
                snapshot.CharacterId,
                snapshot.AccountId,
                snapshot.PlayerObjectId,
                Name: string.Empty,
                CreatedUtc: DateTime.UnixEpoch,
                WorldRevision: 0));
        world.Add(
            entity,
            new PlayerVitalsComponent(
                snapshot.CurrentHp,
                snapshot.MaximumHp,
                snapshot.CurrentMp,
                snapshot.MaximumMp,
                snapshot.VitalsRevision));
        world.Add(
            entity,
            new MonsterPlayerDamageStateComponent(
                snapshot.LifeRevision,
                0,
                0));
        return new MonsterPlayerDamageEntity(entity);
    }

    public static void QueueDamage(
        EcsWorld world,
        in MonsterPlayerDamageEntity player,
        in MonsterPlayerDamageIntentComponent intent)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsurePlayer(world, player);
        if (intent.MonsterObjectId == 0 ||
            intent.ExpectedCharacterId <= 0 ||
            intent.ExpectedPlayerObjectId == 0)
        {
            throw new ArgumentException(
                "A monster-player damage intent requires valid identities.",
                nameof(intent));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(
            intent.ExpectedLifeRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(
            intent.ExpectedVitalsRevision);
        if (world.Has<MonsterPlayerDamageIntentComponent>(
                player.Entity))
        {
            throw new InvalidOperationException(
                "The player already has a queued monster damage intent.");
        }

        world.Add(player.Entity, intent);
    }

    public static void SynchronizePlayer(
        EcsWorld world,
        in MonsterPlayerDamageEntity player,
        in MonsterPlayerDamageHydrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateHydration(snapshot);
        EnsurePlayer(world, player);

        world.Set(
            player.Entity,
            new PlayerIdentityComponent(
                snapshot.CharacterId,
                snapshot.AccountId,
                snapshot.PlayerObjectId,
                Name: string.Empty,
                CreatedUtc: DateTime.UnixEpoch,
                WorldRevision: 0));
        world.Set(
            player.Entity,
            new PlayerVitalsComponent(
                snapshot.CurrentHp,
                snapshot.MaximumHp,
                snapshot.CurrentMp,
                snapshot.MaximumMp,
                snapshot.VitalsRevision));
        ref var state = ref world.Get<
            MonsterPlayerDamageStateComponent>(player.Entity);
        state.LifeRevision = snapshot.LifeRevision;
    }

    public static void SynchronizePetHealingTalent(
        EcsWorld world,
        in MonsterPlayerDamageEntity player,
        PetHealingTalentHydrationSnapshot? activePet)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsurePlayer(world, player);
        if (activePet is not { } pet)
        {
            world.Remove<ActivePetHealingTalentComponent>(
                player.Entity);
            return;
        }

        ValidateActivePet(pet);
        world.Set(
            player.Entity,
            new ActivePetHealingTalentComponent(
                pet.PetId,
                pet.Level,
                pet.Aptitude,
                pet.TalentMask,
                pet.IsCarried,
                pet.IsSummoned));
    }

    private static void RegisterComponents(EcsWorld world)
    {
        world.RegisterComponent<PlayerIdentityComponent>();
        world.RegisterComponent<PlayerVitalsComponent>();
        world.RegisterComponent<MonsterPlayerDamageStateComponent>();
        world.RegisterComponent<MonsterPlayerDamageIntentComponent>();
        world.RegisterComponent<ActivePetHealingTalentComponent>();
    }

    private static void EnsurePlayer(
        EcsWorld world,
        in MonsterPlayerDamageEntity player)
    {
        RegisterComponents(world);
        if (!world.IsAlive(player.Entity) ||
            !world.Has<PlayerIdentityComponent>(player.Entity) ||
            !world.Has<PlayerVitalsComponent>(player.Entity) ||
            !world.Has<MonsterPlayerDamageStateComponent>(
                player.Entity))
        {
            throw new ArgumentException(
                "The entity is not a hydrated damage target.",
                nameof(player));
        }
    }

    private static void ValidateHydration(
        in MonsterPlayerDamageHydrationSnapshot snapshot)
    {
        if (snapshot.CharacterId <= 0 ||
            snapshot.AccountId <= 0 ||
            snapshot.PlayerObjectId == 0)
        {
            throw new ArgumentException(
                "Player damage identity values must be positive.",
                nameof(snapshot));
        }

        if (snapshot.MaximumHp <= 0 ||
            snapshot.CurrentHp < 0 ||
            snapshot.CurrentHp > snapshot.MaximumHp ||
            snapshot.MaximumMp < 0 ||
            snapshot.CurrentMp < 0 ||
            snapshot.CurrentMp > snapshot.MaximumMp)
        {
            throw new ArgumentException(
                "Player damage vitals are outside their scalar bounds.",
                nameof(snapshot));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(
            snapshot.VitalsRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(
            snapshot.LifeRevision);
    }

    private static void ValidateActivePet(
        in PetHealingTalentHydrationSnapshot pet)
    {
        if (pet.PetId <= 0 ||
            pet.PetId > uint.MaxValue ||
            pet.Level <= 0 ||
            pet.Aptitude is < 1 or > 16 ||
            pet.TalentMask is < 0 or > 31 ||
            !pet.IsCarried ||
            !pet.IsSummoned)
        {
            throw new ArgumentException(
                "An active Healing pet must be a bounded, carried and " +
                "summoned pet projection.",
                nameof(pet));
        }
    }
}
