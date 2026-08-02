namespace Godswar.Server.Application.Inventory;

internal enum HolySuitReceiptItemRole : byte
{
    HolyBox = 1,
    Equipment = 2,
    Ware = 3,
    ExperiencePrism = 4
}

internal readonly record struct HolySuitReceiptMutation(
    HolySuitReceiptItemRole Role,
    int KitBagSlot,
    uint ItemId,
    long ItemInstanceId,
    string BeforeCompactItemState,
    string AfterCompactItemState);
