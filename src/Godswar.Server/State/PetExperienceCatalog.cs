namespace Godswar.Server.State;

/// <summary>
/// The original GodsWar pet experience ladder. Each entry is the experience
/// spent by a pet at the entry's current level to advance exactly one level.
/// </summary>
internal static class PetExperienceCatalog
{
    public const int MinimumLevel = 1;
    public const int MaximumLevel = PetManagerPlanner.MaximumPetLevel;
    public const long TotalExperienceToMaximumLevel = 252_947_820;

    private static readonly int[] RequiredExperienceByCurrentLevel =
    [
        1_500, 4_500, 7_500, 10_500, 13_500, 16_500, 19_500, 27_540,
        37_305, 49_500, 78_450, 133_725, 187_650, 240_300, 291_600,
        341_775, 390_675, 438_375, 485_025, 530_550, 575_025, 618_450,
        660_900, 702_450, 743_025, 782_775, 821_625, 859_725, 897_075,
        933_675, 969_525, 1_004_775, 1_039_425, 1_073_475, 1_106_925,
        1_139_925, 1_172_400, 1_204_425, 1_236_075, 1_267_425,
        1_298_325, 1_329_000, 1_359_375, 1_389_600, 1_419_525,
        1_449_375, 1_479_075, 1_508_775, 1_538_325, 1_567_950,
        1_597_575, 1_627_200, 1_657_050, 1_686_975, 1_717_050,
        1_747_350, 1_777_875, 1_808_775, 1_839_900, 1_871_400,
        1_903_350, 1_935_675, 1_968_450, 2_001_750, 2_035_575,
        2_070_000, 2_105_025, 2_140_650, 2_177_025, 2_214_075,
        2_251_875, 2_290_500, 2_329_875, 2_370_150, 2_411_400,
        2_453_475, 2_496_600, 2_540_700, 2_585_775, 2_632_050,
        2_679_375, 2_727_825, 2_777_550, 2_828_400, 2_880_525,
        2_934_000, 2_988_825, 3_044_925, 3_102_525, 3_161_475,
        3_222_000, 3_283_950, 3_347_475, 3_412_575, 3_479_325,
        3_547_725, 3_617_850, 3_689_625, 3_763_275, 3_838_650,
        3_915_900, 3_994_950, 4_076_025, 4_158_975, 4_243_875,
        4_330_875, 4_419_900, 4_511_025, 4_604_250, 4_699_650,
        4_797_225, 4_897_125, 4_999_200, 5_103_600, 5_210_400,
        5_319_525, 5_431_125, 5_545_125, 5_661_675
    ];

    static PetExperienceCatalog()
    {
        if (RequiredExperienceByCurrentLevel.Length !=
                MaximumLevel - MinimumLevel ||
            RequiredExperienceByCurrentLevel.Any(
                static required => required <= 0) ||
            !RequiredExperienceByCurrentLevel
                .Zip(
                    RequiredExperienceByCurrentLevel.Skip(1),
                    static (current, next) => current < next)
                .All(static increasing => increasing) ||
            RequiredExperienceByCurrentLevel.Sum(
                static required => (long)required) !=
                    TotalExperienceToMaximumLevel)
        {
            throw new InvalidDataException(
                "The pet experience ladder is incomplete or corrupt.");
        }
    }

    public static int RequiredForNextLevel(int currentLevel)
    {
        if (currentLevel is < MinimumLevel or > MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentLevel),
                currentLevel,
                $"Pet level must be between {MinimumLevel} and {MaximumLevel}.");
        }

        return currentLevel == MaximumLevel
            ? 0
            : RequiredExperienceByCurrentLevel[currentLevel - MinimumLevel];
    }
}
