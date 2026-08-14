using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private const string LegacyGrowthRateSemantics =
        "legacy_base_preserve_acceleration";
    private const string CountWidenedRateSemantics =
        "nature_base_rebirth_modifier_v1";

    private static decimal[] ToGrowthArray(PetSavvy value) =>
    [
        value.Agility,
        value.Strength,
        value.Accuracy,
        value.Technique,
        value.Wisdom,
        value.Luck
    ];

    private static void ValidateLockedGrowthPreview(
        LockedPetGrowthPreview preview)
    {
        if (preview.GrowthRates.Length != 6 ||
            preview.ExpectedStatRevisions.Length != 6)
        {
            throw new InvalidDataException(
                "The Phoenix Growth preview vectors are incomplete.");
        }

        if (string.Equals(
                preview.RateSemantics,
                LegacyGrowthRateSemantics,
                StringComparison.Ordinal))
        {
            if (preview.CompletedRebirths is not null ||
                preview.RebirthModifiers is not null)
            {
                throw new InvalidDataException(
                    "The legacy Phoenix Growth preview has new evidence.");
            }
            return;
        }

        if (!preview.UsesRebirthCountWidenedRates ||
            preview.CompletedRebirths is not { } completedRebirths ||
            preview.RebirthModifiers is not { Length: 6 } modifiers ||
            !PetPhoenixRebirthModifierPolicy.IsValid(
                completedRebirths,
                new PetSavvy(
                    modifiers[0], modifiers[1], modifiers[2],
                    modifiers[3], modifiers[4], modifiers[5])))
        {
            throw new InvalidDataException(
                "The Phoenix Growth preview semantics are invalid.");
        }
    }

    private void ValidateCountWidenedNatureRoll(
        LockedGrowthResetPet pet,
        decimal[] natureRates)
    {
        var total = natureRates.Sum();
        if (!_petContent.TryGetAptitude(pet.Aptitude, out var bracket) ||
            natureRates.Length != 6 ||
            natureRates.Any(static value => value <= 0m) ||
            total < bracket.MinimumTotalGrowth ||
            total > bracket.MaximumTotalGrowth ||
            total * 100m != decimal.Truncate(total * 100m))
        {
            throw new InvalidDataException(
                "The Phoenix nature Growth roll is outside its bracket.");
        }

        var mean = total / natureRates.Length;
        var minimum = mean * (1m - bracket.MaximumGrowthStatDeviation);
        var maximum = mean * (1m + bracket.MaximumGrowthStatDeviation);
        if (natureRates.Any(value =>
                value < minimum ||
                value > maximum ||
                value * 1_000_000m !=
                    decimal.Truncate(value * 1_000_000m)))
        {
            throw new InvalidDataException(
                "The Phoenix nature Growth distribution is invalid.");
        }
    }
}
