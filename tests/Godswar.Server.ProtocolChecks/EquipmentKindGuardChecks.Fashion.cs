using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentKindGuardChecks
{
    private const uint PermanentFashionItemId = 8068;

    private static void CheckFashionSlotConsistency()
    {
        Check.True(
            EquipmentSlots.TryGetAuthoritativeSlot(
                TestItemContent.Catalog,
                PermanentFashionItemId,
                out var authoritativeSlot),
            "permanent Christmas fashion has an authoritative slot");
        Check.Equal(
            EquipmentSlots.Stylish,
            authoritativeSlot,
            "permanent Christmas fashion uses native slot 12");
        Check.Equal(
            EquipmentSlots.Stylish,
            EquipmentSlots.ResolveSlotForItem(
                TestItemContent.Catalog,
                PermanentFashionItemId,
                requestedSlot: -1),
            "right-click fashion equip infers native slot 12");
        Check.Equal(
            EquipmentSlots.Stylish,
            EquipmentSlots.ResolveSlotForItem(
                TestItemContent.Catalog,
                PermanentFashionItemId,
                EquipmentSlots.Stylish),
            "drag fashion equip accepts native slot 12");
        Check.Equal(
            -1,
            EquipmentSlots.ResolveSlotForItem(
                TestItemContent.Catalog,
                PermanentFashionItemId,
                requestedSlot: 13),
            "fashion equip rejects reserved legacy slot 13");

        for (byte profession = 0; profession <= 3; profession++)
        {
            var equipment = GameDefaults.DefaultEquipment(profession);
            Check.Equal(
                8040u,
                EquipmentSlots.GetItemId(
                    equipment,
                    profession,
                    EquipmentSlots.Stylish),
                $"profession {profession} starter fashion uses slot 12");
            Check.Equal(
                0u,
                EquipmentSlots.GetItemId(equipment, profession, 13),
                $"profession {profession} leaves reserved slot 13 empty");
        }
    }
}
