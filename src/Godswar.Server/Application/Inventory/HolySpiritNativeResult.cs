using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.Application.Inventory;

internal static class HolySpiritNativeResult
{
    private const int ResultKindScale = 100;
    private const int EffectScale = 10_000;
    private const int ValueScale = 1_000_000;
    private const int SuccessSuffix = 3;
    private const int MountIncompatibleSuffix = 4;
    private const int ImplementIncompatibleSuffix = 5;

    public static int GetResultSubId(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        string targetBeforeState,
        string targetAfterState,
        string materialBeforeState)
    {
        var targetBefore = HolyStoneCompactItemEvidence.Parse(
            targetBeforeState);
        var targetAfter = HolyStoneCompactItemEvidence.Parse(
            targetAfterState);
        var materialBefore = HolyStoneCompactItemEvidence.Parse(
            materialBeforeState);
        if (operation == HolyStoneCommandOperation.ImplementSpirit)
        {
            if (status == HolyStoneCommandResultStatus.SpiritImplemented &&
                TryEncodeSuccess(targetAfter, out var success))
            {
                return success;
            }
            if (status == HolyStoneCommandResultStatus.IncompatibleTarget &&
                IsHolyStone(targetBefore.Id))
            {
                return checked(
                    ((int)targetBefore.Id * ResultKindScale) +
                    ImplementIncompatibleSuffix);
            }
        }
        else if (operation == HolyStoneCommandOperation.Mount &&
                 status == HolyStoneCommandResultStatus.IncompatibleTarget &&
                 IsHolyStone(materialBefore.Id))
        {
            return checked(
                ((int)materialBefore.Id * ResultKindScale) +
                MountIncompatibleSuffix);
        }

        return HolyStoneNativeResults.GetResultSubId(operation, status);
    }

    public static bool IsValid(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        int nativeResultSubId,
        string targetBeforeState,
        string targetAfterState,
        string materialBeforeState) =>
        nativeResultSubId == GetResultSubId(
            operation,
            status,
            targetBeforeState,
            targetAfterState,
            materialBeforeState);

    private static bool TryEncodeSuccess(
        HolyStoneCompactItemEvidence implementedStone,
        out int resultSubId)
    {
        resultSubId = 0;
        if (!IsHolyStone(implementedStone.Id) ||
            implementedStone.SocketCount != 1 ||
            implementedStone.Socket1EffectId is not { } effectId ||
            implementedStone.Socket1Level is not
                (>= HolySpiritImplementationPolicy.MinimumHolyStoneGrade and
                 <= HolySpiritImplementationPolicy.MaximumHolyStoneGrade) ||
            implementedStone.Socket1Value is not (> 0) ||
            !HolySpiritImplementationPolicy.All.Any(definition =>
                definition.EffectId == effectId))
        {
            return false;
        }

        resultSubId = checked(
            (implementedStone.Socket1Value.Value * ValueScale) +
            (effectId * EffectScale) +
            ((implementedStone.Socket1Level.Value - 1) *
                ResultKindScale) +
            SuccessSuffix);
        return true;
    }

    private static bool IsHolyStone(uint itemId) =>
        itemId is
            HolySpiritImplementationPolicy.HeatedHolyStoneItemId or
            HolySpiritImplementationPolicy.CooledHolyStoneItemId;
}
