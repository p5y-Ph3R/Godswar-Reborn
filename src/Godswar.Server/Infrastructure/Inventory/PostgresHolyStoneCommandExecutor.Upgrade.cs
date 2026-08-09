using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private static HolyStoneCommandResultStatus MissingUpgradeMaterialStatus(
        CompactItemEntry target)
    {
        if (!HolyStoneUpgradePolicy.IsHolyStone(target.Id) ||
            target.Grade is < HolyStoneUpgradePolicy.MinimumLevel or
                > HolyStoneUpgradePolicy.MaximumLevel)
        {
            return HolyStoneCommandResultStatus.TargetNotHolyStone;
        }
        if (target.Grade == HolyStoneUpgradePolicy.MaximumLevel)
        {
            return HolyStoneCommandResultStatus.MaximumStoneLevel;
        }
        return HolyStoneUpgradePolicy.RequiredEclipseStone(target.Grade)
            switch
            {
                9040 => HolyStoneCommandResultStatus.EclipseLevel1Missing,
                9041 => HolyStoneCommandResultStatus.EclipseLevel2Missing,
                9042 => HolyStoneCommandResultStatus.EclipseLevel3Missing,
                _ => throw new InvalidDataException(
                    "The Holy Stone Upgrade level has no Eclipse tier.")
            };
    }

    private HolyStonePlan PlanUpgrade(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        CompactItemEntry eclipseStone,
        CompactItemEntry catalyst)
    {
        var eligibility = HolyStoneUpgradePolicy.TryPrepare(
            target,
            eclipseStone,
            catalyst,
            out var attempt);
        if (eligibility != HolyStoneUpgradeEligibilityFailure.None)
        {
            return Rejected(
                context,
                MapUpgradeFailure(
                    eligibility,
                    target,
                    eclipseStone),
                target,
                eclipseStone);
        }

        var roll = _upgradeRandomSource.NextRoll();
        var resolution = attempt.Resolve(
            target,
            eclipseStone,
            catalyst,
            roll);
        return new HolyStonePlan(
            MapUpgradeOutcome(resolution.Outcome),
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            AlignImplementedStoneLevel(resolution.TargetAfter),
            resolution.EclipseStoneAfter,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0)
        {
            CatalystAfter = resolution.CatalystAfter,
            UpgradeRoll = resolution.Roll,
            UpgradeSuccessRate = resolution.SuccessRatePercent
        };
    }

    private static HolyStoneCommandResultStatus MapUpgradeFailure(
        HolyStoneUpgradeEligibilityFailure failure,
        CompactItemEntry target,
        CompactItemEntry eclipseStone) =>
        failure switch
        {
            HolyStoneUpgradeEligibilityFailure.TargetNotHolyStone or
            HolyStoneUpgradeEligibilityFailure.InvalidLevel =>
                HolyStoneCommandResultStatus.TargetNotHolyStone,
            HolyStoneUpgradeEligibilityFailure.MaximumLevel =>
                HolyStoneCommandResultStatus.MaximumStoneLevel,
            HolyStoneUpgradeEligibilityFailure.EclipseStone =>
                eclipseStone.Id is >= 9040 and <= 9042
                    ? MissingUpgradeMaterialStatus(target)
                    : HolyStoneCommandResultStatus.EclipseStoneRequired,
            HolyStoneUpgradeEligibilityFailure.SignetTransition or
            HolyStoneUpgradeEligibilityFailure.Catalyst =>
                HolyStoneCommandResultStatus.SignetMismatch,
            HolyStoneUpgradeEligibilityFailure
                .SignetProtectionUnavailable =>
                HolyStoneCommandResultStatus
                    .SignetProtectionUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

    private static HolyStoneCommandResultStatus MapUpgradeOutcome(
        HolyStoneUpgradeOutcome outcome) =>
        outcome switch
        {
            HolyStoneUpgradeOutcome.Succeeded =>
                HolyStoneCommandResultStatus.Upgraded,
            HolyStoneUpgradeOutcome.FailedDowngraded =>
                HolyStoneCommandResultStatus.UpgradeFailedDowngraded,
            HolyStoneUpgradeOutcome.FailedProtected =>
                HolyStoneCommandResultStatus.UpgradeFailedProtected,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}
