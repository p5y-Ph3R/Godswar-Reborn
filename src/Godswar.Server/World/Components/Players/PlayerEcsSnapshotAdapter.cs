using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Players;

internal sealed record PlayerEcsSnapshot(
    PlayerIdentityComponent Identity,
    PlayerClassComponent Class,
    PlayerCampComponent Camp,
    PlayerTransformComponent Transform,
    PlayerVitalsComponent Vitals,
    PlayerProgressionComponent Progression,
    PlayerWalletComponent Wallet,
    PlayerEquipmentAppearanceComponent EquipmentAppearance,
    PlayerZodiacComponent Zodiac,
    PlayerCalculatedStatsComponent CalculatedStats,
    ImmutableArray<ClientStatusEffect> StatusEffects,
    ClientStatusAggregate StatusAggregate,
    string StatusFingerprint);

/// <summary>
/// Reads an immutable player snapshot after a simulation tick. ToProtocolCharacter
/// is a temporary parity bridge for existing packet builders; it does not carry
/// kit-bag state and must not be persisted.
/// </summary>
internal static class PlayerEcsSnapshotAdapter
{
    public static PlayerEcsSnapshot Capture(
        EcsWorld world,
        EntityId entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        var status = world.Get<PlayerStatusEffectsComponent>(entity);
        return new PlayerEcsSnapshot(
            world.Get<PlayerIdentityComponent>(entity),
            world.Get<PlayerClassComponent>(entity),
            world.Get<PlayerCampComponent>(entity),
            world.Get<PlayerTransformComponent>(entity),
            world.Get<PlayerVitalsComponent>(entity),
            world.Get<PlayerProgressionComponent>(entity),
            world.Get<PlayerWalletComponent>(entity),
            world.Get<PlayerEquipmentAppearanceComponent>(entity),
            world.Get<PlayerZodiacComponent>(entity),
            world.Get<PlayerCalculatedStatsComponent>(entity),
            status.Effects,
            status.Aggregate,
            status.Fingerprint);
    }

    public static GameCharacter ToProtocolCharacter(PlayerEcsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var identity = snapshot.Identity;
        var playerClass = snapshot.Class;
        var transform = snapshot.Transform;
        var vitals = snapshot.Vitals;
        var progression = snapshot.Progression;
        var equipment = snapshot.EquipmentAppearance;
        var zodiac = snapshot.Zodiac;

        var character = new GameCharacter
        {
            Id = identity.CharacterId,
            AccountId = identity.AccountId,
            Name = identity.Name,
            CreatedUtc = identity.CreatedUtc,
            Gender = playerClass.Gender,
            Profession = playerClass.Profession,
            Hair = playerClass.Hair,
            Face = playerClass.Face,
            Faith = playerClass.Faith,
            Camp = snapshot.Camp.Camp,
            CurrentMap = transform.MapId,
            PositionX = transform.X,
            PositionZ = transform.Z,
            CurrentHp = vitals.CurrentHp,
            MaxHp = vitals.MaximumHp,
            CurrentMp = vitals.CurrentMp,
            MaxMp = vitals.MaximumMp,
            VitalsRevision = vitals.Revision,
            Level = progression.Level,
            Experience = progression.Experience,
            FighterLevelSealed = progression.FighterLevelSealed,
            TalentPoints = progression.TalentPoints,
            TalentExperience = progression.TalentExperience,
            HolySuitPoints = progression.HolySuitPoints,
            Silver = snapshot.Wallet.Silver,
            Gold = snapshot.Wallet.Gold,
            Equipment = equipment.Equipment,
            WeaponRank = equipment.WeaponRank,
            WeaponAuraEffect = equipment.WeaponAuraEffect,
            ArmorRank = equipment.ArmorRank,
            ArmorAuraEffect = equipment.ArmorAuraEffect,
            ZodiacType = zodiac.Type,
            ZodiacLuckyStatus = zodiac.LuckyStatus,
            ZodiacLuckyExpiresAt = zodiac.LuckyExpiresAt,
            ZodiacLevel = zodiac.Level,
            ZodiacEnergy = zodiac.Energy,
            ZodiacEnergyRemainderX100 = zodiac.EnergyRemainderX100,
            ZodiacOnlineDay = zodiac.OnlineDay,
            ZodiacOnlineDurationTicksToday = zodiac.OnlineDurationTicksToday,
            ZodiacLastOnlineAt = zodiac.LastOnlineAt,
            ZodiacLastCompensationDay = zodiac.LastCompensationDay,
            ZodiacAccumulatedExperienceX100 =
                zodiac.AccumulatedExperienceX100,
            ZodiacAccumulatedTalentExperienceX100 =
                zodiac.AccumulatedTalentExperienceX100
        };

        if (snapshot.CalculatedStats.HasValue)
        {
            character.CalculatedStats =
                ToCharacterStats(snapshot.CalculatedStats);
        }

        return character;
    }

    private static CharacterStats ToCharacterStats(
        PlayerCalculatedStatsComponent stats) =>
        new()
        {
            CharacterId = stats.CharacterId,
            AccountId = stats.AccountId,
            Name = stats.Name,
            Level = stats.Level,
            MaxHp = stats.MaxHp,
            MaxMp = stats.MaxMp,
            CurrentHp = stats.CurrentHp,
            CurrentMp = stats.CurrentMp,
            PhysicalAttack = stats.PhysicalAttack,
            PhysicalDefense = stats.PhysicalDefense,
            MagicAttack = stats.MagicAttack,
            MagicDefense = stats.MagicDefense,
            Hit = stats.Hit,
            Dodge = stats.Dodge,
            Critical = stats.Critical,
            CriticalResistance = stats.CriticalResistance,
            DamageAbsorb = stats.DamageAbsorb,
            PhysicalDamageBonus = stats.PhysicalDamageBonus,
            MagicDamageBonus = stats.MagicDamageBonus,
            CureBonus = stats.CureBonus,
            BeCureBonus = stats.BeCureBonus,
            HpRecovery = stats.HpRecovery,
            MpRecovery = stats.MpRecovery,
            IgnorePhysicalDefense = stats.IgnorePhysicalDefense,
            IgnoreMagicDefense = stats.IgnoreMagicDefense,
            PhysicalAppendDamage = stats.PhysicalAppendDamage,
            MagicAppendDamage = stats.MagicAppendDamage,
            CriticalDamagePercent = stats.CriticalDamagePercent,
            CriticalDamageFlat = stats.CriticalDamageFlat,
            WeaponScore = stats.WeaponScore,
            WeaponRank = stats.WeaponRank,
            WeaponAuraEffect = stats.WeaponAuraEffect,
            ArmorScore = stats.ArmorScore,
            ArmorRank = stats.ArmorRank,
            ArmorAuraEffect = stats.ArmorAuraEffect,
            LearnedSkillCount = stats.LearnedSkillCount,
            PhysicalDamageReduction = stats.PhysicalDamageReduction,
            MagicDamageReduction = stats.MagicDamageReduction,
            CriticalDamageReduction = stats.CriticalDamageReduction,
            LifeAbsorption = stats.LifeAbsorption,
            DamageRebound = stats.DamageRebound
        };
}
