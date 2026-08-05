namespace Godswar.Server.Application.Inventory;

internal enum HolyStoneCommandOperation : byte
{
    Mount = 1,
    Remove = 2,
    Drill = 3,
    AdvancedDrill = 4,
    Upgrade = 5,
    Combine = 6,
    ImplementSpirit = 7
}

internal enum HolyStoneTargetLocation : byte
{
    Equipment = 0,
    KitBag = 1
}
