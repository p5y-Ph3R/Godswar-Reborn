using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private static HolyStonePlan PlanCombination(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        CompactItemEntry firstMaterial,
        CompactItemEntry secondMaterial,
        CompactItemEntry thirdMaterial)
    {
        var eligibility = HolyStoneCombinationPolicy.TryPrepare(
            target,
            firstMaterial,
            secondMaterial,
            thirdMaterial,
            out var combination);
        if (eligibility != HolyStoneCombinationEligibilityFailure.None)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.CombinationNotAllowed,
                target,
                firstMaterial);
        }

        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Combined,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            AlignImplementedStoneLevel(combination.TargetAfter),
            combination.FirstMaterialAfter,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0)
        {
            CatalystAfter = combination.SecondMaterialAfter,
            ThirdMaterialAfter = combination.ThirdMaterialAfter
        };
    }
}
