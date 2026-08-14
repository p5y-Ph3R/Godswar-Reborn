using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Players;

/// <summary>
/// Copies the mutable persistence/session projection into typed ECS values.
/// No live character object, socket, store, or packet buffer crosses this
/// boundary.
/// </summary>
internal static class GameCharacterEcsHydrator
{
    public static void RegisterComponents(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RegisterComponent<PlayerIdentityComponent>();
        world.RegisterComponent<PlayerClassComponent>();
        world.RegisterComponent<PlayerCampComponent>();
        world.RegisterComponent<PlayerTransformComponent>();
        world.RegisterComponent<PlayerVitalsComponent>();
        world.RegisterComponent<PlayerProgressionComponent>();
        world.RegisterComponent<PlayerWalletComponent>();
        world.RegisterComponent<PlayerEquipmentAppearanceComponent>();
        world.RegisterComponent<PlayerZodiacComponent>();
        world.RegisterComponent<PlayerCalculatedStatsComponent>();
        world.RegisterComponent<PlayerStatusEffectsComponent>();
    }

    public static EntityId Hydrate(
        EcsWorld world,
        GameCharacter character,
        uint objectId,
        long worldRevision,
        PlayerStatusSnapshot status,
        PlayerTransformOverride? transformOverride = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(status);

        if (objectId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectId),
                objectId,
                "A player ECS entity requires a non-zero world object ID.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(worldRevision);
        var mapId =
            transformOverride?.MapId ?? character.CurrentMap;
        var positionX =
            transformOverride?.X ?? character.PositionX;
        var positionZ =
            transformOverride?.Z ?? character.PositionZ;
        if (!float.IsFinite(positionX) ||
            !float.IsFinite(positionZ))
        {
            throw new ArgumentException(
                "A player ECS entity requires finite world coordinates.",
                nameof(character));
        }

        ArgumentNullException.ThrowIfNull(status.Effects);
        ArgumentNullException.ThrowIfNull(status.Fingerprint);
        if (!float.IsFinite(status.Aggregate.ExperienceBonus) ||
            !float.IsFinite(status.Aggregate.MovementSpeedMultiplier) ||
            status.Aggregate.MovementSpeedMultiplier <= 0f)
        {
            throw new ArgumentException(
                "The player status aggregate contains invalid protocol values.",
                nameof(status));
        }

        var calculatedStats = character.CalculatedStats ??
            CharacterStats.FromCharacter(character);
        var hasCalculatedStats = character.CalculatedStats is not null;
        var effects = ImmutableArray.CreateRange(status.Effects);

        RegisterComponents(world);
        var entity = world.CreateEntity();
        world.Add(
            entity,
            new PlayerIdentityComponent(
                character.Id,
                character.AccountId,
                objectId,
                character.Name,
                character.CreatedUtc,
                worldRevision));
        world.Add(
            entity,
            new PlayerClassComponent(
                character.Gender,
                character.Profession,
                character.Hair,
                character.Face,
                character.Faith));
        world.Add(entity, new PlayerCampComponent(character.Camp));
        world.Add(
            entity,
            new PlayerTransformComponent(
                mapId,
                positionX,
                positionZ));
        world.Add(
            entity,
            new PlayerVitalsComponent(
                character.CurrentHp,
                character.MaxHp,
                character.CurrentMp,
                character.MaxMp,
                character.VitalsRevision));
        world.Add(
            entity,
            new PlayerProgressionComponent(
                character.Level,
                character.Experience,
                character.FighterLevelSealed,
                character.TalentPoints,
                character.TalentExperience,
                character.HolySuitPoints));
        world.Add(
            entity,
            new PlayerWalletComponent(character.Silver, character.Gold));
        world.Add(
            entity,
            new PlayerEquipmentAppearanceComponent(
                character.Equipment,
                character.WeaponRank,
                character.WeaponAuraEffect,
                character.ArmorRank,
                character.ArmorAuraEffect));
        world.Add(entity, CreateZodiac(character));
        world.Add(
            entity,
            CreateCalculatedStats(hasCalculatedStats, calculatedStats));
        world.Add(
            entity,
            new PlayerStatusEffectsComponent(
                effects,
                status.Aggregate,
                status.Fingerprint));
        return entity;
    }

    private static PlayerZodiacComponent CreateZodiac(GameCharacter character) =>
        new(
            character.ZodiacType,
            character.ZodiacLuckyStatus,
            character.ZodiacLuckyExpiresAt,
            character.ZodiacLevel,
            character.ZodiacEnergy,
            character.ZodiacEnergyRemainderX100,
            character.ZodiacOnlineDay,
            character.ZodiacOnlineDurationTicksToday,
            character.ZodiacLastOnlineAt,
            character.ZodiacLastCompensationDay,
            character.ZodiacAccumulatedExperienceX100,
            character.ZodiacAccumulatedTalentExperienceX100);

    private static PlayerCalculatedStatsComponent CreateCalculatedStats(
        bool hasValue,
        CharacterStats stats) =>
        new(
            hasValue,
            stats.CharacterId,
            stats.AccountId,
            stats.Name,
            stats.Level,
            stats.MaxHp,
            stats.MaxMp,
            stats.CurrentHp,
            stats.CurrentMp,
            stats.PhysicalAttack,
            stats.PhysicalDefense,
            stats.MagicAttack,
            stats.MagicDefense,
            stats.Hit,
            stats.Dodge,
            stats.Critical,
            stats.CriticalResistance,
            stats.DamageAbsorb,
            stats.PhysicalDamageBonus,
            stats.MagicDamageBonus,
            stats.CureBonus,
            stats.BeCureBonus,
            stats.HpRecovery,
            stats.MpRecovery,
            stats.IgnorePhysicalDefense,
            stats.IgnoreMagicDefense,
            stats.PhysicalAppendDamage,
            stats.MagicAppendDamage,
            stats.CriticalDamagePercent,
            stats.CriticalDamageFlat,
            stats.WeaponScore,
            stats.WeaponRank,
            stats.WeaponAuraEffect,
            stats.ArmorScore,
            stats.ArmorRank,
            stats.ArmorAuraEffect,
            stats.LearnedSkillCount,
            stats.PhysicalDamageReduction,
            stats.MagicDamageReduction,
            stats.CriticalDamageReduction,
            stats.LifeAbsorption,
            stats.DamageRebound);
}

internal readonly record struct PlayerTransformOverride(
    byte MapId,
    float X,
    float Z);
