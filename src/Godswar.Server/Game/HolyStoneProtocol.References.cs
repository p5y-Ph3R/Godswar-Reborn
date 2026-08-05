using Godswar.Server.Application.Inventory;

namespace Godswar.Server.Game;

internal static partial class HolyStoneProtocol
{
    public static int EncodeKitBagReference(int slot)
    {
        if (slot is
            < HolyStoneCommandEnvelope.MinimumKitBagSlot or
            > HolyStoneCommandEnvelope.MaximumKitBagSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var page = slot / ClientKitBagSlotsPerPage;
        var pageSlot = slot % ClientKitBagSlotsPerPage;
        return checked((page * ClientKitBagPageStride) + pageSlot);
    }

    private static bool TryDecodeKitBagReference(
        int reference,
        out int slot)
    {
        if (reference < 0)
        {
            slot = -1;
            return false;
        }

        var page = reference / ClientKitBagPageStride;
        var pageSlot = reference % ClientKitBagPageStride;
        if (page is < 0 or >= ClientKitBagPageCount ||
            pageSlot is < 0 or >= ClientKitBagSlotsPerPage)
        {
            slot = -1;
            return false;
        }

        slot = checked((page * ClientKitBagSlotsPerPage) + pageSlot);
        return slot is
            >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
            <= HolyStoneCommandEnvelope.MaximumKitBagSlot;
    }

    private static bool OnlyArgumentsUsed(
        IReadOnlyList<int> args,
        params int[] usedIndexes)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (usedIndexes.Contains(index))
            {
                continue;
            }
            if (args[index] != -1)
            {
                return false;
            }
        }

        return true;
    }
}
