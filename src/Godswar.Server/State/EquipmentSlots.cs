namespace Godswar.Server.State;

internal static class EquipmentSlots
{
    public const int Head = 0;
    public const int Amulet = 1;
    public const int Glove = 2;
    public const int Armor = 3;
    public const int Cuff = 4;
    public const int Girdle = 5;
    public const int Shoes = 6;
    public const int Leggings = 7;
    public const int Ring1 = 8;
    public const int Ring2 = 9;
    public const int Weapon = 10;
    public const int Shield = 11;
    public const int Stylish = 12;

    private const string Empty = "[]";

    public static string ClearSlot(string equipment, byte profession, int slot)
    {
        return SetSlot(equipment, profession, slot, Empty);
    }

    public static string SetSlot(string equipment, byte profession, int slot, string value)
    {
        if (slot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Equipment slot cannot be negative.");
        }

        var slots = Normalize(equipment, profession);
        while (slots.Count <= slot)
        {
            slots.Add(Empty);
        }

        slots[slot] = value;
        return string.Join('#', slots) + "#";
    }

    public static string GetEntry(string equipment, byte profession, int slot)
    {
        var slots = Normalize(equipment, profession);
        if (slot < 0 || slot >= slots.Count)
        {
            return Empty;
        }

        return string.IsNullOrWhiteSpace(slots[slot]) ? Empty : slots[slot];
    }

    public static uint GetItemId(string equipment, byte profession, int slot)
    {
        return GetItem(equipment, profession, slot).Id;
    }

    public static CompactItemEntry GetItem(string equipment, byte profession, int slot)
    {
        return CompactItemEntry.Parse(GetEntry(equipment, profession, slot));
    }

    public static bool IsEquipmentSlot(int slot)
    {
        return slot is >= Head and <= Stylish;
    }

    public static int ResolveSlotForItem(uint itemId, int requestedSlot)
    {
        if (itemId is >= 1000 and < 2000)
        {
            return Weapon;
        }

        if (itemId is >= 2000 and < 2100)
        {
            return Shield;
        }

        if (itemId is >= 2100 and < 2300)
        {
            return Armor;
        }

        if (itemId is >= 2300 and < 2600)
        {
            return Head;
        }

        if (itemId is >= 2600 and < 2700)
        {
            return Cuff;
        }

        if (itemId is >= 2700 and < 2800)
        {
            return Leggings;
        }

        if (itemId is >= 2800 and < 2900)
        {
            return Glove;
        }

        if (itemId is >= 2900 and < 3000)
        {
            return Shoes;
        }

        if (itemId is >= 3000 and < 3100)
        {
            return Girdle;
        }

        if (itemId is >= 3100 and < 3200)
        {
            return Amulet;
        }

        if (itemId is >= 3200 and < 3300)
        {
            return requestedSlot is Ring1 or Ring2 ? requestedSlot : Ring1;
        }

        if (itemId is >= 8000 and < 9000)
        {
            return Stylish;
        }

        return requestedSlot >= 0 ? requestedSlot : Weapon;
    }

    public static int ResolveSlotForItem(
        uint itemId,
        int requestedSlot,
        string equipment,
        byte profession,
        int defaultSlot)
    {
        if (itemId is >= 3200 and < 3300)
        {
            if (requestedSlot is Ring1 or Ring2)
            {
                return requestedSlot;
            }

            if (GetItemId(equipment, profession, Ring1) == 0)
            {
                return Ring1;
            }

            if (GetItemId(equipment, profession, Ring2) == 0)
            {
                return Ring2;
            }

            return defaultSlot is Ring1 or Ring2 ? defaultSlot : Ring1;
        }

        return IsEquipmentSlot(defaultSlot)
            ? defaultSlot
            : ResolveSlotForItem(itemId, requestedSlot);
    }

    private static List<string> Normalize(string equipment, byte profession)
    {
        var source = string.IsNullOrWhiteSpace(equipment)
            ? GameDefaults.DefaultEquipment(profession)
            : equipment;

        var slots = source.Split('#', StringSplitOptions.None).ToList();
        if (slots.Count > 0 && slots[^1].Length == 0)
        {
            slots.RemoveAt(slots.Count - 1);
        }

        return slots;
    }

}

internal static class KitBagSlots
{
    private const string Empty = "[]";

    public static string ClearSlot(string kitBag, int slot)
    {
        return SetSlot(kitBag, slot, Empty);
    }

    public static string GetEntry(string kitBag, int slot)
    {
        var slots = Normalize(kitBag);
        if (slot < 0 || slot >= slots.Count)
        {
            return Empty;
        }

        return string.IsNullOrWhiteSpace(slots[slot]) ? Empty : slots[slot];
    }

    public static uint GetItemId(string kitBag, int slot)
    {
        return GetItem(kitBag, slot).Id;
    }

    public static CompactItemEntry GetItem(string kitBag, int slot)
    {
        return CompactItemEntry.Parse(GetEntry(kitBag, slot));
    }

    public static string SetSlot(string kitBag, int slot, string value)
    {
        if (slot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Kit bag slot cannot be negative.");
        }

        var slots = Normalize(kitBag);
        while (slots.Count <= slot)
        {
            slots.Add(Empty);
        }

        slots[slot] = string.IsNullOrWhiteSpace(value) ? Empty : value;
        return string.Join('#', slots) + "#";
    }

    private static List<string> Normalize(string kitBag)
    {
        var source = string.IsNullOrWhiteSpace(kitBag)
            ? GameDefaults.DefaultKitBag
            : kitBag;

        var slots = source.Split('#', StringSplitOptions.None).ToList();
        if (slots.Count > 0 && slots[^1].Length == 0)
        {
            slots.RemoveAt(slots.Count - 1);
        }

        return slots;
    }

}
