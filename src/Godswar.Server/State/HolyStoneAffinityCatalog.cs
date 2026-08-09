namespace Godswar.Server.State;

internal enum HolyStoneAffinity : byte
{
    Heated = 1,
    Cooled = 2,
    Zephyr = 3
}

internal readonly record struct HolyStoneAffinityDefinition(
    uint ItemId,
    HolyStoneAffinity Affinity,
    IReadOnlyList<int> AllowedEquipmentSlots);

/// <summary>
/// Owns the relationship between a Holy Stone item, its Spirit affinity, and
/// the equipment slots that may receive it. Unknown affinities and item IDs
/// fail closed so a future stone cannot silently inherit Cooled behavior.
/// </summary>
internal static class HolyStoneAffinityCatalog
{
    private static readonly HolyStoneAffinityDefinition[] Definitions =
    [
        new(
            HolyStoneUpgradePolicy.HeatedHolyStoneItemId,
            HolyStoneAffinity.Heated,
            [
                EquipmentSlots.Head,
                EquipmentSlots.Glove,
                EquipmentSlots.Ring1,
                EquipmentSlots.Ring2,
                EquipmentSlots.Weapon
            ]),
        new(
            HolyStoneUpgradePolicy.CooledHolyStoneItemId,
            HolyStoneAffinity.Cooled,
            [
                EquipmentSlots.Amulet,
                EquipmentSlots.Armor,
                EquipmentSlots.Cuff,
                EquipmentSlots.Girdle,
                EquipmentSlots.Shoes,
                EquipmentSlots.Leggings,
                EquipmentSlots.Shield
            ]),
        new(
            HolyStoneUpgradePolicy.ZephyrHolyStoneItemId,
            HolyStoneAffinity.Zephyr,
            [
                EquipmentSlots.MountHead,
                EquipmentSlots.MountArmor,
                EquipmentSlots.MountSoul,
                EquipmentSlots.MountOrnament,
                EquipmentSlots.MountAmulet
            ])
    ];

    public static IReadOnlyList<HolyStoneAffinityDefinition> All { get; } =
        Array.AsReadOnly(Definitions);

    public static bool TryGetByItemId(
        uint itemId,
        out HolyStoneAffinityDefinition definition)
    {
        foreach (var candidate in Definitions)
        {
            if (candidate.ItemId == itemId)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    public static bool TryGetItemId(
        HolyStoneAffinity affinity,
        out uint itemId)
    {
        foreach (var candidate in Definitions)
        {
            if (candidate.Affinity == affinity)
            {
                itemId = candidate.ItemId;
                return true;
            }
        }

        itemId = 0;
        return false;
    }

    public static bool IsCompatibleWithEquipmentSlot(
        int equipmentSlot,
        uint holyStoneItemId) =>
        TryGetByItemId(holyStoneItemId, out var definition) &&
        definition.AllowedEquipmentSlots.Contains(equipmentSlot);
}
