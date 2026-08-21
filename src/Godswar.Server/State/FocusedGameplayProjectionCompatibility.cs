using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Application.World;

namespace Godswar.Server.State;

/// <summary>
/// Keeps temporary legacy state models at the edge while focused application
/// contracts own the live persistence paths. Delete these mappings with the
/// remaining JSON/broad-store compatibility layer.
/// </summary>
internal static partial class FocusedGameplayProjectionCompatibility
{
    public static ExperienceBoostSnapshot ToApplication(
        ExperienceBoostState state,
        DateTimeOffset readAtUtc)
    {
        var mapped = new ExperienceBoostSnapshot(
            state.ActiveBoosts
                .OrderBy(static boost => boost.Kind)
                .Select(static boost => new ExperienceBoostEntry(
                    boost.StatusId,
                    boost.Kind,
                    boost.BonusBasisPoints,
                    boost.Priority,
                    boost.ExpiresAt,
                    boost.Source))
                .ToImmutableArray());
        ExperienceBoostContract.ValidateSnapshot(mapped, readAtUtc);
        return mapped;
    }

    public static ExperienceBoostState ToLegacy(
        ExperienceBoostSnapshot snapshot,
        DateTimeOffset readAtUtc)
    {
        ExperienceBoostContract.ValidateSnapshot(snapshot, readAtUtc);
        return new ExperienceBoostState(
            snapshot.ActiveBoosts
                .Select(static boost => new ActiveExperienceBoost(
                    boost.StatusId,
                    boost.Kind,
                    boost.BonusBasisPoints,
                    boost.Priority,
                    boost.ExpiresAtUtc,
                    boost.Source))
                .ToArray());
    }

    public static FactionAreaExperienceControl ToLegacy(
        WorldBossAreaControlSnapshot control)
    {
        WorldBossPersistenceContract.Validate(control);
        return new FactionAreaExperienceControl
        {
            MapId = checked((byte)control.MapId),
            ControllingCamp = control.ControllingCamp,
            BossTemplateKey = control.BossTemplateKey,
            DeathToken = control.DeathToken,
            BonusBasisPoints = control.BonusBasisPoints,
            ActivatedAt = control.ActivatedAtUtc,
            ExpiresAt = control.ExpiresAtUtc
        };
    }

    public static WorldBossAreaControlSnapshot ToApplication(
        FactionAreaExperienceControl control)
    {
        var mapped = new WorldBossAreaControlSnapshot(
            control.MapId,
            control.ControllingCamp,
            control.BossTemplateKey,
            control.DeathToken,
            control.BonusBasisPoints,
            control.ActivatedAt,
            control.ExpiresAt);
        WorldBossPersistenceContract.Validate(mapped);
        return mapped;
    }

    public static WorldBossRespawnState ToLegacy(
        WorldBossRespawnSnapshot respawn)
    {
        WorldBossPersistenceContract.Validate(respawn);
        return new WorldBossRespawnState(
            respawn.MapId,
            respawn.BossTemplateKey,
            respawn.RespawnAtUtc);
    }

    public static ZodiacLevelUpgradeStoreResult ToApplication(
        ZodiacLevelUpgradeResult result)
    {
        var mapped = new ZodiacLevelUpgradeStoreResult(
            result.Status switch
            {
                ZodiacLevelUpgradeStatus.Succeeded =>
                    ZodiacLevelUpgradeStoreStatus.Succeeded,
                ZodiacLevelUpgradeStatus.CharacterLevelTooLow =>
                    ZodiacLevelUpgradeStoreStatus.CharacterLevelTooLow,
                ZodiacLevelUpgradeStatus.InsufficientEnergy =>
                    ZodiacLevelUpgradeStoreStatus.InsufficientEnergy,
                ZodiacLevelUpgradeStatus.MaximumLevelReached =>
                    ZodiacLevelUpgradeStoreStatus.MaximumLevelReached,
                _ => throw new InvalidDataException(
                    "Unknown legacy Zodiac-level status.")
            },
            result.PreviousLevel,
            result.CurrentLevel,
            result.RequiredCharacterLevel,
            result.EnergyCost,
            result.CurrentEnergy,
            result.CurrentEnergyRemainderX100);
        mapped.Validate();
        return mapped;
    }

    public static ZodiacLevelUpgradeResult ToLegacy(
        ZodiacLevelUpgradeStoreResult result)
    {
        result.Validate();
        return new ZodiacLevelUpgradeResult(
            result.Status switch
            {
                ZodiacLevelUpgradeStoreStatus.Succeeded =>
                    ZodiacLevelUpgradeStatus.Succeeded,
                ZodiacLevelUpgradeStoreStatus.CharacterLevelTooLow =>
                    ZodiacLevelUpgradeStatus.CharacterLevelTooLow,
                ZodiacLevelUpgradeStoreStatus.InsufficientEnergy =>
                    ZodiacLevelUpgradeStatus.InsufficientEnergy,
                ZodiacLevelUpgradeStoreStatus.MaximumLevelReached =>
                    ZodiacLevelUpgradeStatus.MaximumLevelReached,
                _ => throw new InvalidDataException(
                    "Unknown focused Zodiac-level status.")
            },
            result.PreviousLevel,
            result.CurrentLevel,
            result.RequiredCharacterLevel,
            result.EnergyCost,
            result.CurrentEnergy,
            result.CurrentEnergyRemainderX100);
    }

    public static CharacterCalculatedStatsSnapshot ToApplication(
        CharacterStats stats) =>
        new(
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
            stats.DamageRebound,
            stats.Profession,
            stats.PhysicalFlatAbsorption,
            stats.MagicFlatAbsorption,
            stats.CriticalDamageFlatReduction,
            stats.DamageReboundFlat,
            stats.BasicAttackIntervalMilliseconds,
            stats.BasicAttackRange,
            stats.StatusHit,
            stats.StatusResistance,
            stats.LifeAbsorptionFlat);

    public static CharacterPetSnapshot ToApplication(
        PetBootstrapSnapshot pet) =>
        new(
            pet.PetId,
            pet.AccountId,
            pet.OwnerCharacterId,
            pet.SpeciesId,
            pet.Name,
            pet.Sex,
            pet.Level,
            pet.Experience,
            (short)pet.Aptitude,
            pet.Rank,
            pet.CompletedRebirths,
            pet.RebirthsRemaining,
            pet.CompletedPetMerges,
            pet.HasSoulContract,
            pet.HasOwnerMergeTalent,
            pet.CurrentEnergy,
            pet.MaximumEnergy,
            pet.Amity,
            pet.Satiety,
            pet.RemainingLifetime,
            pet.AvailableStatPoints,
            pet.GrowthRevealed,
            pet.IsBound,
            pet.ActivityState,
            pet.IsCarried,
            pet.IsSummoned,
            pet.ContributesToCharacter,
            pet.Revision,
            pet.CreatedAt,
            pet.UpdatedAt,
            pet.StatValues
                .Select(static value => new CharacterPetStatValueSnapshot(
                    value.StatCode,
                    value.InitialSavvy,
                    value.AddedSavvy,
                    value.BaseGrowthRate,
                    value.GrowthAcceleration,
                    value.Revision,
                    value.BirthInitialSavvy,
                    value.RarityAddedSavvy))
                .ToImmutableArray(),
            pet.CharacterBonuses
                .Select(static bonus => new CharacterPetBonusSnapshot(
                    bonus.EffectCode,
                    bonus.EffectValue,
                    bonus.Revision))
                .ToImmutableArray(),
            pet.Skills
                .Select(static skill => new CharacterPetSkillSnapshot(
                    skill.SkillId,
                    skill.SlotIndex,
                    skill.SkillRank,
                    skill.SkillExperience,
                    skill.IsActive,
                    skill.Revision))
                .ToImmutableArray(),
            pet.OpenedSkillSlots,
            pet.AvailableSkillSlots,
            pet.TalentMask,
            pet.InitialSavvySourceVersion,
            pet.SoulContractStage);
}
