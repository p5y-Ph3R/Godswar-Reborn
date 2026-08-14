using Godswar.Server.Application.Pets;

namespace Godswar.Server.Application.Characters;

internal static class CharacterSnapshotPetSavvyContract
{
    public static void Validate(CharacterPetSnapshot pet)
    {
        var hasChildProvenance = pet.StatValues.Any(static stat =>
            stat.BirthInitialSavvy is not null ||
            stat.RarityAddedSavvy is not null);
        if (pet.InitialSavvySourceVersion is null)
        {
            if (hasChildProvenance)
            {
                ThrowInvalid(
                    "Legacy pet snapshot has partial Savvy provenance.");
            }
            return;
        }

        if (!string.Equals(
                pet.InitialSavvySourceVersion,
                PetSavvyPersistenceContract.SourceVersion,
                StringComparison.Ordinal) ||
            pet.StatValues.Length != 6)
        {
            ThrowInvalid(
                "Pet snapshot has unsupported or incomplete Savvy provenance.");
        }

        var basicTotal = 0m;
        var birthTotal = 0m;
        foreach (var stat in pet.StatValues)
        {
            var birth = stat.BirthInitialSavvy;
            var rarity = stat.RarityAddedSavvy;
            if (birth is null || birth <= 0m ||
                rarity is null || rarity <= 0m ||
                birth != rarity ||
                stat.InitialSavvy <= 0m ||
                stat.BaseGrowthRate <= 0m ||
                stat.GrowthAcceleration < 0m ||
                stat.AddedSavvy != PetSavvyPersistenceContract.ResolveAdded(
                    pet.Level,
                    stat.BaseGrowthRate,
                    stat.GrowthAcceleration))
            {
                ThrowInvalid(
                    "Pet snapshot has stale Basic, Growth, or Added state.");
            }
            basicTotal = checked(basicTotal + stat.InitialSavvy);
            birthTotal = checked(birthTotal + birth.GetValueOrDefault());
        }
        if (basicTotal < birthTotal)
        {
            ThrowInvalid(
                "Pet snapshot has less aggregate Basic Savvy than its hatch baseline.");
        }
    }

    private static void ThrowInvalid(string message) =>
        throw new CharacterSnapshotUnavailableException(
            CharacterSnapshotFailureReason.InvalidData,
            message);
}
