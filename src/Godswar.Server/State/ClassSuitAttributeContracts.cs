namespace Godswar.Server.State;

internal enum ClassSuitAttributeOperation : short
{
    AddClassSpecific = 101,
    DeleteClassSpecific = 102
}

internal enum ClassSuitAttributeStatus
{
    Succeeded,
    RequestMissing,
    UnsupportedOperation,
    InvalidProfession,
    InvalidKitBagSlot,
    DuplicateKitBagSlot,
    SelectionMissing,
    StaleSelection,
    InvalidWeapon,
    ProfessionMismatch,
    InvalidCatalyst,
    InvalidClassStone,
    InsufficientMaterial,
    InvalidAttributeState,
    ClassAttributeAlreadyPresent,
    AttributeSlotsFull,
    ClassAttributeMissing
}

internal sealed record ClassSuitAttributeRequest(
    ClassSuitAttributeOperation Operation,
    ClassSuitSlotSelection Gear,
    ClassSuitSlotSelection Catalyst,
    ClassSuitSlotSelection? ClassStone = null);

internal sealed record ClassSuitAttributeResult(
    ClassSuitAttributeStatus Status,
    ClassSuitAttributeOperation? Operation,
    string OriginalKitBag,
    string UpdatedKitBag,
    CompactItemEntry EquipmentBefore,
    CompactItemEntry EquipmentAfter,
    IReadOnlyList<ClassSuitSlotMutation> Mutations,
    IReadOnlyList<ClassSuitMaterialChange> Materials,
    string? RejectionReason = null)
{
    public bool Committed => Status == ClassSuitAttributeStatus.Succeeded;
}
