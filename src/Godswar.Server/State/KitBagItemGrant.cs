namespace Godswar.Server.State;

internal enum KitBagItemGrantStatus
{
    Added,
    CharacterNotFound,
    InsufficientCapacity
}

internal sealed record KitBagItemGrantResult(
    KitBagItemGrantStatus Status,
    GameCharacter? Character)
{
    public bool Added => Status == KitBagItemGrantStatus.Added && Character is not null;
}

internal static class KitBagItemGrantPlanner
{
    public const int SlotCount = 96;
    public const int MaximumQuantity = 999;

    public static bool TryAdd(
        string kitBag,
        uint itemId,
        int quantity,
        short stackCap,
        short bound,
        out string updatedKitBag)
    {
        if (itemId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemId));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if (stackCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stackCap));
        }

        if (bound is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bound));
        }

        var slots = Enumerable.Range(0, SlotCount)
            .Select(slot => KitBagSlots.GetItem(kitBag, slot))
            .ToArray();
        long capacity = 0;
        foreach (var item in slots)
        {
            if (item.IsEmpty)
            {
                capacity += stackCap;
            }
            else if (item.Id == itemId && item.Bound == bound)
            {
                capacity += Math.Max(0, stackCap - item.Stack);
            }
        }

        if (capacity < quantity)
        {
            updatedKitBag = kitBag;
            return false;
        }

        var remaining = quantity;
        updatedKitBag = kitBag;
        for (var slot = 0; slot < slots.Length && remaining > 0; slot++)
        {
            var item = slots[slot];
            if (item.IsEmpty || item.Id != itemId || item.Bound != bound || item.Stack >= stackCap)
            {
                continue;
            }

            var added = Math.Min(remaining, stackCap - item.Stack);
            item = item with { Stack = checked((short)(item.Stack + added)) };
            updatedKitBag = KitBagSlots.SetSlot(updatedKitBag, slot, item.ToCompactString());
            remaining -= added;
        }

        for (var slot = 0; slot < slots.Length && remaining > 0; slot++)
        {
            if (!slots[slot].IsEmpty)
            {
                continue;
            }

            var stack = Math.Min(remaining, stackCap);
            var item = CompactItemEntry.Parse($"[{itemId},,,,,,1,1,{bound},{stack},0]");
            updatedKitBag = KitBagSlots.SetSlot(updatedKitBag, slot, item.ToCompactString());
            remaining -= stack;
        }

        return remaining == 0;
    }
}
