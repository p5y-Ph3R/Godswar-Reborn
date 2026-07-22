namespace Godswar.Server.State;

internal sealed record ForgeSlotSelection(
    int KitBagSlot,
    CompactItemEntry ExpectedItem,
    int Quantity)
{
    public static ForgeSlotSelection Capture(string kitBag, int kitBagSlot, int quantity = 1)
    {
        return new ForgeSlotSelection(
            kitBagSlot,
            KitBagSlots.GetItem(kitBag, kitBagSlot),
            quantity);
    }
}

internal sealed record ForgeTransactionRequest(
    ForgeSlotSelection Equipment,
    ForgeSlotSelection PrimaryMaterial,
    ForgeSlotSelection? OddsMaterial,
    IReadOnlyList<ForgeSlotSelection>? AdditionalOddsMaterials = null)
{
    public IReadOnlyList<ForgeSlotSelection> OddsMaterials
    {
        get
        {
            if (OddsMaterial is null)
            {
                return AdditionalOddsMaterials ?? [];
            }

            if (AdditionalOddsMaterials is null or { Count: 0 })
            {
                return [OddsMaterial];
            }

            return [OddsMaterial, .. AdditionalOddsMaterials];
        }
    }
}

internal enum ForgeTransactionStatus
{
    Succeeded,
    FailedRoll,
    CharacterNotFound,
    InvalidSelection,
    StaleSelection,
    InvalidForge,
    InsufficientMaterials,
    InsufficientSilver
}

internal sealed record ForgeTransactionResult(
    ForgeTransactionStatus Status,
    GameCharacter? Character,
    int MaterialType,
    int Probability,
    int SilverSpent,
    CompactItemEntry EquipmentBefore,
    CompactItemEntry EquipmentAfter,
    string? RejectionReason = null)
{
    public bool Committed => Status is ForgeTransactionStatus.Succeeded or ForgeTransactionStatus.FailedRoll;

    public bool Succeeded => Status == ForgeTransactionStatus.Succeeded;
}
