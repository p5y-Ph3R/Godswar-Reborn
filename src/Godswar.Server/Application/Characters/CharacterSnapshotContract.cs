using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Application.Characters;

internal static class CharacterSnapshotContract
{
    public static void Validate(CharacterAccountSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ContractVersion != CharacterSnapshotContractVersions.Current)
        {
            Fail(
                CharacterSnapshotFailureReason.UnsupportedContractVersion,
                $"Character snapshot contract {snapshot.ContractVersion} is unsupported.");
        }

        RequirePositive(snapshot.AccountId, "account ID");
        if (!snapshot.RealmId.IsValid)
        {
            throw Invalid("Character snapshot realm is invalid.");
        }
        RequireText(
            snapshot.ProviderSnapshotToken,
            CharacterSnapshotLimits.ProviderSnapshotTokenLength,
            "provider snapshot token");
        RequireUtc(snapshot.ReadAtUtc, "snapshot read time");
        if (snapshot.SlotPolicy != CharacterSlotPolicy.SingleCharacterV1)
        {
            Fail(
                CharacterSnapshotFailureReason.UnsupportedContractVersion,
                $"Character slot policy {(byte)snapshot.SlotPolicy} is unsupported.");
        }

        if (snapshot.Character is not null)
        {
            ValidateCharacter(
                snapshot.Character,
                snapshot.AccountId,
                snapshot.RealmId);
        }
    }

    private static void ValidateCharacter(
        CharacterLoadSnapshot snapshot,
        int accountId,
        Godswar.Server.Domain.World.Instances.RealmId realmId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var identity = snapshot.Identity ??
            throw Invalid("Character identity is missing.");
        RequirePositive(identity.CharacterId, "character ID");
        if (identity.AccountId != accountId ||
            identity.RealmId != realmId)
        {
            Fail(
                CharacterSnapshotFailureReason.OwnershipMismatch,
                "Character ownership does not match the requested account and realm.");
        }

        RequireText(
            identity.Name,
            CharacterSnapshotLimits.CharacterNameLength,
            "character name");
        RequireUtc(identity.CreatedAtUtc, "character creation time");
        if (identity.CharacterSlot != 0 ||
            identity.LifecycleVersion < 1)
        {
            throw Invalid(
                "Character lifecycle identity is outside the " +
                "SingleCharacterV1 bounds.");
        }

        if (snapshot.Appearance is null)
        {
            throw Invalid("Character appearance is missing.");
        }

        ValidateLocation(snapshot.Location);
        ValidateProgression(snapshot.Progression);
        ValidateVitals(snapshot.Vitals);
        ValidateWallet(snapshot.Wallet);
        ValidateLoadout(snapshot.Loadout);
        ValidateZodiac(snapshot.Zodiac);
        ValidateStats(
            snapshot.CalculatedStats,
            identity,
            snapshot.Progression,
            snapshot.Vitals,
            snapshot.Loadout);
        ValidateSkills(snapshot.Skills);
        ValidateTalents(snapshot.Talents);
        ValidatePets(snapshot.PetShed, snapshot.Pets, identity);
        ValidateBoosts(snapshot.PersonalBoosts);
    }

    private static void ValidateLocation(CharacterLocationSnapshot location)
    {
        if (location is null)
        {
            throw Invalid("Character location is missing.");
        }

        if (!float.IsFinite(location.PositionX) ||
            !float.IsFinite(location.PositionZ))
        {
            throw Invalid("Character position must be finite.");
        }

        if (location.PositionRevision < 0)
        {
            throw Invalid(
                "Character position revision must not be negative.");
        }
    }

    private static void ValidateProgression(
        CharacterProgressionSnapshot progression)
    {
        if (progression is null)
        {
            throw Invalid("Character progression is missing.");
        }

        if (progression.Level is < 1 or >
                CharacterProgressionSnapshotRules.MaximumCharacterLevel ||
            progression.Experience is < 0 or > uint.MaxValue ||
            progression.TalentPoints < 0 ||
            progression.TalentExperience < 0 ||
            progression.HolySuitPoints < 0 ||
            progression.Revision < 0)
        {
            throw Invalid("Character progression is outside persisted bounds.");
        }

        if (progression.FighterLevelSealed &&
            progression.Level !=
            CharacterProgressionSnapshotRules.FighterLevelSealLevel)
        {
            throw Invalid(
                $"Fighter level sealing is valid only at level " +
                $"{CharacterProgressionSnapshotRules.FighterLevelSealLevel}.");
        }
    }

    private static void ValidateVitals(CharacterVitalsSnapshot vitals)
    {
        if (vitals is null)
        {
            throw Invalid("Character vitals are missing.");
        }

        if (vitals.BaseMaxHp < 1 ||
            vitals.BaseMaxMp < 0 ||
            vitals.PersistedCurrentHp < 0 ||
            vitals.PersistedCurrentMp < 0 ||
            vitals.Revision < 0)
        {
            throw Invalid("Character vitals are outside the persisted bounds.");
        }
    }

    private static void ValidateWallet(CharacterWalletSnapshot wallet)
    {
        if (wallet is null)
        {
            throw Invalid("Character wallet is missing.");
        }

        if (wallet.Silver < 0 || wallet.Gold < 0)
        {
            throw Invalid("Character wallet contains a negative balance.");
        }
    }

    private static void ValidateLoadout(CharacterLoadoutSnapshot loadout)
    {
        if (loadout is null)
        {
            throw Invalid("Character loadout is missing.");
        }

        RequireBoundedText(
            loadout.Equipment,
            CharacterSnapshotLimits.EquipmentProjectionLength,
            "equipment projection");
        RequireBoundedText(
            loadout.KitBag,
            CharacterSnapshotLimits.KitBagProjectionLength,
            "kit-bag projection");
        if (loadout.WeaponRank < 0 ||
            loadout.ArmorRank < 0 ||
            loadout.InventoryRevision < 0)
        {
            throw Invalid("Character equipment ranks cannot be negative.");
        }
    }

    private static void ValidateZodiac(CharacterZodiacSnapshot zodiac)
    {
        if (zodiac is null)
        {
            throw Invalid("Character zodiac state is missing.");
        }

        if (zodiac.Level < 1 ||
            zodiac.Energy < 0 ||
            zodiac.EnergyRemainderX100 < 0 ||
            zodiac.OnlineDurationTicksToday < 0 ||
            zodiac.AccumulatedExperienceX100 < 0 ||
            zodiac.AccumulatedTalentExperienceX100 < 0)
        {
            throw Invalid("Character zodiac state is outside persisted bounds.");
        }

        RequireUtcIfPresent(zodiac.LuckyExpiresAtUtc, "zodiac lucky expiry");
        RequireUtcIfPresent(zodiac.LastOnlineAtUtc, "zodiac last-online time");
        RequireCount(
            zodiac.SkillGridLevels,
            CharacterSnapshotLimits.ZodiacGridCount,
            exact: true,
            "zodiac grid levels");
        RequireCount(
            zodiac.SkillGridSkillIds,
            CharacterSnapshotLimits.ZodiacGridCount,
            exact: true,
            "zodiac grid skills");
        if (zodiac.SkillGridLevels.Any(static value => value < 0) ||
            zodiac.SkillGridSkillIds.Any(static value => value < -1))
        {
            throw Invalid("Character zodiac grids contain invalid values.");
        }
    }

    private static void ValidateStats(
        CharacterCalculatedStatsSnapshot stats,
        CharacterIdentitySnapshot identity,
        CharacterProgressionSnapshot progression,
        CharacterVitalsSnapshot vitals,
        CharacterLoadoutSnapshot loadout)
    {
        if (stats is null)
        {
            Fail(
                CharacterSnapshotFailureReason.MissingCalculatedStats,
                "Character calculated stats are missing.");
        }

        if (stats.CharacterId != identity.CharacterId ||
            stats.AccountId != identity.AccountId ||
            !string.Equals(stats.Name, identity.Name, StringComparison.Ordinal) ||
            stats.Level != progression.Level)
        {
            Fail(
                CharacterSnapshotFailureReason.OwnershipMismatch,
                "Calculated stats do not identify the loaded character.");
        }

        if (stats.MaxHp < 1 ||
            stats.MaxMp < 0 ||
            stats.CurrentHp < 0 ||
            stats.CurrentHp > stats.MaxHp ||
            stats.CurrentMp < 0 ||
            stats.CurrentMp > Math.Max(1, stats.MaxMp) ||
            stats.CurrentHp != Math.Clamp(
                vitals.PersistedCurrentHp,
                0,
                stats.MaxHp) ||
            stats.CurrentMp != Math.Clamp(
                vitals.PersistedCurrentMp,
                0,
                Math.Max(1, stats.MaxMp)) ||
            stats.WeaponRank < 0 ||
            stats.ArmorRank < 0 ||
            stats.WeaponRank != loadout.WeaponRank ||
            stats.WeaponAuraEffect != loadout.WeaponAuraEffect ||
            stats.ArmorRank != loadout.ArmorRank ||
            stats.ArmorAuraEffect != loadout.ArmorAuraEffect ||
            stats.StatusHit < 0 ||
            stats.StatusResistance < 0 ||
            stats.LifeAbsorptionFlat < 0 ||
            stats.LearnedSkillCount < 0)
        {
            throw Invalid("Character calculated stats are outside valid bounds.");
        }
    }

    private static void ValidateSkills(
        ImmutableArray<CharacterSkillSnapshot> skills)
    {
        RequireCount(
            skills,
            CharacterSnapshotLimits.SkillCount,
            exact: false,
            "character skills");
        if (skills.Any(static skill => skill is null ||
                                       skill.SkillId < 0 ||
                                       skill.Level < 1) ||
            HasDuplicates(skills, static skill => skill.SkillId))
        {
            throw Invalid("Character skills contain invalid or duplicate rows.");
        }
    }

    private static void ValidateTalents(
        ImmutableArray<CharacterTalentSnapshot> talents)
    {
        RequireCount(
            talents,
            CharacterSnapshotLimits.TalentCount,
            exact: false,
            "character talents");
        if (talents.Any(static talent => talent is null ||
                                         talent.TalentId < 0 ||
                                         talent.Rank < 0 ||
                                         talent.DisplayValue < 1 ||
                                         talent.NextCost < 0) ||
            HasDuplicates(talents, static talent => talent.TalentId))
        {
            throw Invalid("Character talents contain invalid or duplicate rows.");
        }
    }

    private static void ValidatePets(
        CharacterPetShedSnapshot petShed,
        ImmutableArray<CharacterPetSnapshot> pets,
        CharacterIdentitySnapshot owner)
    {
        if (petShed is null ||
            !PetShedCapacityPolicy.IsValid(petShed.OpenedCellCount) ||
            petShed.Revision < 0 ||
            pets.Length > petShed.OpenedCellCount)
        {
            throw Invalid(
                "Owned pets exceed the character's persisted pet-shed capacity.");
        }

        RequireCount(
            pets,
            CharacterSnapshotLimits.OwnedPetCount,
            exact: false,
            "owned pets");
        if (pets.Any(static pet => pet is null) ||
            HasDuplicates(pets, static pet => pet.PetId) ||
            pets.Count(static pet => pet.IsCarried) > 1 ||
            pets.Count(static pet => pet.IsSummoned) > 1 ||
            pets.Count(static pet => pet.ContributesToCharacter) > 1)
        {
            throw Invalid("Owned pets violate identity or presence uniqueness.");
        }

        foreach (var pet in pets)
        {
            ValidatePet(pet, owner);
        }
    }

    private static void ValidatePet(
        CharacterPetSnapshot pet,
        CharacterIdentitySnapshot owner)
    {
        if (pet is null ||
            pet.PetId <= 0 ||
            pet.AccountId != owner.AccountId ||
            pet.OwnerCharacterId != owner.CharacterId)
        {
            Fail(
                CharacterSnapshotFailureReason.OwnershipMismatch,
                "Owned pet does not identify the loaded character.");
        }

        RequirePositive(pet.SpeciesId, "pet species ID");
        RequireText(
            pet.Name,
            CharacterSnapshotLimits.PetNameLength,
            "pet name");
        RequireText(
            pet.ActivityState,
            CharacterSnapshotLimits.PetActivityStateLength,
            "pet activity state");
        RequireUtc(pet.CreatedAtUtc, "pet creation time");
        RequireUtc(pet.UpdatedAtUtc, "pet update time");
        if (pet.Level is < 1 or > 120 ||
            pet.Experience < 0 ||
            pet.Aptitude is < 1 or > 16 ||
            !PetRankWirePolicy.IsRepresentable(pet.Rank) ||
            pet.CompletedRebirths < 0 ||
            pet.RebirthsRemaining < 0 ||
            pet.CompletedPetMerges < 0 ||
            pet.ProjectedSoulContractStage >
                PetSoulContractRules.MaximumStage ||
            pet.SoulContractStage > 0 && !pet.HasSoulContract ||
            pet.CurrentEnergy < 0 ||
            pet.MaximumEnergy <= 0 ||
            pet.MaximumEnergy < pet.CurrentEnergy ||
            pet.Amity < 0 ||
            pet.Satiety < 0 ||
            pet.RemainingLifetime < 0 ||
            pet.AvailableStatPoints < 0 ||
            pet.OpenedSkillSlots is < 1 or > 12 ||
            pet.AvailableSkillSlots is < 1 or > 12 ||
            pet.OpenedSkillSlots > pet.AvailableSkillSlots ||
            pet.TalentMask is < 0 or > 31 ||
            pet.HasOwnerMergeTalent !=
                ((pet.TalentMask & 16) != 0) ||
            pet.Revision < 0 ||
            pet.IsSummoned && !pet.IsCarried ||
            pet.ContributesToCharacter &&
            (!pet.IsSummoned || !pet.HasOwnerMergeTalent))
        {
            throw Invalid("Owned pet contains invalid persisted state.");
        }

        RequireCount(
            pet.StatValues,
            CharacterSnapshotLimits.PetStatValueCount,
            exact: false,
            "pet stat values");
        RequireCount(
            pet.CharacterBonuses,
            CharacterSnapshotLimits.PetCharacterBonusCount,
            exact: false,
            "pet character bonuses");
        RequireCount(
            pet.Skills,
            CharacterSnapshotLimits.PetSkillCount,
            exact: false,
            "pet skills");
        if (pet.StatValues.Any(static value =>
                value is null ||
                value.StatCode is < 1 or > 6 ||
                value.InitialSavvy < 0 ||
                value.AddedSavvy < 0 ||
                value.BaseGrowthRate < 0 ||
                value.GrowthAcceleration < 0 ||
                value.Revision < 0) ||
            HasDuplicates(pet.StatValues, static value => value.StatCode) ||
            pet.CharacterBonuses.Any(static bonus =>
                bonus is null || bonus.EffectCode < 0 || bonus.Revision < 0) ||
            HasDuplicates(
                pet.CharacterBonuses,
                static bonus => bonus.EffectCode) ||
            pet.Skills.Any(static skill =>
                skill is null ||
                skill.SkillId <= 0 ||
                skill.SlotIndex is < 0 or >= 12 ||
                skill.SkillRank <= 0 ||
                skill.SkillExperience < 0 ||
                skill.Revision < 0) ||
            HasDuplicates(pet.Skills, static skill => skill.SlotIndex) ||
            pet.Skills.Count(static skill => skill.IsActive) >
                pet.OpenedSkillSlots ||
            pet.Skills.Any(skill =>
                skill.IsActive &&
                skill.SlotIndex >= pet.OpenedSkillSlots))
        {
            throw Invalid("Owned pet child rows are invalid or duplicated.");
        }

        CharacterSnapshotPetSavvyContract.Validate(pet);
    }

    private static void ValidateBoosts(
        ImmutableArray<CharacterProgressionBoostSnapshot> boosts)
    {
        RequireCount(
            boosts,
            CharacterSnapshotLimits.PersonalBoostCount,
            exact: false,
            "personal progression boosts");
        if (boosts.Any(static boost =>
                boost is null ||
                boost.StatusId <= 0 ||
                boost.Kind <= 0 ||
                boost.Priority < 0 ||
                boost.RemainingOnlineTicks < 0) ||
            HasDuplicates(boosts, static boost => boost.Kind))
        {
            throw Invalid(
                "Personal progression boosts contain invalid or duplicate rows.");
        }

        foreach (var boost in boosts)
        {
            RequireUtc(boost.ActivatedAtUtc, "boost activation time");
            RequireBoundedText(
                boost.Source,
                CharacterSnapshotLimits.BoostSourceLength,
                "boost source");
        }
    }

    private static bool HasDuplicates<T, TKey>(
        ImmutableArray<T> values,
        Func<T, TKey> keySelector)
        where TKey : notnull =>
        values
            .GroupBy(keySelector)
            .Any(static group => group.Skip(1).Any());

    private static void RequireCount<T>(
        ImmutableArray<T> values,
        int limit,
        bool exact,
        string field)
    {
        if (values.IsDefault || exact && values.Length != limit ||
            !exact && values.Length > limit)
        {
            Fail(
                CharacterSnapshotFailureReason.BoundsExceeded,
                exact
                    ? $"{field} must contain exactly {limit} rows."
                    : $"{field} exceeds the {limit}-row limit.");
        }
    }

    private static void RequirePositive(long value, string field)
    {
        if (value <= 0)
        {
            throw Invalid($"{field} must be positive.");
        }
    }

    private static void RequireText(
        string? value,
        int limit,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{field} is missing.");
        }

        RequireBoundedText(value, limit, field);
    }

    private static void RequireBoundedText(
        string? value,
        int limit,
        string field)
    {
        if (value is null)
        {
            throw Invalid($"{field} is missing.");
        }

        if (value.Length > limit)
        {
            Fail(
                CharacterSnapshotFailureReason.BoundsExceeded,
                $"{field} exceeds the {limit}-character limit.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string field)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw Invalid($"{field} must be a non-default UTC timestamp.");
        }
    }

    private static void RequireUtcIfPresent(
        DateTimeOffset? value,
        string field)
    {
        if (value.HasValue)
        {
            RequireUtc(value.Value, field);
        }
    }

    private static CharacterSnapshotUnavailableException Invalid(
        string message) =>
        new(CharacterSnapshotFailureReason.InvalidData, message);

    [DoesNotReturn]
    private static void Fail(
        CharacterSnapshotFailureReason reason,
        string message) =>
        throw new CharacterSnapshotUnavailableException(reason, message);
}
