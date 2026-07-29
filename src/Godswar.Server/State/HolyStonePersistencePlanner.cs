namespace Godswar.Server.State;

internal readonly record struct HolyStoneSlotMutation(
    bool IsKitBag,
    int Slot,
    CompactItemEntry Before,
    CompactItemEntry After);

internal sealed record HolyStonePersistencePlan(
    string UpdatedEquipment,
    string UpdatedKitBag,
    string Summary,
    IReadOnlyList<HolyStoneSlotMutation> Mutations);

internal static class HolyStonePersistencePlanner
{
    private const int EquipmentSlotCount = 24;
    private const int KitBagSlotCount = 96;

    public static bool TryCreate(
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneOperation operation,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        out HolyStonePersistencePlan? plan,
        out string summary)
    {
        return TryCreate(
            equipment,
            kitBag,
            profession,
            operation,
            HolyStoneTargetMode.LegacyFallback,
            targetKitBagSlot,
            socketIndex,
            stoneKitBagSlot,
            destinationKitBagSlot,
            out plan,
            out summary);
    }

    public static bool TryCreate(
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        out HolyStonePersistencePlan? plan,
        out string summary)
    {
        plan = null;
        if (!HolyStoneItemMutator.TryApply(
                equipment,
                kitBag,
                profession,
                operation,
                targetMode,
                targetKitBagSlot,
                socketIndex,
                stoneKitBagSlot,
                destinationKitBagSlot,
                out var updatedEquipment,
                out var updatedKitBag,
                out summary))
        {
            return false;
        }

        var mutations = new List<HolyStoneSlotMutation>(3);
        AddMutations(
            mutations,
            isKitBag: false,
            EquipmentSlotCount,
            slot => EquipmentSlots.GetItem(equipment, profession, slot),
            slot => EquipmentSlots.GetItem(updatedEquipment, profession, slot));
        AddMutations(
            mutations,
            isKitBag: true,
            KitBagSlotCount,
            slot => KitBagSlots.GetItem(kitBag, slot),
            slot => KitBagSlots.GetItem(updatedKitBag, slot));

        plan = new HolyStonePersistencePlan(
            updatedEquipment,
            updatedKitBag,
            summary,
            mutations);
        return true;
    }

    private static void AddMutations(
        List<HolyStoneSlotMutation> mutations,
        bool isKitBag,
        int slotCount,
        Func<int, CompactItemEntry> readBefore,
        Func<int, CompactItemEntry> readAfter)
    {
        for (var slot = 0; slot < slotCount; slot++)
        {
            var before = readBefore(slot);
            var after = readAfter(slot);
            if (before == after)
            {
                continue;
            }

            mutations.Add(new HolyStoneSlotMutation(isKitBag, slot, before, after));
        }
    }
}
