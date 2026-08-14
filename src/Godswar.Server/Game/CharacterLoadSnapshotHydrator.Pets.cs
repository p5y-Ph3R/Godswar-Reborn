using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static partial class CharacterLoadSnapshotHydrator
{
    internal static PetBootstrapSnapshot MapPet(
        CharacterPetSnapshot pet) =>
        new(
            pet.PetId,
            pet.AccountId,
            pet.OwnerCharacterId,
            pet.SpeciesId,
            pet.Name,
            pet.Sex,
            pet.Level,
            pet.Experience,
            (PetAptitude)pet.Aptitude,
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
            pet.CreatedAtUtc,
            pet.UpdatedAtUtc,
            pet.StatValues
                .Select(static value => new PetStatValueSnapshot(
                    value.StatCode,
                    value.InitialSavvy,
                    value.AddedSavvy,
                    value.BaseGrowthRate,
                    value.GrowthAcceleration,
                    value.Revision,
                    value.BirthInitialSavvy,
                    value.RarityAddedSavvy))
                .ToArray(),
            pet.CharacterBonuses
                .Select(static bonus => new PetCharacterBonusSnapshot(
                    bonus.EffectCode,
                    bonus.EffectValue,
                    bonus.Revision))
                .ToArray(),
            pet.Skills
                .Select(static skill => new PetSkillSnapshot(
                    skill.SkillId,
                    skill.SlotIndex,
                    skill.SkillRank,
                    skill.SkillExperience,
                    skill.IsActive,
                    skill.Revision))
                .ToArray(),
            pet.OpenedSkillSlots,
            pet.AvailableSkillSlots,
            pet.TalentMask,
            pet.InitialSavvySourceVersion,
            pet.SoulContractStage);
}
