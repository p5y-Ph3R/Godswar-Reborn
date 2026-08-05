using Godswar.Server.Application.Items;
using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.State;

internal static partial class HolyStoneItemMutator
{
    private const int BasicMaximumSockets = 2;

    public static bool TryGetDrillGoldCost(
        IItemTemplateCatalog templates,
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        out int goldCost)
    {
        return TryEvaluateDrill(
                templates,
                equipment,
                kitBag,
                profession,
                HolyStoneOperation.DrillSocket,
                targetMode,
                targetKitBagSlot,
                stoneKitBagSlot: -1,
                out var eligibility,
                out goldCost) &&
            eligibility == HolyStoneDrillEligibilityFailure.None;
    }

    public static bool TryEvaluateDrill(
        IItemTemplateCatalog templates,
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int stoneKitBagSlot,
        out HolyStoneDrillEligibilityFailure eligibility,
        out int goldCost)
    {
        eligibility = HolyStoneDrillEligibilityFailure.None;
        goldCost = 0;
        if (operation is not (
                HolyStoneOperation.DrillSocket or
                HolyStoneOperation.AdvancedDrillSocket) ||
            !TryGetTarget(
                templates,
                equipment,
                kitBag,
                profession,
                targetMode,
                targetKitBagSlot,
                allowNormalCharacterGear: true,
                out var target) ||
            !templates.TryGet(target.Item.Id, out var template))
        {
            return false;
        }

        if (operation == HolyStoneOperation.DrillSocket)
        {
            eligibility = HolyStoneDrillEligibilityPolicy.ValidateBasic(
                template,
                target.Item);
            return eligibility != HolyStoneDrillEligibilityFailure.None ||
                HolyStoneDrillCostPolicy.TryGetGoldCost(
                    target.Item.SocketCount,
                    out goldCost);
        }

        if (target.IsKitBag && target.Slot == stoneKitBagSlot)
        {
            eligibility = HolyStoneDrillEligibilityFailure.SocketSpell;
            return true;
        }

        var socketSpell = IsKitBagSlot(stoneKitBagSlot)
            ? KitBagSlots.GetItem(kitBag, stoneKitBagSlot)
            : CompactItemEntry.Empty;
        eligibility = HolyStoneDrillEligibilityPolicy.ValidateAdvanced(
            template,
            target.Item,
            socketSpell);
        return true;
    }

    private static bool TryDrill(
        IItemTemplateCatalog templates,
        ref CompactItemEntry item,
        out string summary)
    {
        if (!templates.TryGet(item.Id, out var template))
        {
            summary = "target item template is missing";
            return false;
        }

        var failure = HolyStoneDrillEligibilityPolicy.ValidateBasic(
            template,
            item);
        if (failure != HolyStoneDrillEligibilityFailure.None)
        {
            summary = DescribeDrillFailure(failure);
            return false;
        }

        var current = Math.Clamp(
            item.SocketCount,
            (short)0,
            (short)BasicMaximumSockets);
        item = item with { SocketCount = (short)(current + 1) };
        summary = $"drilled socket={current + 1}";
        return true;
    }

    private static bool TryAdvancedDrill(
        IItemTemplateCatalog templates,
        string kitBag,
        ref CompactItemEntry item,
        int stoneKitBagSlot,
        out string updatedKitBag,
        out string summary)
    {
        updatedKitBag = kitBag;
        if (!templates.TryGet(item.Id, out var template))
        {
            summary = "target item template is missing";
            return false;
        }

        var socketSpell = IsKitBagSlot(stoneKitBagSlot)
            ? KitBagSlots.GetItem(kitBag, stoneKitBagSlot)
            : CompactItemEntry.Empty;
        var failure = HolyStoneDrillEligibilityPolicy.ValidateAdvanced(
            template,
            item,
            socketSpell);
        if (failure != HolyStoneDrillEligibilityFailure.None)
        {
            summary = DescribeDrillFailure(failure);
            return false;
        }

        var socketIndex = item.SocketCount;
        item = item with
        {
            SocketCount = checked((short)(socketIndex + 1))
        };
        updatedKitBag = socketSpell.Stack == 1
            ? KitBagSlots.ClearSlot(updatedKitBag, stoneKitBagSlot)
            : KitBagSlots.SetSlot(
                updatedKitBag,
                stoneKitBagSlot,
                (socketSpell with
                {
                    Stack = checked((short)(socketSpell.Stack - 1))
                }).ToCompactString());
        summary =
            $"advanced drilled socket={socketIndex + 1} " +
            $"spell={socketSpell.Id} spellSlot={stoneKitBagSlot}";
        return true;
    }

    private static string DescribeDrillFailure(
        HolyStoneDrillEligibilityFailure failure) =>
        failure switch
        {
            HolyStoneDrillEligibilityFailure.MaximumSockets =>
                "maximum drill count reached",
            HolyStoneDrillEligibilityFailure.SocketSpell =>
                "corresponding Socket Spell is required",
            HolyStoneDrillEligibilityFailure.ItemLevel =>
                "target item level does not meet the socket requirement",
            HolyStoneDrillEligibilityFailure.SocketPrerequisite =>
                "previous sockets must be drilled first",
            HolyStoneDrillEligibilityFailure.FourthSocketEquipment =>
                "fourth socket requires Orichalcum 1, Arcane quality, and Grade 20",
            _ => "drill requirements are not met"
        };
}
