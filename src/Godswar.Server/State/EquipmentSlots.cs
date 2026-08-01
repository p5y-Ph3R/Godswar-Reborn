using Godswar.Server.Application.Items;

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
    public const int MountHead = 15;
    public const int MountArmor = 16;
    public const int MountSoul = 17;
    public const int MountOrnament = 18;
    public const int MountAmulet = 19;
    public const int Mount = 20;

    private const string Empty = "[]";

    private static readonly HashSet<string> EquipmentKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "head",
        "amulet",
        "glove",
        "armor",
        "cloth",
        "cuff",
        "girdle",
        "shoes",
        "leggins",
        "ring",
        "weapon",
        "shield",
        "stylish",
        "mounthead",
        "mountarmor",
        "mountsoul",
        "mountornament",
        "mountamulet",
        "mount"
    };

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
        return slot is >= Head and <= Mount;
    }

    public static bool IsEquipmentKind(string? kind)
    {
        return kind is not null && EquipmentKinds.Contains(kind);
    }

    public static bool TryGetAuthoritativeSlot(
        IItemTemplateCatalog templates,
        uint itemId,
        out int slot)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.TryGet(itemId, out var template) &&
            IsEquipmentKind(template.Kind) &&
            IsEquipmentSlot(template.EquipmentSlot))
        {
            slot = template.EquipmentSlot;
            return true;
        }

        slot = -1;
        return false;
    }

    public static int ResolveSlotForItem(
        IItemTemplateCatalog templates,
        uint itemId,
        int requestedSlot)
    {
        if (!TryGetAuthoritativeSlot(templates, itemId, out var slot))
        {
            return -1;
        }

        if (requestedSlot < 0)
        {
            return slot;
        }

        if (slot is Ring1 or Ring2)
        {
            return requestedSlot is Ring1 or Ring2 ? requestedSlot : -1;
        }

        return requestedSlot == slot ? slot : -1;
    }

    public static int ResolveSlotForItem(
        uint itemId,
        int requestedSlot,
        string equipment,
        byte profession,
        int defaultSlot)
    {
        if (requestedSlot >= 0)
        {
            if (defaultSlot is Ring1 or Ring2)
            {
                return requestedSlot is Ring1 or Ring2 ? requestedSlot : -1;
            }

            return requestedSlot == defaultSlot ? defaultSlot : -1;
        }

        if (defaultSlot is Ring1 or Ring2)
        {
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

        return IsEquipmentSlot(defaultSlot) ? defaultSlot : -1;
    }

    public static int ResolveSlotForItem(
        IItemTemplateCatalog templates,
        uint itemId,
        int requestedSlot,
        string equipment,
        byte profession,
        int defaultSlot)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (!TryGetAuthoritativeSlot(
                templates,
                itemId,
                out var authoritativeSlot) ||
            (authoritativeSlot != defaultSlot &&
             !(authoritativeSlot is Ring1 or Ring2 &&
               defaultSlot is Ring1 or Ring2)))
        {
            return -1;
        }

        return ResolveSlotForItem(
            itemId,
            requestedSlot,
            equipment,
            profession,
            authoritativeSlot);
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
            ? GameDefaults.EmptyKitBag
            : kitBag;

        var slots = source.Split('#', StringSplitOptions.None).ToList();
        if (slots.Count > 0 && slots[^1].Length == 0)
        {
            slots.RemoveAt(slots.Count - 1);
        }

        return slots;
    }

}
