namespace Godswar.Server.Game;

internal static partial class HolySuitDesignProtocol
{
    public const int ClientKitBagPageCount = 4;
    public const int ClientKitBagSlotsPerPage = 24;
    public const int ClientKitBagPageStride = 100;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot =
        (ClientKitBagPageCount * ClientKitBagSlotsPerPage) - 1;

    public static bool TryDecodeKitBagReference(
        int reference,
        out int kitBagSlot)
    {
        kitBagSlot = NoKitBagSlot;
        if (reference < 0)
        {
            return false;
        }

        var page = Math.DivRem(
            reference,
            ClientKitBagPageStride,
            out var pageSlot);
        if (page is < 0 or >= ClientKitBagPageCount ||
            pageSlot is < 0 or >= ClientKitBagSlotsPerPage)
        {
            return false;
        }

        kitBagSlot = checked(
            (page * ClientKitBagSlotsPerPage) + pageSlot);
        return true;
    }

    public static bool TryEncodeKitBagReference(
        int kitBagSlot,
        out int reference)
    {
        if (kitBagSlot is < MinimumKitBagSlot or > MaximumKitBagSlot)
        {
            reference = -1;
            return false;
        }

        var page = Math.DivRem(
            kitBagSlot,
            ClientKitBagSlotsPerPage,
            out var pageSlot);
        reference = checked(
            (page * ClientKitBagPageStride) + pageSlot);
        return true;
    }
}
