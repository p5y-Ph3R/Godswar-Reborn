namespace Godswar.Server.State;

internal enum HolyStoneCombinationEligibilityFailure : byte
{
    None = 0,
    TargetNotHolyStone,
    MaterialNotHolyStone,
    InvalidTargetStack,
    InvalidLevel,
    LevelMismatch
}

internal readonly record struct HolyStoneCombinationPlan(
    CompactItemEntry TargetAfter,
    CompactItemEntry FirstMaterialAfter,
    CompactItemEntry SecondMaterialAfter,
    CompactItemEntry ThirdMaterialAfter);

internal static class HolyStoneCombinationPolicy
{
    public const short MinimumSourceLevel = 4;
    public const short MaximumSourceLevel = 9;

    public static HolyStoneCombinationEligibilityFailure TryPrepare(
        CompactItemEntry target,
        CompactItemEntry firstMaterial,
        CompactItemEntry secondMaterial,
        CompactItemEntry thirdMaterial,
        out HolyStoneCombinationPlan plan)
    {
        plan = default;
        if (!IsHolyStone(target.Id))
        {
            return HolyStoneCombinationEligibilityFailure
                .TargetNotHolyStone;
        }
        if (!IsHolyStone(firstMaterial.Id) ||
            !IsHolyStone(secondMaterial.Id) ||
            !IsHolyStone(thirdMaterial.Id))
        {
            return HolyStoneCombinationEligibilityFailure
                .MaterialNotHolyStone;
        }
        if (target.Stack != 1 ||
            firstMaterial.Stack < 1 ||
            secondMaterial.Stack < 1 ||
            thirdMaterial.Stack < 1)
        {
            return HolyStoneCombinationEligibilityFailure
                .InvalidTargetStack;
        }
        if (target.Grade is < MinimumSourceLevel or > MaximumSourceLevel)
        {
            return HolyStoneCombinationEligibilityFailure.InvalidLevel;
        }
        if (firstMaterial.Grade != target.Grade ||
            secondMaterial.Grade != target.Grade ||
            thirdMaterial.Grade != target.Grade)
        {
            return HolyStoneCombinationEligibilityFailure.LevelMismatch;
        }

        plan = new HolyStoneCombinationPlan(
            target with
            {
                Grade = checked((short)(target.Grade + 1)),
                Bound = target.Bound != 0 ||
                    firstMaterial.Bound != 0 ||
                    secondMaterial.Bound != 0 ||
                    thirdMaterial.Bound != 0
                        ? (short)1
                        : (short)0
            },
            ConsumeOne(firstMaterial),
            ConsumeOne(secondMaterial),
            ConsumeOne(thirdMaterial));
        return HolyStoneCombinationEligibilityFailure.None;
    }

    public static bool IsHolyStone(uint itemId) =>
        itemId is
            HolyStoneUpgradePolicy.HeatedHolyStoneItemId or
            HolyStoneUpgradePolicy.CooledHolyStoneItemId;

    private static CompactItemEntry ConsumeOne(CompactItemEntry item) =>
        item.Stack == 1
            ? CompactItemEntry.Empty
            : item with { Stack = checked((short)(item.Stack - 1)) };
}
