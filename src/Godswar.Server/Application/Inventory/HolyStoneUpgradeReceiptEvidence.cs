using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.Application.Inventory;

internal static class HolyStoneUpgradeReceiptEvidence
{
    public static void Validate(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        string targetBefore,
        string targetAfter,
        string eclipseBefore,
        string eclipseAfter,
        int catalystSlot,
        long? catalystItemInstanceId,
        string expectedCatalyst,
        string catalystBefore,
        string catalystAfter,
        int? roll,
        int? successRate)
    {
        if (operation == HolyStoneCommandOperation.Combine)
        {
            if (roll.HasValue || successRate.HasValue)
            {
                throw new ArgumentException(
                    "Combination cannot contain Upgrade roll evidence.");
            }
            return;
        }

        if (operation == HolyStoneCommandOperation.ImplementSpirit)
        {
            if (roll.HasValue || successRate.HasValue)
            {
                throw new ArgumentException(
                    "Holy Spirit implementation cannot contain Upgrade " +
                    "roll evidence.");
            }
            return;
        }

        if (operation != HolyStoneCommandOperation.Upgrade)
        {
            if (catalystSlot != HolyStoneCommandEnvelope.NoStoneKitBagSlot ||
                catalystItemInstanceId.HasValue ||
                expectedCatalyst != "[]" ||
                catalystBefore != "[]" ||
                catalystAfter != "[]" ||
                roll.HasValue ||
                successRate.HasValue)
            {
                throw new ArgumentException(
                    "Only Upgrade may contain catalyst or roll evidence.");
            }
            return;
        }

        var catalystSelected = catalystSlot is
            >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
            <= HolyStoneCommandEnvelope.MaximumKitBagSlot;
        var catalystPresent = catalystItemInstanceId.HasValue;
        if ((!catalystSelected &&
             (catalystPresent ||
              expectedCatalyst != "[]" ||
              catalystBefore != "[]" ||
              catalystAfter != "[]")) ||
            (catalystSelected && expectedCatalyst == "[]") ||
            catalystPresent != (catalystBefore != "[]"))
        {
            throw new ArgumentException(
                "The Upgrade catalyst evidence is inconsistent.");
        }

        var committed = HolyStoneNativeResults.IsSuccess(status);
        if (committed != roll.HasValue ||
            committed != successRate.HasValue ||
            committed && catalystSelected && !catalystPresent)
        {
            throw new ArgumentException(
                "Only a committed Upgrade may contain random evidence.");
        }
        if (!committed)
        {
            if (targetAfter != targetBefore ||
                eclipseAfter != eclipseBefore ||
                catalystAfter != catalystBefore)
            {
                throw new ArgumentException(
                    "A rejected Upgrade cannot mutate item evidence.");
            }
            return;
        }

        var target = HolyStoneCompactItemEvidence.Parse(targetBefore);
        var eclipse = HolyStoneCompactItemEvidence.Parse(eclipseBefore);
        var catalyst = HolyStoneCompactItemEvidence.Parse(catalystBefore);
        if (!TryPrepare(
                target,
                eclipse,
                catalyst,
                out var effectiveRate,
                out var preventsDowngrade) ||
            roll is not (>= 0 and < 100) ||
            successRate != effectiveRate)
        {
            throw new ArgumentException(
                "The committed Upgrade policy evidence is invalid.");
        }

        var succeeded = roll.Value < effectiveRate;
        var nextLevel = succeeded
            ? checked((short)(target.Grade + 1))
            : preventsDowngrade
                ? target.Grade
                : checked((short)Math.Max(1, target.Grade - 1));
        var expectedStatus = succeeded
            ? HolyStoneCommandResultStatus.Upgraded
            : nextLevel == target.Grade
                ? HolyStoneCommandResultStatus.UpgradeFailedProtected
                : HolyStoneCommandResultStatus.UpgradeFailedDowngraded;
        if (status != expectedStatus ||
            targetAfter != target.WithGrade(nextLevel) ||
            eclipseAfter != eclipse.ConsumeOne() ||
            catalystAfter != (catalyst.IsEmpty
                ? "[]"
                : catalyst.ConsumeOne()))
        {
            throw new ArgumentException(
                "The stored Upgrade outcome does not match its roll.");
        }
    }

    private static bool TryPrepare(
        HolyStoneCompactItemEvidence target,
        HolyStoneCompactItemEvidence eclipse,
        HolyStoneCompactItemEvidence catalyst,
        out int successRate,
        out bool preventsDowngrade)
    {
        successRate = 0;
        preventsDowngrade = false;
        if (!HolySpiritImplementationPolicy.IsHolyStoneItem(target.Id) ||
            target.Stack != 1 ||
            target.Grade is < 1 or >= 10 ||
            eclipse.Id != RequiredEclipse(target.Grade) ||
            eclipse.Stack <= 0)
        {
            return false;
        }

        var bonus = 0;
        if (!catalyst.IsEmpty)
        {
            if (catalyst.Stack <= 0)
            {
                return false;
            }
            if (catalyst.Id == 9050)
            {
                bonus = 10;
            }
            else if (catalyst.Id == RequiredSignet(target.Grade))
            {
                bonus = 10;
                preventsDowngrade = true;
            }
            else
            {
                return false;
            }
        }

        successRate = Math.Min(100, BaseRate(target.Grade) + bonus);
        return true;
    }

    private static uint RequiredEclipse(short level) =>
        level switch
        {
            >= 1 and <= 3 => 9040,
            >= 4 and <= 6 => 9041,
            >= 7 and <= 9 => 9042,
            _ => 0
        };

    private static uint RequiredSignet(short level) =>
        level switch
        {
            4 => 9051,
            5 => 9052,
            6 => 9053,
            _ => 0
        };

    private static int BaseRate(short level) =>
        level switch
        {
            >= 1 and <= 3 => 90,
            >= 4 and <= 6 => 25,
            >= 7 and <= 9 => 10,
            _ => 0
        };
}
