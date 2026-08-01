namespace Godswar.Server.State;

internal readonly record struct EquipmentEligibilityResult(bool Allowed, string Reason)
{
    public static EquipmentEligibilityResult Accept() => new(true, string.Empty);

    public static EquipmentEligibilityResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Shared, server-authoritative equipment checks.  Client drag/drop validation
/// is treated only as presentation; both persistence providers call this guard
/// before changing ownership locations.
/// </summary>
internal static class EquipmentEligibility
{
    public static EquipmentEligibilityResult ValidateEquip(
        GameplayItemContent content,
        byte profession,
        int characterLevel,
        string equipment,
        uint itemId,
        int equipmentSlot) =>
        ValidateEquip(
            content,
            profession,
            characterLevel,
            itemId,
            equipmentSlot,
            slot => EquipmentSlots.GetItemId(
                equipment,
                profession,
                slot));

    public static EquipmentEligibilityResult ValidateEquip(
        GameplayItemContent content,
        byte profession,
        int characterLevel,
        uint itemId,
        int equipmentSlot,
        Func<int, uint> equippedItemAtSlot)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(equippedItemAtSlot);
        if (!content.Templates.TryGet(itemId, out var template) ||
            !EquipmentSlots.IsEquipmentKind(template.Kind))
        {
            return EquipmentEligibilityResult.Reject("The item is not equipment.");
        }

        if (template.EquipmentSlot != equipmentSlot &&
            !(template.Kind.Equals("ring", StringComparison.OrdinalIgnoreCase) &&
              equipmentSlot is EquipmentSlots.Ring1 or EquipmentSlots.Ring2))
        {
            return EquipmentEligibilityResult.Reject("The item does not belong in that equipment slot.");
        }

        if (template.ClassIds.Count > 0 &&
            !template.ClassIds.Contains((short)profession))
        {
            return EquipmentEligibilityResult.Reject("The item is for a different class.");
        }

        if (template.MinLevel is { } minimumLevel && characterLevel < minimumLevel)
        {
            return EquipmentEligibilityResult.Reject(
                $"Character level {minimumLevel} is required.");
        }

        if (template.MaxLevel is { } maximumLevel && characterLevel > maximumLevel)
        {
            return EquipmentEligibilityResult.Reject(
                $"The item supports characters only through level {maximumLevel}.");
        }

        if (template.Kind.Equals("mount", StringComparison.OrdinalIgnoreCase))
        {
            if (!content.Mounts.TryGetRideDefinition(itemId, out _))
            {
                return EquipmentEligibilityResult.Reject(
                    "This mount family is not enabled on the server yet.");
            }

            var mountLevel = template.MinLevel ?? 1;
            foreach (var mountGearSlot in MountGearSlots())
            {
                var gearId = equippedItemAtSlot(mountGearSlot);
                if (gearId != 0 &&
                    content.Templates.TryGet(gearId, out var gearTemplate) &&
                    (gearTemplate.MinLevel ?? 1) > mountLevel)
                {
                    return EquipmentEligibilityResult.Reject(
                        "The mount level is too low for the equipped mount gear.");
                }
            }
        }
        else if (IsMountGearKind(template.Kind))
        {
            var mountId =
                equippedItemAtSlot(EquipmentSlots.Mount);
            if (mountId == 0 ||
                !content.Templates.TryGet(mountId, out var mountTemplate) ||
                !mountTemplate.Kind.Equals("mount", StringComparison.OrdinalIgnoreCase))
            {
                return EquipmentEligibilityResult.Reject(
                    "Equip a mount before equipping mount gear.");
            }

            if ((mountTemplate.MinLevel ?? 1) < (template.MinLevel ?? 1))
            {
                return EquipmentEligibilityResult.Reject(
                    "The mount level is too low for this mount gear.");
            }
        }

        return EquipmentEligibilityResult.Accept();
    }

    public static EquipmentEligibilityResult ValidateUnequip(
        byte profession,
        string equipment,
        int equipmentSlot)
    {
        if (equipmentSlot != EquipmentSlots.Mount)
        {
            return EquipmentEligibilityResult.Accept();
        }

        foreach (var mountGearSlot in MountGearSlots())
        {
            if (EquipmentSlots.GetItemId(equipment, profession, mountGearSlot) != 0)
            {
                return EquipmentEligibilityResult.Reject(
                    "Remove every piece of mount gear before removing the mount.");
            }
        }

        return EquipmentEligibilityResult.Accept();
    }

    public static bool IsMountSlot(int slot) =>
        slot is >= EquipmentSlots.MountHead and <= EquipmentSlots.Mount;

    public static bool IsMountGearKind(string? kind) => kind is
        "mounthead" or
        "mountarmor" or
        "mountsoul" or
        "mountornament" or
        "mountamulet";

    private static IEnumerable<int> MountGearSlots()
    {
        yield return EquipmentSlots.MountHead;
        yield return EquipmentSlots.MountArmor;
        yield return EquipmentSlots.MountSoul;
        yield return EquipmentSlots.MountOrnament;
        yield return EquipmentSlots.MountAmulet;
    }
}
