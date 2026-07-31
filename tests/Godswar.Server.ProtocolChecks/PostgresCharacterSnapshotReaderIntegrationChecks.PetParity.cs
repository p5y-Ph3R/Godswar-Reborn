using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static void AssertPetParity(
        IReadOnlyList<PetBootstrapSnapshot> expected,
        IReadOnlyList<CharacterPetSnapshot> actual)
    {
        Check.Equal(
            expected.Count,
            actual.Count,
            "snapshot pet count matches the legacy reader");
        Check.Equal(1, actual.Count, "rich fixture owns one pet");

        for (var petIndex = 0; petIndex < expected.Count; petIndex++)
        {
            AssertPetRootParity(
                expected[petIndex],
                actual[petIndex],
                petIndex);
            AssertPetStatParity(
                expected[petIndex],
                actual[petIndex],
                petIndex);
            AssertPetBonusParity(
                expected[petIndex],
                actual[petIndex],
                petIndex);
            AssertPetSkillParity(
                expected[petIndex],
                actual[petIndex],
                petIndex);
        }
    }

    private static void AssertPetRootParity(
        PetBootstrapSnapshot expected,
        CharacterPetSnapshot actual,
        int petIndex)
    {
        var label = $"snapshot pet {petIndex}";
        Check.Equal(expected.PetId, actual.PetId, $"{label} ID");
        Check.Equal(expected.AccountId, actual.AccountId, $"{label} account");
        Check.Equal(
            expected.OwnerCharacterId,
            actual.OwnerCharacterId,
            $"{label} owner");
        Check.Equal(expected.SpeciesId, actual.SpeciesId, $"{label} species");
        Check.Equal(expected.Name, actual.Name, $"{label} name");
        Check.Equal(expected.Sex, actual.Sex, $"{label} sex");
        Check.Equal(expected.Level, actual.Level, $"{label} level");
        Check.Equal(
            expected.Experience,
            actual.Experience,
            $"{label} experience");
        Check.Equal((short)expected.Aptitude, actual.Aptitude, $"{label} aptitude");
        Check.Equal(expected.Rank, actual.Rank, $"{label} rank");
        Check.Equal(
            expected.CompletedRebirths,
            actual.CompletedRebirths,
            $"{label} completed rebirths");
        Check.Equal(
            expected.RebirthsRemaining,
            actual.RebirthsRemaining,
            $"{label} remaining rebirths");
        Check.Equal(
            expected.CompletedPetMerges,
            actual.CompletedPetMerges,
            $"{label} completed merges");
        Check.Equal(
            expected.HasSoulContract,
            actual.HasSoulContract,
            $"{label} soul contract");
        Check.Equal(
            expected.HasOwnerMergeTalent,
            actual.HasOwnerMergeTalent,
            $"{label} owner merge talent");
        Check.Equal(
            expected.CurrentEnergy,
            actual.CurrentEnergy,
            $"{label} current energy");
        Check.Equal(
            expected.MaximumEnergy,
            actual.MaximumEnergy,
            $"{label} maximum energy");
        Check.Equal(expected.Amity, actual.Amity, $"{label} amity");
        Check.Equal(expected.Satiety, actual.Satiety, $"{label} satiety");
        Check.Equal(
            expected.RemainingLifetime,
            actual.RemainingLifetime,
            $"{label} remaining lifetime");
        Check.Equal(
            expected.AvailableStatPoints,
            actual.AvailableStatPoints,
            $"{label} available stat points");
        Check.Equal(
            expected.GrowthRevealed,
            actual.GrowthRevealed,
            $"{label} growth revealed");
        Check.Equal(expected.IsBound, actual.IsBound, $"{label} bound state");
        Check.Equal(
            expected.ActivityState,
            actual.ActivityState,
            $"{label} activity state");
        Check.Equal(expected.IsCarried, actual.IsCarried, $"{label} carried state");
        Check.Equal(
            expected.IsSummoned,
            actual.IsSummoned,
            $"{label} summoned state");
        Check.Equal(
            expected.ContributesToCharacter,
            actual.ContributesToCharacter,
            $"{label} contribution state");
        Check.Equal(expected.Revision, actual.Revision, $"{label} revision");
        Check.Equal(expected.CreatedAt, actual.CreatedAtUtc, $"{label} created time");
        Check.Equal(expected.UpdatedAt, actual.UpdatedAtUtc, $"{label} updated time");
    }

    private static void AssertPetStatParity(
        PetBootstrapSnapshot expected,
        CharacterPetSnapshot actual,
        int petIndex)
    {
        Check.Equal(
            expected.StatValues.Count,
            actual.StatValues.Length,
            $"snapshot pet {petIndex} stat count");
        for (var index = 0; index < expected.StatValues.Count; index++)
        {
            var legacy = expected.StatValues[index];
            var snapshot = actual.StatValues[index];
            var label = $"snapshot pet {petIndex} stat {index}";
            Check.Equal(legacy.StatCode, snapshot.StatCode, $"{label} code");
            Check.Equal(legacy.InitialSavvy, snapshot.InitialSavvy, $"{label} initial savvy");
            Check.Equal(legacy.AddedSavvy, snapshot.AddedSavvy, $"{label} added savvy");
            Check.Equal(legacy.BaseGrowthRate, snapshot.BaseGrowthRate, $"{label} base growth");
            Check.Equal(legacy.GrowthAcceleration, snapshot.GrowthAcceleration, $"{label} acceleration");
            Check.Equal(legacy.Revision, snapshot.Revision, $"{label} revision");
            Check.True(
                legacy.BirthInitialSavvy == snapshot.BirthInitialSavvy,
                $"{label} birth savvy");
            Check.True(
                legacy.RarityAddedSavvy == snapshot.RarityAddedSavvy,
                $"{label} rarity savvy");
        }
    }

    private static void AssertPetBonusParity(
        PetBootstrapSnapshot expected,
        CharacterPetSnapshot actual,
        int petIndex)
    {
        Check.Equal(
            expected.CharacterBonuses.Count,
            actual.CharacterBonuses.Length,
            $"snapshot pet {petIndex} bonus count");
        for (var index = 0; index < expected.CharacterBonuses.Count; index++)
        {
            var legacy = expected.CharacterBonuses[index];
            var snapshot = actual.CharacterBonuses[index];
            var label = $"snapshot pet {petIndex} bonus {index}";
            Check.Equal(legacy.EffectCode, snapshot.EffectCode, $"{label} effect");
            Check.Equal(legacy.EffectValue, snapshot.EffectValue, $"{label} value");
            Check.Equal(legacy.Revision, snapshot.Revision, $"{label} revision");
        }
    }

    private static void AssertPetSkillParity(
        PetBootstrapSnapshot expected,
        CharacterPetSnapshot actual,
        int petIndex)
    {
        Check.Equal(
            expected.Skills.Count,
            actual.Skills.Length,
            $"snapshot pet {petIndex} skill count");
        for (var index = 0; index < expected.Skills.Count; index++)
        {
            var legacy = expected.Skills[index];
            var snapshot = actual.Skills[index];
            var label = $"snapshot pet {petIndex} skill {index}";
            Check.Equal(legacy.SkillId, snapshot.SkillId, $"{label} ID");
            Check.Equal(legacy.SlotIndex, snapshot.SlotIndex, $"{label} slot");
            Check.Equal(legacy.SkillRank, snapshot.SkillRank, $"{label} rank");
            Check.Equal(legacy.SkillExperience, snapshot.SkillExperience, $"{label} experience");
            Check.Equal(legacy.IsActive, snapshot.IsActive, $"{label} active state");
            Check.Equal(legacy.Revision, snapshot.Revision, $"{label} revision");
        }
    }
}
