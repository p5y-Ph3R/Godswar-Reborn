namespace Godswar.Server.Application.Inventory;

internal enum EquipmentForgeCommandResultStatus : byte
{
    Succeeded = 1,
    FailedRoll = 2,
    InvalidSelection = 3,
    StaleSelection = 4,
    InvalidForge = 5,
    InsufficientMaterials = 6,
    InsufficientSilver = 7
}
