using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static class HolyStoneEquipmentEligibility
{
    public static bool IsNormalCharacterGear(
        IItemTemplateCatalog templates,
        uint itemId)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return templates.TryGet(itemId, out var template) &&
            EquipmentSlots.IsEquipmentKind(template.Kind) &&
            template.EquipmentSlot is
                >= EquipmentSlots.Head and <= EquipmentSlots.Shield;
    }

    public static bool IsWeapon(
        IItemTemplateCatalog templates,
        uint itemId)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return templates.TryGet(itemId, out var template) &&
            template.EquipmentSlot == EquipmentSlots.Weapon &&
            string.Equals(
                template.Kind,
                "weapon",
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCompatibleWithHolyStone(
        int equipmentSlot,
        uint holyStoneItemId) =>
        HolyStoneAffinityCatalog.IsCompatibleWithEquipmentSlot(
            equipmentSlot,
            holyStoneItemId);
}
