namespace Godswar.Server.State;

internal enum HolyStoneUpgradeEligibilityFailure : byte
{
    None = 0,
    TargetNotHolyStone = 1,
    InvalidLevel = 2,
    MaximumLevel = 3,
    EclipseStone = 4,
    Catalyst = 5,
    SignetTransition = 6,
    SignetProtectionUnavailable = 7
}

internal enum HolyStoneUpgradeOutcome : byte
{
    Succeeded = 1,
    FailedDowngraded = 2,
    FailedProtected = 3
}

internal readonly record struct HolyStoneUpgradeAttempt(
    short CurrentLevel,
    uint RequiredEclipseStoneId,
    int SuccessRatePercent,
    bool PreventsDowngrade)
{
    public HolyStoneUpgradeResolution Resolve(
        CompactItemEntry target,
        CompactItemEntry eclipseStone,
        CompactItemEntry catalyst,
        int roll)
    {
        if (roll is < 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(roll));
        }

        var succeeded = roll < SuccessRatePercent;
        var nextLevel = succeeded
            ? checked((short)(CurrentLevel + 1))
            : PreventsDowngrade
                ? CurrentLevel
                : checked((short)Math.Max(1, CurrentLevel - 1));
        var outcome = succeeded
            ? HolyStoneUpgradeOutcome.Succeeded
            : nextLevel == CurrentLevel
                ? HolyStoneUpgradeOutcome.FailedProtected
                : HolyStoneUpgradeOutcome.FailedDowngraded;
        return new HolyStoneUpgradeResolution(
            outcome,
            roll,
            SuccessRatePercent,
            target with { Grade = nextLevel },
            ConsumeOne(eclipseStone),
            catalyst.IsEmpty
                ? CompactItemEntry.Empty
                : ConsumeOne(catalyst));
    }

    private static CompactItemEntry ConsumeOne(CompactItemEntry item) =>
        item.Stack == 1
            ? CompactItemEntry.Empty
            : item with
            {
                Stack = checked((short)(item.Stack - 1))
            };
}

internal readonly record struct HolyStoneUpgradeResolution(
    HolyStoneUpgradeOutcome Outcome,
    int Roll,
    int SuccessRatePercent,
    CompactItemEntry TargetAfter,
    CompactItemEntry EclipseStoneAfter,
    CompactItemEntry CatalystAfter);

internal static class HolyStoneUpgradePolicy
{
    public const int MinimumLevel = 1;
    public const int MaximumLevel = 10;
    public const int GoddessBonusPercent = 10;
    public const int SignetBonusPercent = 10;
    public const uint HeatedHolyStoneItemId = 9030;
    public const uint CooledHolyStoneItemId = 9031;
    public const uint GoddessStoneItemId = 9050;

    public static HolyStoneUpgradeEligibilityFailure TryPrepare(
        CompactItemEntry target,
        CompactItemEntry eclipseStone,
        CompactItemEntry catalyst,
        out HolyStoneUpgradeAttempt attempt)
    {
        attempt = default;
        if (target.Id is not (
                HeatedHolyStoneItemId or CooledHolyStoneItemId) ||
            target.Stack != 1)
        {
            return HolyStoneUpgradeEligibilityFailure.TargetNotHolyStone;
        }
        if (target.Grade is < MinimumLevel or > MaximumLevel)
        {
            return HolyStoneUpgradeEligibilityFailure.InvalidLevel;
        }
        if (target.Grade == MaximumLevel)
        {
            return HolyStoneUpgradeEligibilityFailure.MaximumLevel;
        }

        var requiredEclipse = RequiredEclipseStone(target.Grade);
        if (eclipseStone.Id != requiredEclipse ||
            eclipseStone.Stack <= 0)
        {
            return HolyStoneUpgradeEligibilityFailure.EclipseStone;
        }

        var bonus = 0;
        var preventsDowngrade = false;
        if (!catalyst.IsEmpty)
        {
            if (catalyst.Stack <= 0)
            {
                return HolyStoneUpgradeEligibilityFailure.Catalyst;
            }
            if (catalyst.Id == GoddessStoneItemId)
            {
                bonus = GoddessBonusPercent;
            }
            else if (catalyst.Id == RequiredSignet(target.Grade))
            {
                bonus = SignetBonusPercent;
                preventsDowngrade = true;
            }
            else if (catalyst.Id is >= 9051 and <= 9056)
            {
                return target.Grade >= 7
                    ? HolyStoneUpgradeEligibilityFailure
                        .SignetProtectionUnavailable
                    : HolyStoneUpgradeEligibilityFailure.SignetTransition;
            }
            else
            {
                return HolyStoneUpgradeEligibilityFailure.Catalyst;
            }
        }

        attempt = new HolyStoneUpgradeAttempt(
            target.Grade,
            requiredEclipse,
            Math.Min(100, BaseRate(target.Grade) + bonus),
            preventsDowngrade);
        return HolyStoneUpgradeEligibilityFailure.None;
    }

    public static uint RequiredEclipseStone(short currentLevel) =>
        currentLevel switch
        {
            >= 1 and <= 3 => 9040,
            >= 4 and <= 6 => 9041,
            >= 7 and <= 9 => 9042,
            _ => 0
        };

    public static uint RequiredSignet(short currentLevel) =>
        currentLevel switch
        {
            4 => 9051,
            5 => 9052,
            6 => 9053,
            _ => 0
        };

    public static int BaseRate(short currentLevel) =>
        currentLevel switch
        {
            >= 1 and <= 3 => 90,
            >= 4 and <= 6 => 25,
            >= 7 and <= 9 => 10,
            _ => 0
        };
}
