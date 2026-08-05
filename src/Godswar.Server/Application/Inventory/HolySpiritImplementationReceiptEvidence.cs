using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.Application.Inventory;

internal static class HolySpiritImplementationReceiptEvidence
{
    public static void Validate(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        string targetBeforeState,
        string targetAfterState,
        string spiritBeforeState,
        string spiritAfterState,
        int catalystSlot,
        long? catalystItemInstanceId,
        string expectedCatalystState,
        string catalystBeforeState,
        string catalystAfterState)
    {
        if (operation != HolyStoneCommandOperation.ImplementSpirit)
        {
            return;
        }

        var catalystSelected = catalystSlot is
            >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
            <= HolyStoneCommandEnvelope.MaximumKitBagSlot;
        if (catalystSelected != (expectedCatalystState != "[]") ||
            catalystItemInstanceId.HasValue !=
                (catalystBeforeState != "[]"))
        {
            throw new ArgumentException(
                "Holy Spirit catalyst evidence is inconsistent.");
        }

        var committed =
            status == HolyStoneCommandResultStatus.SpiritImplemented;
        if (!committed)
        {
            if (targetAfterState != targetBeforeState ||
                spiritAfterState != spiritBeforeState ||
                catalystAfterState != catalystBeforeState)
            {
                throw new ArgumentException(
                    "A rejected Holy Spirit operation mutated evidence.");
            }
            return;
        }

        var targetBefore = HolyStoneCompactItemEvidence.Parse(
            targetBeforeState);
        var targetAfter = HolyStoneCompactItemEvidence.Parse(
            targetAfterState);
        var spiritBefore = HolyStoneCompactItemEvidence.Parse(
            spiritBeforeState);
        var catalystBefore = HolyStoneCompactItemEvidence.Parse(
            catalystBeforeState);
        var catalystAfter = HolyStoneCompactItemEvidence.Parse(
            catalystAfterState);
        if (targetBefore.Id is not (
                HolySpiritImplementationPolicy.HeatedHolyStoneItemId or
                HolySpiritImplementationPolicy.CooledHolyStoneItemId) ||
            targetBefore.Stack != 1 ||
            targetAfter.Id != targetBefore.Id ||
            targetAfter.Grade != targetBefore.Grade ||
            targetAfter.SocketCount != 1 ||
            targetAfter.Socket1EffectId is not { } effectId ||
            targetAfter.Socket1Level != targetAfter.Grade ||
            targetAfter.Socket1Value is not { } effectiveness ||
            !HolySpiritImplementationPolicy.TryGetDefinition(
                spiritBefore.Id,
                out var definition) ||
            definition.EffectId != effectId ||
            !HolySpiritImplementationPolicy.IsCompatibleWithHolyStone(
                spiritBefore.Id,
                targetBefore.Id) ||
            !HolySpiritImplementationPolicy.TryGetGradeBracket(
                spiritBefore.Id,
                targetBefore.Grade,
                out var minimum,
                out var maximum) ||
            effectiveness < minimum ||
            effectiveness > maximum ||
            spiritAfterState != spiritBefore.ConsumeOne() ||
            catalystSelected &&
            (catalystBefore.Id != 9050 ||
             catalystAfterState !=
                catalystBefore.ConsumeOne()) ||
            !catalystSelected && !catalystAfter.IsEmpty)
        {
            throw new ArgumentException(
                "Committed Holy Spirit evidence violates policy.");
        }
    }

}
