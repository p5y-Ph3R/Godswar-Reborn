using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record HydratedCharacterLoadSnapshot(
    GameCharacter Character,
    IReadOnlyList<SkillState> Skills,
    IReadOnlyList<TalentState> Talents,
    IReadOnlyList<PetBootstrapSnapshot> Pets,
    IReadOnlyList<CharacterProgressionBoostSnapshot> PersonalBoosts);

/// <summary>
/// Copies one validated, immutable application snapshot into the mutable
/// legacy session projections. Persistence has already closed its read
/// transaction before this boundary runs.
/// </summary>
internal static partial class CharacterLoadSnapshotHydrator
{
    public static HydratedCharacterLoadSnapshot? Hydrate(
        CharacterAccountSnapshot accountSnapshot)
    {
        CharacterSnapshotContract.Validate(accountSnapshot);
        return accountSnapshot.Character is null
            ? null
            : HydrateCharacter(accountSnapshot.Character);
    }

    private static HydratedCharacterLoadSnapshot HydrateCharacter(
        CharacterLoadSnapshot snapshot)
    {
        var calculatedStats = MapCalculatedStats(snapshot.CalculatedStats);
        var character = new GameCharacter
        {
            Id = snapshot.Identity.CharacterId,
            AccountId = snapshot.Identity.AccountId,
            CharacterSlot = snapshot.Identity.CharacterSlot,
            LifecycleState = CharacterLifecycleState.Active,
            LifecycleVersion = snapshot.Identity.LifecycleVersion,
            Name = snapshot.Identity.Name,
            Gender = snapshot.Appearance.Gender,
            Camp = snapshot.Appearance.Camp,
            Profession = snapshot.Appearance.Profession,
            Hair = snapshot.Appearance.Hair,
            Face = snapshot.Appearance.Face,
            Faith = snapshot.Appearance.Faith,
            ZodiacType = snapshot.Zodiac.Type,
            ZodiacLuckyStatus = snapshot.Zodiac.LuckyStatus,
            ZodiacLuckyExpiresAt = snapshot.Zodiac.LuckyExpiresAtUtc,
            ZodiacLevel = snapshot.Zodiac.Level,
            ZodiacEnergy = snapshot.Zodiac.Energy,
            ZodiacEnergyRemainderX100 =
                snapshot.Zodiac.EnergyRemainderX100,
            ZodiacOnlineDay = snapshot.Zodiac.OnlineDay,
            ZodiacOnlineDurationTicksToday =
                snapshot.Zodiac.OnlineDurationTicksToday,
            ZodiacLastOnlineAt = snapshot.Zodiac.LastOnlineAtUtc,
            ZodiacLastCompensationDay =
                snapshot.Zodiac.LastCompensationDay,
            ZodiacAccumulatedExperienceX100 =
                snapshot.Zodiac.AccumulatedExperienceX100,
            ZodiacAccumulatedTalentExperienceX100 =
                snapshot.Zodiac.AccumulatedTalentExperienceX100,
            ZodiacSkillGridLevels = snapshot.Zodiac.SkillGridLevels.ToArray(),
            ZodiacSkillGridSkillIds =
                snapshot.Zodiac.SkillGridSkillIds.ToArray(),
            CurrentMap = snapshot.Location.CurrentMap,
            PositionX = snapshot.Location.PositionX,
            PositionZ = snapshot.Location.PositionZ,
            PositionRevision = snapshot.Location.PositionRevision,
            Level = snapshot.Progression.Level,
            Experience = snapshot.Progression.Experience,
            TalentPoints = snapshot.Progression.TalentPoints,
            TalentExperience = snapshot.Progression.TalentExperience,
            HolySuitPoints = snapshot.Progression.HolySuitPoints,
            Silver = snapshot.Wallet.Silver,
            Gold = snapshot.Wallet.Gold,
            MaxHp = Math.Max(1, calculatedStats.MaxHp),
            MaxMp = Math.Max(0, calculatedStats.MaxMp),
            CurrentHp = Math.Clamp(
                calculatedStats.CurrentHp,
                0,
                Math.Max(1, calculatedStats.MaxHp)),
            CurrentMp = Math.Clamp(
                calculatedStats.CurrentMp,
                0,
                Math.Max(1, calculatedStats.MaxMp)),
            // Loading is not a vitals mutation. Preserve the revision observed
            // in the same database snapshot instead of calling ApplyTo(), which
            // deliberately advances a live mutable character revision.
            VitalsRevision = snapshot.Vitals.Revision,
            Equipment = snapshot.Loadout.Equipment,
            KitBag = snapshot.Loadout.KitBag,
            WeaponRank = calculatedStats.WeaponRank,
            WeaponAuraEffect = calculatedStats.WeaponAuraEffect,
            ArmorRank = calculatedStats.ArmorRank,
            ArmorAuraEffect = calculatedStats.ArmorAuraEffect,
            CreatedUtc = snapshot.Identity.CreatedAtUtc.UtcDateTime,
            CalculatedStats = calculatedStats
        };

        return new HydratedCharacterLoadSnapshot(
            character,
            snapshot.Skills
                .Select(static skill => new SkillState
                {
                    SkillId = skill.SkillId,
                    Level = skill.Level
                })
                .ToArray(),
            snapshot.Talents
                .Select(static talent => new TalentState
                {
                    TalentId = talent.TalentId,
                    Rank = talent.Rank,
                    DisplayValue = talent.DisplayValue,
                    NextCost = talent.NextCost
                })
                .ToArray(),
            snapshot.Pets.Select(MapPet).ToArray(),
            snapshot.PersonalBoosts.ToArray());
    }

    internal static CharacterStats MapCalculatedStats(
        CharacterCalculatedStatsSnapshot stats) =>
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
            LearnedSkillCount = stats.LearnedSkillCount
        };
}
