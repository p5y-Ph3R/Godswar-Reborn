using System.Text;

namespace Godswar.Server.Application.Inventory;

internal sealed record HolyStoneCombinationReceiptEvidence(
    int ThirdMaterialKitBagSlot,
    long? ThirdMaterialItemInstanceId,
    string ExpectedThirdMaterialCompactItemState,
    string AuthoritativeThirdMaterialBeforeCompactItemState,
    string AuthoritativeThirdMaterialAfterCompactItemState)
{
    public static void Validate(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        long? targetItemInstanceId,
        string expectedTarget,
        string targetBefore,
        string targetAfter,
        int firstMaterialSlot,
        long? firstMaterialItemInstanceId,
        string expectedFirstMaterial,
        string firstMaterialBefore,
        string firstMaterialAfter,
        int secondMaterialSlot,
        long? secondMaterialItemInstanceId,
        string expectedSecondMaterial,
        string secondMaterialBefore,
        string secondMaterialAfter,
        HolyStoneCombinationReceiptEvidence? thirdMaterial)
    {
        if (operation != HolyStoneCommandOperation.Combine)
        {
            if (thirdMaterial is not null)
            {
                throw new ArgumentException(
                    "Only Combination may contain third-material " +
                    "evidence.");
            }
            return;
        }

        if (thirdMaterial is null ||
            targetLocation != HolyStoneTargetLocation.KitBag ||
            !IsKitBagSlot(targetSlot) ||
            !IsKitBagSlot(firstMaterialSlot) ||
            !IsKitBagSlot(secondMaterialSlot) ||
            !IsKitBagSlot(thirdMaterial.ThirdMaterialKitBagSlot) ||
            !AreDistinct(
                targetSlot,
                firstMaterialSlot,
                secondMaterialSlot,
                thirdMaterial.ThirdMaterialKitBagSlot) ||
            !IsBoundedNonEmptyState(expectedTarget) ||
            !IsBoundedNonEmptyState(expectedFirstMaterial) ||
            !IsBoundedNonEmptyState(expectedSecondMaterial) ||
            !IsValidInstanceId(
                thirdMaterial.ThirdMaterialItemInstanceId) ||
            !IsBoundedNonEmptyState(
                thirdMaterial.ExpectedThirdMaterialCompactItemState) ||
            !IsBoundedState(
                thirdMaterial
                    .AuthoritativeThirdMaterialBeforeCompactItemState) ||
            !IsBoundedState(
                thirdMaterial
                    .AuthoritativeThirdMaterialAfterCompactItemState))
        {
            throw new ArgumentException(
                "The Combination third-material evidence is invalid.");
        }

        var thirdPresent =
            thirdMaterial.ThirdMaterialItemInstanceId.HasValue;
        if (targetItemInstanceId.HasValue != (targetBefore != "[]") ||
            firstMaterialItemInstanceId.HasValue !=
                (firstMaterialBefore != "[]") ||
            secondMaterialItemInstanceId.HasValue !=
                (secondMaterialBefore != "[]") ||
            thirdPresent !=
            (thirdMaterial
                .AuthoritativeThirdMaterialBeforeCompactItemState != "[]"))
        {
            throw new ArgumentException(
                "The Combination third-material identity is inconsistent.");
        }

        if (status != HolyStoneCommandResultStatus.Combined)
        {
            if (targetAfter != targetBefore ||
                firstMaterialAfter != firstMaterialBefore ||
                secondMaterialAfter != secondMaterialBefore ||
                thirdMaterial
                    .AuthoritativeThirdMaterialAfterCompactItemState !=
                thirdMaterial
                    .AuthoritativeThirdMaterialBeforeCompactItemState)
            {
                throw new ArgumentException(
                    "A rejected Combination cannot mutate item evidence.");
            }
            return;
        }

        if (!targetItemInstanceId.HasValue ||
            !firstMaterialItemInstanceId.HasValue ||
            !secondMaterialItemInstanceId.HasValue ||
            !thirdPresent ||
            expectedTarget != targetBefore ||
            expectedFirstMaterial != firstMaterialBefore ||
            expectedSecondMaterial != secondMaterialBefore ||
            thirdMaterial.ExpectedThirdMaterialCompactItemState !=
                thirdMaterial
                    .AuthoritativeThirdMaterialBeforeCompactItemState)
        {
            throw new ArgumentException(
                "A committed Combination requires four unchanged source " +
                "identities.");
        }

        var target = HolyStoneCompactItemEvidence.Parse(targetBefore);
        var first = HolyStoneCompactItemEvidence.Parse(
            firstMaterialBefore);
        var second = HolyStoneCompactItemEvidence.Parse(
            secondMaterialBefore);
        var third = HolyStoneCompactItemEvidence.Parse(
            thirdMaterial
                .AuthoritativeThirdMaterialBeforeCompactItemState);
        var propagatedBound =
            target.Bound != 0 || first.Bound != 0 ||
            second.Bound != 0 || third.Bound != 0
                ? (short)1
                : (short)0;
        if (!IsHolyStone(target.Id) ||
            !IsHolyStone(first.Id) ||
            !IsHolyStone(second.Id) ||
            !IsHolyStone(third.Id) ||
            target.Stack != 1 ||
            first.Stack < 1 || second.Stack < 1 || third.Stack < 1 ||
            target.Grade is < 4 or > 9 ||
            first.Grade != target.Grade ||
            second.Grade != target.Grade ||
            third.Grade != target.Grade ||
            targetAfter != target.WithGradeAndBound(
                checked((short)(target.Grade + 1)),
                propagatedBound) ||
            firstMaterialAfter != first.ConsumeOne() ||
            secondMaterialAfter != second.ConsumeOne() ||
            thirdMaterial.AuthoritativeThirdMaterialAfterCompactItemState !=
                third.ConsumeOne())
        {
            throw new ArgumentException(
                "The stored Combination outcome does not match policy.");
        }
    }

    private static bool IsKitBagSlot(int slot) =>
        slot is
            >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
            <= HolyStoneCommandEnvelope.MaximumKitBagSlot;

    private static bool IsHolyStone(uint itemId) =>
        itemId is 9030 or 9031;

    private static bool IsValidInstanceId(long? value) =>
        !value.HasValue || value.Value > 0;

    private static bool AreDistinct(
        int target,
        int first,
        int second,
        int third) =>
        target != first && target != second && target != third &&
        first != second && first != third && second != third;

    private static bool IsBoundedNonEmptyState(string? value) =>
        value != "[]" && IsBoundedState(value);

    private static bool IsBoundedState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']')
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(value) <=
            HolyStoneCommandEnvelope.MaximumCompactItemStateUtf8Bytes;
    }
}
