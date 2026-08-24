namespace Godswar.Server.Application.Warehouse;

/// <summary>
/// Structural bounds for the character-owned normal warehouse. Concrete
/// expansion levels and costs come from the startup-pinned database policy.
/// </summary>
internal static class WarehouseCapacityPolicy
{
    public const int SlotsPerBox = 40;
    public const int DefaultCapacity = SlotsPerBox;
    public const int MaximumSupportedBoxCount = 9;
    public const int MaximumSupportedCapacity =
        SlotsPerBox * MaximumSupportedBoxCount;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumTransferMutations =
        MaximumSupportedCapacity + 1;
    public const int AutomaticKitBagSlot = -1;
    public const int AutomaticWarehouseSlot = -1;
    private const int ManagerStateBase = 100_000;
    private const int ManagerMissingKeysBase = 900_000;

    public static bool IsValidCapacity(int capacity) =>
        capacity is >= DefaultCapacity and <= MaximumSupportedCapacity &&
        capacity % SlotsPerBox == 0;

    public static bool IsValidWarehouseSlot(int slot) =>
        slot is >= 0 and < MaximumSupportedCapacity;

    public static bool IsOpenWarehouseSlot(int slot, int capacity) =>
        IsValidCapacity(capacity) && slot >= 0 && slot < capacity;

    public static bool IsValidKitBagSlot(int slot) =>
        slot is >= MinimumKitBagSlot and <= MaximumKitBagSlot;

    public static int NextCapacity(int currentCapacity) =>
        checked(currentCapacity + SlotsPerBox) is var next &&
        IsValidCapacity(next)
            ? next
            : throw new ArgumentOutOfRangeException(nameof(currentCapacity));

    public static int BoxNumber(int capacity) =>
        IsValidCapacity(capacity)
            ? capacity / SlotsPerBox
            : throw new ArgumentOutOfRangeException(nameof(capacity));

    public static int StateSubId(
        int capacity,
        int maximumCapacity,
        int nextKeyCost)
    {
        if (!IsValidCapacity(capacity) ||
            !IsValidCapacity(maximumCapacity) ||
            capacity > maximumCapacity ||
            nextKeyCost is < 0 or >
                WarehouseExpansionPolicySnapshot.MaximumKeyCost)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        return capacity == maximumCapacity
            ? 998
            : checked(
                ManagerStateBase + BoxNumber(capacity) * 100 + nextKeyCost);
    }

    public static int SuccessSubId(int capacity) =>
        IsValidCapacity(capacity) && capacity > DefaultCapacity
            ? checked(199 + BoxNumber(capacity))
            : throw new ArgumentOutOfRangeException(nameof(capacity));

    public static int InsufficientKeysSubId(
        int targetCapacity,
        int requiredKeyCount) =>
        IsValidCapacity(targetCapacity) &&
        targetCapacity > DefaultCapacity &&
        requiredKeyCount is >= 1 and <=
            WarehouseExpansionPolicySnapshot.MaximumKeyCost
            ? checked(
                ManagerMissingKeysBase +
                BoxNumber(targetCapacity) * 100 +
                requiredKeyCount)
            : throw new ArgumentOutOfRangeException(
                nameof(targetCapacity));

    public static bool TryDecodeManagerState(
        int subId,
        out int currentBox,
        out int nextKeyCost)
    {
        currentBox = 0;
        nextKeyCost = 0;
        if (subId < ManagerStateBase)
        {
            return false;
        }

        var encoded = subId - ManagerStateBase;
        currentBox = encoded / 100;
        nextKeyCost = encoded % 100;
        return currentBox is >= 1 and < MaximumSupportedBoxCount &&
            nextKeyCost is >= 1 and <=
                WarehouseExpansionPolicySnapshot.MaximumKeyCost;
    }
}
